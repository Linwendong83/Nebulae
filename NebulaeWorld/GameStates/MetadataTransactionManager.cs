using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeModel.Logger;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Combat;
using NebulaeModel.Packets.GameStates;
using NebulaeModel.Utils;

namespace NebulaeWorld.GameStates;

/// <summary>Personal wallet payments and authoritative world effects use durable, replayable receipts.</summary>
internal sealed class MetadataTransactionManager : IDisposable
{
    // Reserved negative feature values are serialized inside the vanilla world snapshot itself.
    // This keeps effect application and its watermark in the same save, unlike a sidecar watermark.
    private const int WatermarkLow = -1708469001;
    private const int WatermarkHigh = -1708469002;
    private readonly Dictionary<string, MetadataTransaction> serverTransactions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MetadataTransaction> localTransactions = new(StringComparer.Ordinal);
    private readonly Queue<MetadataTransaction> requests = new();
    private MetadataTransaction pending;
    private string serverPath;
    private string localPath;
    private long sequence;
    private long lastPoll;
    private bool initialized;
    private bool faulted;
    public bool ApplyingWorld { get; private set; }
    public bool ApplyingPersonal { get; private set; }
    public string ActiveOperationId { get; private set; } = "";
    public int TrashOrdinal { get; set; }
    public bool Busy => pending != null || requests.Count > 0 || faulted;
    public bool SuppressVanillaDebit => ApplyingWorld || ApplyingPersonal;
    public bool OwnsCommittedOperation(string id, string owner) => serverTransactions.TryGetValue(id ?? "", out var transaction) &&
        transaction.Owner == owner && transaction.State is MetadataTransactionState.Committed or MetadataTransactionState.Applied;
    public bool OwnsCommittedRespawn(string id, string owner, int deathCount) =>
        serverTransactions.TryGetValue(id ?? "", out var tx) && tx.Owner == owner && tx.Operation == MetadataOperation.Respawn &&
        tx.Stamp == deathCount && tx.State is MetadataTransactionState.Committed or MetadataTransactionState.Applied;

    public void BeginDropOperation(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("Redeploy has no committed transaction");
        ActiveOperationId = id;
        TrashOrdinal = 0;
    }

    public void EndDropOperation() => ActiveOperationId = "";

    public void Initialize()
    {
        if (initialized || !Multiplayer.Session.IsGameLoaded) return;
        initialized = true;
        if (Multiplayer.Session.IsServer)
        {
            serverPath = Path.Combine(LegacyDataMigration.DataDirectory(GameConfig.gameSaveFolder), SaveManager.WorldId, "transactions.bin");
            Load(serverPath, serverTransactions);
            sequence = serverTransactions.Values.Select(x => x.Sequence).DefaultIfEmpty().Max();
            foreach (var tx in serverTransactions.Values.OrderBy(x => x.Sequence))
            {
                if (tx.State is MetadataTransactionState.Committed or MetadataTransactionState.Applied)
                    ApplyWorld(tx);
            }
        }
        if (Multiplayer.Session.IsDedicated) return;
        localPath = Path.Combine(LegacyDataMigration.DataDirectory(GameConfig.propertyFolder), AtomicFile.IdentityFileName(MetadataManager.LocalIdentity),
            SaveManager.WorldId + ".transactions");
        Load(localPath, localTransactions);
        foreach (var tx in localTransactions.Values)
        {
            if (tx.State is MetadataTransactionState.Applied or MetadataTransactionState.Rejected) continue;
            requests.Enqueue(tx);
        }
    }

    public void Request(MetadataOperation operation, int target = 0, int count = 0, int parameter = 0)
    {
        Initialize();
        if (faulted || Multiplayer.Session.IsDedicated || !Multiplayer.Session.IsGameLoaded) return;
        // UI double clicks must not queue a second purchase while the first is awaiting confirmation.
        if (Busy) { UIRealtimeTip.Popup("Waiting for the previous metadata operation".Translate()); return; }
        var tx = new MetadataTransaction
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = operation,
            Target = target,
            Count = count,
            Parameter = parameter,
            Owner = MetadataManager.LocalIdentity,
            State = MetadataTransactionState.Requested
        };
        localTransactions.Add(tx.Id, tx);
        PersistLocal();
        requests.Enqueue(tx);
        Poll();
    }

    public void GameTick()
    {
        if (!Multiplayer.Session.IsGameLoaded || faulted) return;
        try
        {
            Initialize();
            var now = DateTime.UtcNow.Ticks;
            if (now - lastPoll < TimeSpan.TicksPerSecond * 2) return;
            lastPoll = now;
            Poll();
        }
        catch (Exception e)
        {
            faulted = true;
            Log.Error("Metadata transaction recovery is suspended after a persistence failure.", e);
        }
    }

    private void Poll()
    {
        if (Multiplayer.Session.IsDedicated || faulted) return;
        if (pending == null && requests.Count > 0) pending = requests.Dequeue();
        if (pending == null) return;
        if (pending.State == MetadataTransactionState.DebitPending) FinishDebit(pending);
        var message = pending.State switch
        {
            MetadataTransactionState.Requested => MetadataMessage.Request,
            MetadataTransactionState.Debited => MetadataMessage.Commit,
            _ => MetadataMessage.Query
        };
        SendToServer(message, pending);
    }

    private void SendToServer(MetadataMessage message, MetadataTransaction tx)
    {
        var packet = Packet(message, tx);
        if (Multiplayer.Session.IsServer)
            HandleServer(packet, (PlayerData)Multiplayer.Session.LocalPlayer.Data, Receive);
        else Multiplayer.Session.Network.SendPacket(packet);
    }

    public void HandleServer(MetadataOperationPacket packet, PlayerData player, Action<MetadataOperationPacket> reply)
    {
        Initialize();
        if (faulted || packet.WorldId != SaveManager.WorldId || string.IsNullOrEmpty(player.PersistentId)) return;
        var input = MetadataTransaction.ImportWire(packet.Transaction);
        if (!serverTransactions.TryGetValue(input.Id, out var tx))
        {
            // Queries/commits never invent a payment that the host has not quoted.
            tx = new MetadataTransaction
            {
                Id = input.Id,
                Owner = player.PersistentId,
                Operation = input.Operation,
                Target = input.Target,
                Count = input.Count,
                Parameter = input.Parameter
            };
            serverTransactions.Add(tx.Id, tx);
            if (packet.Message != MetadataMessage.Request) Reject(tx, "Unknown or cancelled metadata transaction");
            else
            {
                try
                {
                    if (serverTransactions.Values.Any(x => x.Id != tx.Id && x.State == MetadataTransactionState.Quoted &&
                        x.Expires > DateTime.UtcNow.Ticks && (x.Owner == tx.Owner ||
                        (tx.Operation == MetadataOperation.BuyTech && x.Operation == tx.Operation && x.Target == tx.Target))))
                        throw new InvalidOperationException("Another metadata operation is pending");
                    Quote(tx, player);
                    tx.State = MetadataTransactionState.Quoted;
                    tx.Expires = DateTime.UtcNow.AddMinutes(2).Ticks;
                }
                catch (InvalidOperationException error) { Reject(tx, error.Message); }
            }
            PersistServer();
        }
        if (tx.Owner != player.PersistentId) return;
        if (packet.Message == MetadataMessage.Cancel && tx.State == MetadataTransactionState.Quoted)
        {
            Reject(tx, "Metadata operation cancelled");
            PersistServer();
        }
        if (packet.Message == MetadataMessage.Commit && tx.State == MetadataTransactionState.Quoted)
        {
            if (DateTime.UtcNow.Ticks > tx.Expires || tx.Stamp != GetStamp(tx, player))
                Reject(tx, "The game state changed; the metadata payment has been refunded");
            else
            {
                // Research may progress while a quote is in flight. Charge the current lower price;
                // a level change or a higher price invalidates the quote instead of charging extra.
                if (tx.Operation == MetadataOperation.BuyTech)
                {
                    var current = new MetadataTransaction { Operation = tx.Operation, Target = tx.Target };
                    Quote(current, player);
                    if (current.Cost.Where((value, i) => value > tx.Cost[i]).Any())
                        Reject(tx, "The technology price changed; the metadata payment has been refunded");
                    else tx.Cost = current.Cost;
                }
                if (tx.State != MetadataTransactionState.Rejected)
                {
                    tx.State = MetadataTransactionState.Committed;
                    tx.Sequence = ++sequence;
                    if (tx.Operation == MetadataOperation.Truce)
                        tx.EffectValue += GameMain.gameTick + GameMain.history.dfTruceTimer;
                }
            }
            PersistServer(); // A commit is durable before any world effect is executed.
        }
        if (tx.State is MetadataTransactionState.Committed or MetadataTransactionState.Applied) ApplyWorld(tx);
        if (packet.Message == MetadataMessage.Acknowledge &&
            tx.State is MetadataTransactionState.Committed or MetadataTransactionState.Applied)
        {
            if (input.AfterPlayer.Length > 0 && tx.AfterPlayer.Length == 0)
            {
                tx.AfterPlayer = input.AfterPlayer;
                var restored = ReadPlayer(tx.AfterPlayer);
                if (restored != null && ShouldRestorePlayer(tx, restored, player))
                {
                    if (player.CombatAuthoritative) player.TryApplyPersonalSnapshot(restored);
                    else { player.Mecha = restored.Mecha; player.Life = restored.Life; }
                }
            }
            tx.State = MetadataTransactionState.Applied;
            PersistServer();
            reply(Packet(MetadataMessage.Acknowledge, tx));
            return;
        }
        reply(Packet(tx.State == MetadataTransactionState.Quoted ? MetadataMessage.Quote : MetadataMessage.Result, tx));
    }

    private static void Reject(MetadataTransaction tx, string error)
    {
        tx.State = MetadataTransactionState.Rejected;
        tx.Error = error;
    }

    private static long GetStamp(MetadataTransaction tx, PlayerData player)
    {
        var history = GameMain.history;
        switch (tx.Operation)
        {
            case MetadataOperation.BuyTech:
                var state = history.TechState(tx.Target);
                return (long)state.curLevel * 2 + (state.hashUploaded >= state.hashNeeded ? 1 : 0);
            case MetadataOperation.Respawn:
                return player.Life.IsAlive || player.Life.RespawnMode != 0 ? -1 : player.Life.DeathCount;
            case MetadataOperation.IncreaseAggressiveness:
            case MetadataOperation.DecreaseAggressiveness:
            case MetadataOperation.Truce:
            case MetadataOperation.WithdrawTruce:
                return (long)history.combatSettings.aggressiveLevel ^ ReadWatermark() * 397;
            default:
                return player.Life.IsAlive ? 1 : 0;
        }
    }

    private static void Quote(MetadataTransaction tx, PlayerData player)
    {
        if (player.PlayerId == Multiplayer.Session.LocalPlayer.Id)
            player.Life = PlayerLifeData.Capture(GameMain.mainPlayer);
        tx.Stamp = GetStamp(tx, player);
        var history = GameMain.history;
        switch (tx.Operation)
        {
            case MetadataOperation.BuyTech:
                var proto = LDB.techs.Select(tx.Target);
                if (proto == null || !history.HasPreTechUnlocked(tx.Target)) throw new InvalidOperationException("Technology prerequisites not met");
                var tech = history.TechState(tx.Target);
                if (tech.hashUploaded >= tech.hashNeeded) throw new InvalidOperationException("Technology already researched");
                tx.Parameter = tech.curLevel;
                if (proto.PropertyOverrideItemArray != null)
                {
                    foreach (var item in proto.PropertyOverrideItemArray)
                        AddCost(tx, item.id, UnityEngine.Mathf.CeilToInt(item.count *
                            (1f - UnityEngine.Mathf.Clamp01((float)((double)tech.hashUploaded / tech.hashNeeded)))));
                }
                else
                {
                    for (var i = 0; i < proto.itemArray.Length; i++)
                        AddCost(tx, proto.itemArray[i].ID, proto.ItemPoints[i] * (tech.hashNeeded - tech.hashUploaded) / 3600);
                }
                break;
            case MetadataOperation.Matrix:
            case MetadataOperation.VariousMatrices:
            case MetadataOperation.DarkFogItems:
                if (!player.Life.IsAlive || tx.Count <= 0 || tx.Count > 2000)
                    throw new InvalidOperationException("Invalid metadata conversion");
                if (tx.Operation == MetadataOperation.Matrix)
                {
                    AddCost(tx, tx.Target, tx.Count);
                    tx.Items = [tx.Target]; tx.Counts = [tx.Count];
                }
                else if (tx.Operation == MetadataOperation.VariousMatrices)
                {
                    tx.Cost[5] = tx.Count;
                    tx.Items = [6001, 6002, 6003, 6004, 6005];
                    tx.Counts = [tx.Count, tx.Count, tx.Count, tx.Count, tx.Count];
                }
                else
                {
                    var rate = tx.Target switch { 5201 => 20, 5206 => 60, 5202 or 5203 or 5204 or 5205 or 0 => 10, _ => 0 };
                    if (rate == 0) throw new InvalidOperationException("Invalid Dark Fog conversion");
                    tx.Items = tx.Target == 0 ? [5202, 5203, 5204] : [tx.Target];
                    tx.Counts = tx.Items.Select(_ => tx.Count * rate).ToArray();
                    tx.Cost[5] = tx.Count * tx.Items.Length;
                }
                break;
            case MetadataOperation.IncreaseAggressiveness:
            case MetadataOperation.DecreaseAggressiveness:
                var level = history.combatSettings.aggressiveLevel switch
                {
                    EAggressiveLevel.Dummy => 0,
                    EAggressiveLevel.Passive => 1,
                    EAggressiveLevel.Torpid => 2,
                    EAggressiveLevel.Normal => 3,
                    EAggressiveLevel.Sharp => 4,
                    EAggressiveLevel.Rampage => 5,
                    _ => -1
                };
                if (!GameMain.data.gameDesc.isCombatMode || level < 0 || level > 5 ||
                    (tx.Operation == MetadataOperation.IncreaseAggressiveness ? level == 5 : level == 0))
                    throw new InvalidOperationException("Cannot change Dark Fog aggressiveness");
                tx.Cost[5] = tx.Operation == MetadataOperation.IncreaseAggressiveness ?
                    new[] { 15, 15, 30, 60, 60, 0 }[level] : new[] { 0, 60, 120, 180, 180, 180 }[level];
                break;
            case MetadataOperation.Truce:
                var index = Array.IndexOf(PropertySystem.matrixIds, tx.Target);
                if (index < 0 || tx.Count < 6 || tx.Count > 600 || !GameMain.data.gameDesc.isCombatMode ||
                    history.combatSettings.aggressiveLevel <= EAggressiveLevel.Passive)
                    throw new InvalidOperationException("Invalid truce request");
                tx.Cost[index] = tx.Count;
                var coefficient = history.combatSettings.aggressiveLevel switch
                { EAggressiveLevel.Torpid => 0.5f, EAggressiveLevel.Sharp => 1.5f, EAggressiveLevel.Rampage => 2f, _ => 1f };
                tx.EffectValue = (long)(tx.Count / 6 * new[] { 1, 2, 5, 15, 30, 60 }[index] * 3600 / coefficient);
                break;
            case MetadataOperation.WithdrawTruce:
                if (history.dfTruceTimer <= 0) throw new InvalidOperationException("No active truce");
                break;
            case MetadataOperation.Respawn:
                if (tx.Stamp < 0 || tx.Target is not (2 or 3)) throw new InvalidOperationException("Player is not awaiting respawn");
                var costs = (int[][][])AccessTools.Field(typeof(PlayerAction_Death), "respawnCosts").GetValue(null);
                var options = costs[Math.Max(0, Math.Min(costs.Length - 1, player.Life.DeathCount - 1))];
                if (tx.Parameter < 0 || tx.Parameter >= options.Length) throw new InvalidOperationException("Invalid respawn option");
                for (var i = 0; i < 6; i++) tx.Cost[i] = options[tx.Parameter][i] * (tx.Target == 2 ? 2 : 1);
                break;
        }
        if (GameMain.data.gameDesc.isSandboxMode) Array.Clear(tx.Cost, 0, tx.Cost.Length);
    }

    private static void AddCost(MetadataTransaction tx, int item, long amount)
    {
        var index = Array.IndexOf(PropertySystem.matrixIds, item);
        if (index < 0 || amount < 0 || amount > int.MaxValue) throw new InvalidOperationException("Invalid metadata price");
        tx.Cost[index] = checked(tx.Cost[index] + (int)amount);
    }

    public void Receive(MetadataOperationPacket packet)
    {
        if (packet.WorldId != SaveManager.WorldId || faulted) return;
        var tx = MetadataTransaction.ImportWire(packet.Transaction);
        if (!localTransactions.TryGetValue(tx.Id, out var local)) return;
        if (packet.Message == MetadataMessage.Acknowledge)
        {
            local.State = MetadataTransactionState.Applied;
            PersistLocal();
            if (pending?.Id == local.Id) pending = null;
            return;
        }
        if (packet.Message == MetadataMessage.Quote)
        {
            if (local.State is MetadataTransactionState.Debited or MetadataTransactionState.DebitPending)
            {
                if (local.State == MetadataTransactionState.DebitPending) FinishDebit(local);
                SendToServer(MetadataMessage.Commit, local);
                return;
            }
            if (local.State != MetadataTransactionState.Requested) return;
            var properties = DSPGame.propertySystem;
            var key = GameMain.data.GetClusterSeedKey();
            var balances = new long[6];
            for (var i = 0; i < 6; i++)
            {
                var item = PropertySystem.matrixIds[i];
                // Communicator and respawn use total property in the vanilla game.
                var available = tx.Operation >= MetadataOperation.IncreaseAggressiveness ?
                    properties.GetItemAvaliablePropertyForRespawn(key, item) : properties.GetItemAvaliableProperty(key, item);
                if (tx.Cost[i] > available)
                {
                    SendToServer(MetadataMessage.Cancel, local);
                    return;
                }
                tx.Before[i] = properties.GetItemConsumption(key, item);
                balances[i] = available;
            }
            try { tx.After = MetadataPayment.Prepare(tx.Before, tx.Cost, balances); }
            catch (InvalidOperationException) { SendToServer(MetadataMessage.Cancel, local); return; }
            tx.State = MetadataTransactionState.DebitPending;
            localTransactions[tx.Id] = pending = tx;
            PersistLocal();
            FinishDebit(tx);
            SendToServer(MetadataMessage.Commit, tx);
            return;
        }
        if (packet.Message != MetadataMessage.Result) return;
        if (tx.State == MetadataTransactionState.Rejected)
        {
            if (local.State is MetadataTransactionState.Debited or MetadataTransactionState.DebitPending)
            {
                for (var i = 0; i < 6; i++)
                    DSPGame.propertySystem.SetItemConsumption(GameMain.data.GetClusterSeedKey(), PropertySystem.matrixIds[i], local.Before[i]);
                PropertyAccountStore.Save();
            }
            local.State = MetadataTransactionState.Rejected;
            PersistLocal();
            if (pending?.Id == local.Id) pending = null;
            UIRealtimeTip.Popup(tx.Error.Translate());
            return;
        }
        if (tx.State is not (MetadataTransactionState.Committed or MetadataTransactionState.Applied)) return;
        if (local.State == MetadataTransactionState.Applied) return;
        var resume = local.State == MetadataTransactionState.Committed;
        if (local.State != MetadataTransactionState.Committed)
        {
            tx.Before = local.Before; tx.After = local.After;
            tx.State = MetadataTransactionState.Committed;
            tx.BeforePlayer = CapturePlayer();
            localTransactions[tx.Id] = pending = local = tx;
            PersistLocal();
        }
        var finalDebit = local.Before.Select((value, i) => checked(value + local.Cost[i])).ToArray();
        var currentDebit = PropertySystem.matrixIds.Select(item => DSPGame.propertySystem.GetItemConsumption(GameMain.data.GetClusterSeedKey(), item)).ToArray();
        MetadataPayment.RecoverDebit(currentDebit, local.After, finalDebit);
        if (!currentDebit.SequenceEqual(finalDebit))
        {
            for (var i = 0; i < 6; i++) DSPGame.propertySystem.SetItemConsumption(GameMain.data.GetClusterSeedKey(), PropertySystem.matrixIds[i], finalDebit[i]);
            PropertyAccountStore.Save();
        }
        ApplyPersonal(local, resume);
        SendToServer(MetadataMessage.Acknowledge, local);
    }

    private void FinishDebit(MetadataTransaction tx)
    {
        var key = GameMain.data.GetClusterSeedKey();
        var currentValues = PropertySystem.matrixIds.Select(item => DSPGame.propertySystem.GetItemConsumption(key, item)).ToArray();
        var after = MetadataPayment.RecoverDebit(currentValues, tx.Before, tx.After);
        for (var i = 0; i < 6; i++)
        {
            var item = PropertySystem.matrixIds[i];
            DSPGame.propertySystem.SetItemConsumption(key, item, after[i]);
        }
        PropertyAccountStore.Save();
        tx.State = MetadataTransactionState.Debited;
        PersistLocal();
    }

    private void ApplyWorld(MetadataTransaction tx)
    {
        if (tx.Sequence <= ReadWatermark()) return;
        ApplyingWorld = true;
        try
        {
            using (Multiplayer.Session.History.IsIncomingRequest.On())
            {
                var history = GameMain.history;
                switch (tx.Operation)
                {
                    case MetadataOperation.BuyTech:
                        var tech = history.TechState(tx.Target);
                        if (tech.curLevel == tx.Parameter && tech.hashUploaded < tech.hashNeeded) history.UnlockTechUnlimited(tx.Target, false);
                        break;
                    case MetadataOperation.IncreaseAggressiveness:
                        history.IncreaseDFAggressiveness(6006, 0);
                        Multiplayer.Session.Server.SendPacket(new CombatAggressivenessUpdatePacket(0, history.combatSettings.aggressiveness));
                        break;
                    case MetadataOperation.DecreaseAggressiveness:
                        history.DecreaseDFAggressiveness(6006, 0);
                        Multiplayer.Session.Server.SendPacket(new CombatAggressivenessUpdatePacket(0, history.combatSettings.aggressiveness));
                        break;
                    case MetadataOperation.Truce:
                        history.AddTruceTime(tx.EffectValue - GameMain.gameTick - history.dfTruceTimer);
                        Multiplayer.Session.Server.SendPacket(new CombatTruceUpdatePacket(0, tx.EffectValue));
                        break;
                    case MetadataOperation.WithdrawTruce:
                        history.AddTruceTime(-history.dfTruceTimer);
                        Multiplayer.Session.Server.SendPacket(new CombatTruceUpdatePacket(0, GameMain.gameTick));
                        break;
                }
                var ban = tx.Operation <= MetadataOperation.DarkFogItems;
                for (var i = 0; i < 6; i++) if (tx.Cost[i] > 0)
                        history.AddPropertyItemConsumption(PropertySystem.matrixIds[i], tx.Cost[i], ban);
                WriteWatermark(tx.Sequence);
                Multiplayer.Session.Server.SendPacket(new PropertyHistoryPacket
                {
                    Sequence = tx.Sequence,
                    BanAchievement = history.hasUsedPropertyBanAchievement,
                    Consumption = PropertySystem.matrixIds.Select(history.GetPropertyItemComsumption).ToArray()
                });
            }
        }
        finally { ApplyingWorld = false; }
    }

    private void ApplyPersonal(MetadataTransaction tx, bool resume)
    {
        if (tx.AfterPlayer.Length > 0)
        {
            var delivered = ReadPlayer(tx.AfterPlayer);
            var current = (PlayerData)Multiplayer.Session.LocalPlayer.Data;
            if (resume && delivered != null && ShouldRestorePlayer(tx, delivered, current))
            {
                RestorePlayer(tx.AfterPlayer);
                current.Life = delivered.Life;
            }
            return;
        }
        ApplyingPersonal = true;
        ActiveOperationId = tx.Id;
        TrashOrdinal = 0;
        try
        {
            // Restoring the pre-effect image makes an interrupted local delivery replayable.
            if (resume) RestorePlayer(tx.BeforePlayer);
            var player = GameMain.mainPlayer;
            if (tx.Operation == MetadataOperation.BuyTech)
                for (var i = 0; i < 6; i++) if (tx.Cost[i] > 0)
                    {
                        player.mecha.AddProductionStat(PropertySystem.matrixIds[i], tx.Cost[i], player.nearestFactory);
                        player.mecha.AddConsumptionStat(PropertySystem.matrixIds[i], tx.Cost[i], player.nearestFactory);
                    }
            for (var i = 0; i < tx.Items.Length; i++)
            {
                var added = player.TryAddItemToPackage(tx.Items[i], tx.Counts[i], 0, true);
                if (added > 0) UIItemup.Up(tx.Items[i], added);
                player.mecha.AddProductionStat(tx.Items[i], tx.Counts[i], player.nearestFactory);
            }
            if (tx.Operation == MetadataOperation.Respawn)
            {
                var data = (PlayerData)Multiplayer.Session.LocalPlayer.Data;
                data.Life.TransactionId = tx.Id;
                PlayerLifeData.CurrentTransactionId = tx.Id;
                PlayerLifeData.CurrentRedeployItemsDropped = false;
                player.controller.actionDeath.selectedRespawnOption = -1; // Already paid through the receipt.
                player.controller.actionDeath.Respawn(tx.Target);
            }
            tx.AfterPlayer = CapturePlayer();
            PersistLocal();
        }
        finally { ActiveOperationId = ""; ApplyingPersonal = false; }
    }

    public void RestoreServerPlayer(PlayerData player)
    {
        Initialize();
        foreach (var tx in serverTransactions.Values.Where(x => x.Owner == player.PersistentId && x.AfterPlayer.Length > 0 &&
                         x.State is MetadataTransactionState.Committed or MetadataTransactionState.Applied)
                     .OrderBy(x => x.Sequence))
        {
            var snapshot = ReadPlayer(tx.AfterPlayer);
            if (snapshot == null || !ShouldRestorePlayer(tx, snapshot, player)) continue;
            player.Mecha = snapshot.Mecha;
            player.Life = snapshot.Life;
        }
    }

    private static bool ShouldRestorePlayer(MetadataTransaction tx, PlayerData snapshot, PlayerData current)
    {
        // A respawn receipt captures the start of the animation (dead, zero HP), not its
        // completion. Even a higher receipt revision cannot undo that completed death cycle.
        return snapshot.Life.Revision > current.Life.Revision && snapshot.Life.DeathCount >= current.Life.DeathCount &&
               !(tx.Operation == MetadataOperation.Respawn && current.Life.IsAlive && tx.Stamp <= current.Life.DeathCount);
    }

    public static byte[] CapturePlayer()
    {
        var player = GameMain.mainPlayer;
        var data = (PlayerData)Multiplayer.Session.LocalPlayer.Data;
        data.LocalPlanetId = GameMain.localPlanet?.id ?? -1;
        data.LocalStarId = GameMain.localStar?.id ?? -1;
        data.LocalPlanetPosition = new NebulaeModel.DataStructures.Float3(player.position);
        data.UPosition = new NebulaeModel.DataStructures.Double3(player.uPosition.x, player.uPosition.y, player.uPosition.z);
        // The seat's Rotation mirrors what LocalPlayerMovement reports: the root transform's
        // world euler angles (the gravity-aligned frame), never the mecha's facing.
        data.Rotation = new NebulaeModel.DataStructures.Float3(player.transform.eulerAngles);
        data.Mecha = new MechaData(player);
        Multiplayer.Session.Research.OverlayCheckpoint((MechaData)data.Mecha);
        data.Life = PlayerLifeData.Capture(player, transactionId: data.Life.TransactionId);
        var writer = new NetDataWriter();
        var snapshot = (PlayerData)data.CreateCopyWithoutMechaData();
        snapshot.Mecha = data.Mecha;
        snapshot.Serialize(writer);
        return writer.CopyData();
    }

    internal static PlayerData ReadPlayer(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        try
        {
            var player = new PlayerData();
            var reader = new NetDataReader(bytes);
            player.Deserialize(reader);
            return reader.AvailableBytes == 0 ? player : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void RestorePlayer(byte[] bytes)
    {
        if (bytes.Length == 0) return;
        var snapshot = ReadPlayer(bytes);
        if (snapshot == null) return;
        snapshot.Mecha.UpdateMech(GameMain.mainPlayer);
        SimulatedWorld.FixPlayerAfterImport();
    }

    private static long ReadWatermark()
    {
        var values = GameMain.history.featureValues;
        values.TryGetValue(WatermarkLow, out var low);
        values.TryGetValue(WatermarkHigh, out var high);
        return ((long)high << 32) | (uint)low;
    }

    private static void WriteWatermark(long value)
    {
        GameMain.history.featureValues[WatermarkLow] = (int)value;
        GameMain.history.featureValues[WatermarkHigh] = (int)(value >> 32);
    }

    private static MetadataOperationPacket Packet(MetadataMessage message, MetadataTransaction transaction) =>
        new() { WorldId = SaveManager.WorldId, Message = message, Transaction = transaction.ExportWire() };

    private void PersistServer() => Save(serverPath, serverTransactions);
    private void PersistLocal() => Save(localPath, localTransactions);

    private static void Save(string path, Dictionary<string, MetadataTransaction> transactions)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1); writer.Write(transactions.Count);
        foreach (var tx in transactions.Values)
        {
            var bytes = tx.Export(); writer.Write(bytes.Length); writer.Write(bytes);
        }
        AtomicFile.Write(path, stream.ToArray());
    }

    private static void Load(string path, Dictionary<string, MetadataTransaction> transactions)
    {
        if (!File.Exists(path)) return;
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unknown transaction journal version");
        var count = reader.ReadInt32();
        if (count < 0 || count > 1000000) throw new InvalidDataException("Invalid journal count");
        for (var i = 0; i < count; i++)
        {
            var length = reader.ReadInt32();
            if (length < 0 || length > 32 * 1024 * 1024) throw new InvalidDataException("Invalid journal entry");
            var tx = MetadataTransaction.Import(reader.ReadBytes(length));
            transactions.Add(tx.Id, tx);
        }
    }

    public void Dispose() { requests.Clear(); localTransactions.Clear(); serverTransactions.Clear(); }
}
