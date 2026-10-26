using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private const int JournalVersion = 2;
    private const int SnapshotFormatVersion = 1;
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
    private readonly DiagnosticWatchdog diagnosticWaits = new();
    public bool ApplyingWorld { get; private set; }
    public bool ApplyingPersonal { get; private set; }
    public string ActiveOperationId { get; private set; } = "";
    public int TrashOrdinal { get; set; }
    public bool Busy => pending != null || requests.Count > 0 || faulted;
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
        var server = new Dictionary<string, MetadataTransaction>(StringComparer.Ordinal);
        var local = new Dictionary<string, MetadataTransaction>(StringComparer.Ordinal);
        if (Multiplayer.Session.IsServer)
        {
            serverPath = Path.Combine(LegacyDataMigration.DataDirectory(GameConfig.gameSaveFolder), SaveManager.WorldId, "transactions.bin");
            Load(serverPath, server);
        }
        if (!Multiplayer.Session.IsDedicated)
        {
            localPath = Path.Combine(LegacyDataMigration.DataDirectory(GameConfig.propertyFolder), AtomicFile.IdentityFileName(MetadataManager.LocalIdentity),
                SaveManager.WorldId + ".transactions");
            Load(localPath, local);
        }
        // No effects or recovery requests are published until both journals are valid.
        foreach (var tx in server.Values) serverTransactions.Add(tx.Id, tx);
        foreach (var tx in local.Values) localTransactions.Add(tx.Id, tx);
        initialized = true;
        Log.Debug($"[metadata] journals loaded world={SaveManager.WorldId} serverPath={serverPath} localPath={localPath} serverTransactions={server.Count} localTransactions={local.Count}");
        if (Multiplayer.Session.IsServer)
        {
            sequence = serverTransactions.Values.Select(x => x.Sequence).DefaultIfEmpty().Max();
            foreach (var tx in serverTransactions.Values.OrderBy(x => x.Sequence))
            {
                if (tx.State is MetadataTransactionState.Committed or MetadataTransactionState.Applied)
                    ApplyWorld(tx);
            }
        }
        if (Multiplayer.Session.IsDedicated) return;
        foreach (var tx in localTransactions.Values)
        {
            if (tx.State is MetadataTransactionState.Applied or MetadataTransactionState.Rejected) continue;
            requests.Enqueue(tx);
        }
    }

    public void Request(MetadataOperation operation, int target = 0, int count = 0, int parameter = 0)
    {
        try { RequestCore(operation, target, count, parameter); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Suspend(error);
            throw;
        }
    }

    private void RequestCore(MetadataOperation operation, int target, int count, int parameter)
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
        if (operation == MetadataOperation.Respawn)
        {
            try
            {
                Quote(tx, (PlayerData)Multiplayer.Session.LocalPlayer.Data);
                PrepareDebit(tx);
            }
            catch (InvalidOperationException error)
            {
                UIRealtimeTip.Popup(error.Message.Translate());
                return;
            }
            // This is the recovery record for both an interrupted debit and an unanswered commit.
            // A separate Requested/Debited write carries no extra information for a fixed respawn price.
            tx.State = MetadataTransactionState.DebitPending;
            localTransactions.Add(tx.Id, pending = tx);
            PersistLocal();
            FinishDebit(tx);
            SendToServer(MetadataMessage.RespawnCommit, tx);
            return;
        }
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
            Suspend(e);
        }
    }

    private void Poll()
    {
        if (Multiplayer.Session.IsDedicated || faulted) return;
        if (pending == null && requests.Count > 0) pending = requests.Dequeue();
        diagnosticWaits.Observe("metadata-operation", pending != null,
            () => $"[metadata] world={SaveManager.WorldId} tx={pending?.Id} operation={pending?.Operation} state={pending?.State} sequence={pending?.Sequence} queued={requests.Count}");
        if (pending == null) return;
        if (pending.State == MetadataTransactionState.DebitPending) FinishDebit(pending);
        var message = pending.State switch
        {
            MetadataTransactionState.Requested => MetadataMessage.Request,
            MetadataTransactionState.Debited => CommitMessage(pending),
            _ => MetadataMessage.Query
        };
        SendToServer(message, pending);
    }

    private void SendToServer(MetadataMessage message, MetadataTransaction tx)
    {
        lastPoll = DateTime.UtcNow.Ticks;
        var packet = Packet(message, tx);
        if (Multiplayer.Session.IsServer)
            HandleServer(packet, (PlayerData)Multiplayer.Session.LocalPlayer.Data, Receive);
        else Multiplayer.Session.Network.SendPacket(packet);
    }

    public void HandleServer(MetadataOperationPacket packet, PlayerData player, Action<MetadataOperationPacket> reply)
    {
        if (faulted || packet.WorldId != SaveManager.WorldId || string.IsNullOrEmpty(player.PersistentId)) return;
        // Bad wire data is a packet error, not evidence that the durable journal failed.
        var input = MetadataTransaction.ImportWire(packet.Transaction);
        try
        {
            Initialize();
            if (packet.Message == MetadataMessage.RespawnCommit) HandleRespawnCommit(input, player, reply);
            else HandleServerCore(packet, player, reply, input);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Suspend(error);
            throw;
        }
    }

    private void HandleRespawnCommit(MetadataTransaction input, PlayerData player, Action<MetadataOperationPacket> reply)
    {
        if (input.Operation != MetadataOperation.Respawn) return;
        var existing = serverTransactions.TryGetValue(input.Id, out var tx);
        if (existing)
        {
            if (tx.Owner != player.PersistentId) return;
            // Look up a completed receipt before validating the live death state: a retry can arrive
            // after the animation completed or after the player died again.
            if (tx.State is MetadataTransactionState.Committed or MetadataTransactionState.Applied or MetadataTransactionState.Rejected)
            {
                if (tx.State != MetadataTransactionState.Rejected && !SameRespawn(tx, input)) return;
                if (tx.State != MetadataTransactionState.Rejected) ApplyWorld(tx);
                reply(Packet(MetadataMessage.Result, tx));
                return;
            }
        }
        else
        {
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
        }
        try
        {
            if (input.State != MetadataTransactionState.Debited || input.Count != 0 ||
                existing && (tx.State != MetadataTransactionState.Quoted || DateTime.UtcNow.Ticks > tx.Expires || !SameRespawn(tx, input)))
                throw new InvalidOperationException("The game state changed; the metadata payment has been refunded");
            if (serverTransactions.Values.Any(x => x.Id != tx.Id && x.Owner == tx.Owner &&
                (x.State == MetadataTransactionState.Quoted && x.Expires > DateTime.UtcNow.Ticks ||
                 x.Operation == MetadataOperation.Respawn && x.Stamp == input.Stamp &&
                 x.State is MetadataTransactionState.Committed or MetadataTransactionState.Applied)))
                throw new InvalidOperationException("Another metadata operation is pending");
            var current = new MetadataTransaction
            {
                Operation = MetadataOperation.Respawn,
                Target = input.Target,
                Parameter = input.Parameter
            };
            Quote(current, player);
            if (current.Stamp != input.Stamp || !current.Cost.SequenceEqual(input.Cost))
                throw new InvalidOperationException("The game state changed; the metadata payment has been refunded");
            tx.Cost = current.Cost;
            tx.Stamp = current.Stamp;
            tx.State = MetadataTransactionState.Committed;
            tx.Sequence = ++sequence;
        }
        catch (InvalidOperationException error) { Reject(tx, error.Message); }
        PersistServer(); // Both accepted and rejected outcomes are durable before replying.
        if (tx.State == MetadataTransactionState.Committed) ApplyWorld(tx);
        reply(Packet(MetadataMessage.Result, tx));
    }

    private static bool SameRespawn(MetadataTransaction tx, MetadataTransaction input) =>
        tx.Operation == input.Operation && tx.Target == input.Target && tx.Parameter == input.Parameter &&
        tx.Count == input.Count && tx.Stamp == input.Stamp && tx.Cost.SequenceEqual(input.Cost);

    private static MetadataMessage CommitMessage(MetadataTransaction tx) =>
        tx.Operation == MetadataOperation.Respawn ? MetadataMessage.RespawnCommit : MetadataMessage.Commit;

    private void HandleServerCore(MetadataOperationPacket packet, PlayerData player, Action<MetadataOperationPacket> reply, MetadataTransaction input)
    {
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
                var restored = ReadPlayer(tx.AfterPlayer, $"tx={tx.Id} operation={tx.Operation} snapshot=after");
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
        Log.Debug($"[metadata] transaction rejected tx={tx.Id} operation={tx.Operation} state={tx.State} sequence={tx.Sequence} reason={error}");
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
                if (tx.Stamp < 0) throw new InvalidOperationException("Player is not awaiting respawn");
                tx.Cost = RespawnPaymentPolicy.Calculate(player.Life.DeathCount, tx.Target, tx.Parameter, GameMain.data.gameDesc.isSandboxMode);
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
        try { ReceiveCore(packet, tx); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Suspend(error);
            throw;
        }
    }

    private void ReceiveCore(MetadataOperationPacket packet, MetadataTransaction tx)
    {
        if (!localTransactions.TryGetValue(tx.Id, out var local)) return;
        if (packet.Message == MetadataMessage.Acknowledge)
        {
            if (local.State != MetadataTransactionState.Committed) return;
            local.State = MetadataTransactionState.Applied;
            PersistLocal();
            Log.Debug($"[metadata] transaction confirmed tx={local.Id} operation={local.Operation} sequence={local.Sequence}");
            if (pending?.Id == local.Id) pending = null;
            return;
        }
        if (packet.Message == MetadataMessage.Quote)
        {
            if (local.State is MetadataTransactionState.Debited or MetadataTransactionState.DebitPending)
            {
                if (local.State == MetadataTransactionState.DebitPending) FinishDebit(local);
                SendToServer(CommitMessage(local), local);
                return;
            }
            if (local.State != MetadataTransactionState.Requested) return;
            try { PrepareDebit(tx); }
            catch (InvalidOperationException) { SendToServer(MetadataMessage.Cancel, local); return; }
            tx.State = MetadataTransactionState.DebitPending;
            localTransactions[tx.Id] = pending = tx;
            PersistLocal();
            FinishDebit(tx);
            SendToServer(CommitMessage(tx), tx);
            Log.Debug($"[metadata] debit committed tx={tx.Id} operation={tx.Operation} state={tx.State} sequence={tx.Sequence}");
            return;
        }
        if (packet.Message != MetadataMessage.Result) return;
        if (local.State is MetadataTransactionState.Applied or MetadataTransactionState.Rejected) return;
        if (tx.State == MetadataTransactionState.Rejected)
        {
            if (local.State is MetadataTransactionState.Debited or MetadataTransactionState.DebitPending)
            {
                var current = PropertySystem.matrixIds.Select(item => DSPGame.propertySystem.GetItemConsumption(GameMain.data.GetClusterSeedKey(), item)).ToArray();
                MetadataPayment.RecoverDebit(current, local.Before, local.After);
                for (var i = 0; i < 6; i++)
                    DSPGame.propertySystem.SetItemConsumption(GameMain.data.GetClusterSeedKey(), PropertySystem.matrixIds[i], local.Before[i]);
                SaveWallet();
            }
            local.State = MetadataTransactionState.Rejected;
            PersistLocal();
            Log.Debug($"[metadata] transaction rejected or refunded tx={local.Id} operation={local.Operation} sequence={local.Sequence} reason={tx.Error}");
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
            SaveWallet();
        }
        ApplyPersonal(local, resume);
        SendToServer(MetadataMessage.Acknowledge, local);
    }

    private static void PrepareDebit(MetadataTransaction tx)
    {
        var properties = DSPGame.propertySystem;
        var key = GameMain.data.GetClusterSeedKey();
        var balances = new long[6];
        for (var i = 0; i < 6; i++)
        {
            var item = PropertySystem.matrixIds[i];
            balances[i] = tx.Operation >= MetadataOperation.IncreaseAggressiveness ?
                properties.GetItemAvaliablePropertyForRespawn(key, item) : properties.GetItemAvaliableProperty(key, item);
            tx.Before[i] = properties.GetItemConsumption(key, item);
        }
        tx.After = MetadataPayment.Prepare(tx.Before, tx.Cost, balances);
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
        SaveWallet();
        tx.State = MetadataTransactionState.Debited;
        // DebitPending plus the durable absolute wallet values is enough to recover this state.
        if (tx.Operation != MetadataOperation.Respawn) PersistLocal();
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
                        // An expired receipt must not clear attacks when replayed after recovery.
                        if (tx.EffectValue > GameMain.gameTick)
                            history.AddTruceTime(tx.EffectValue - GameMain.gameTick - history.dfTruceTimer);
                        else history.dfTruceTimer = 0;
                        Multiplayer.Session.Server.SendPacket(new CombatTruceUpdatePacket(0, tx.EffectValue));
                        break;
                    case MetadataOperation.WithdrawTruce:
                        // Vanilla withdrawal only ends the timer; AddTruceTime also resets enemy attacks.
                        history.dfTruceTimer = 0;
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
            var delivered = ReadPlayer(tx.AfterPlayer, $"tx={tx.Id} operation={tx.Operation} snapshot=after");
            var current = (PlayerData)Multiplayer.Session.LocalPlayer.Data;
            if (resume && delivered != null && ShouldRestorePlayer(tx, delivered, current))
            {
                RestorePlayer(tx.AfterPlayer, $"tx={tx.Id} operation={tx.Operation} snapshot=after");
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
            if (resume) RestorePlayer(tx.BeforePlayer, $"tx={tx.Id} operation={tx.Operation} snapshot=before");
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
            var snapshot = ReadPlayer(tx.AfterPlayer, $"tx={tx.Id} operation={tx.Operation} snapshot=after");
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
        var snapshot = data.CreatePersonalSnapshot(player.mecha);
        snapshot.Serialize(writer);
        return writer.CopyData();
    }

    internal static PlayerData ReadPlayer(byte[] bytes, string context = null)
    {
        if (bytes.Length == 0) return null;
        try
        {
            var player = new PlayerData();
            var reader = new NetDataReader(bytes);
            player.Deserialize(reader);
            if (reader.AvailableBytes == 0) return player;
            if (context != null) Diagnostics.Error("metadata:snapshot:" + context, () => $"[metadata] player snapshot has trailing bytes context={context} bytes={bytes.Length} trailing={reader.AvailableBytes}");
            return null;
        }
        catch (Exception error)
        {
            if (context != null) Diagnostics.Error("metadata:snapshot:" + context, () => $"[metadata] player snapshot unreadable context={context} bytes={bytes.Length}", error);
            return null;
        }
    }

    private static void RestorePlayer(byte[] bytes, string context)
    {
        if (bytes.Length == 0) return;
        var snapshot = ReadPlayer(bytes, context);
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

    private void SaveWallet()
    {
        PropertyAccountStore.Save();
    }

    private void Suspend(Exception error)
    {
        faulted = true;
        var reason = error is InvalidDataException or ParseException or EndOfStreamException
            ? "a transaction journal format failure"
            : error is IOException or UnauthorizedAccessException ? "a file access failure" : "a recovery failure";
        Log.ErrorWithoutPopup($"[metadata] recovery suspended reason={reason} world={SaveManager.WorldId} tx={pending?.Id} operation={pending?.Operation} state={pending?.State} sequence={pending?.Sequence} activeOperation={ActiveOperationId} serverPath={serverPath} localPath={localPath} fileStage={error.Data["Nebulae.FileStage"]} target={error.Data["Nebulae.FileTarget"]}", error);
    }

    private void PersistServer() => Save(serverPath, serverTransactions);
    private void PersistLocal() => Save(localPath, localTransactions);

    private static void Save(string path, Dictionary<string, MetadataTransaction> transactions)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(JournalVersion); writer.Write(SnapshotFormatVersion); writer.Write(transactions.Count);
        foreach (var tx in transactions.Values)
        {
            // Backfill the entry length so historical snapshots are written directly, without
            // allocating a MemoryStream and two byte-array copies for every old transaction.
            writer.Write(0);
            var start = stream.Position;
            tx.WriteTo(writer);
            var end = stream.Position;
            stream.Position = start - sizeof(int);
            writer.Write(checked((int)(end - start)));
            stream.Position = end;
        }
        AtomicFile.Write(path, stream.GetBuffer(), checked((int)stream.Length));
    }

    private static void Load(string path, Dictionary<string, MetadataTransaction> transactions)
    {
        if (!File.Exists(path)) return;
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        var loaded = new Dictionary<string, MetadataTransaction>(StringComparer.Ordinal);
        var context = "header";
        try
        {
            var version = reader.ReadInt32();
            if (version != 1 && version != JournalVersion) throw new InvalidDataException("Unknown transaction journal version");
            if (version == JournalVersion && reader.ReadInt32() != SnapshotFormatVersion)
                throw new InvalidDataException("Unknown transaction player snapshot format");
            var count = reader.ReadInt32();
            if (count < 0 || count > 1000000) throw new InvalidDataException("Invalid journal count");
            for (var i = 0; i < count; i++)
            {
                context = $"entry {i}, offset {stream.Position}";
                var length = reader.ReadInt32();
                if (length < 0 || length > 32 * 1024 * 1024 || length > stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid journal entry length");
                var tx = MetadataTransaction.Import(reader.ReadBytes(length));
                context = $"transaction {tx.Id}, BeforePlayer";
                tx.BeforePlayer = ReadJournalSnapshot(tx.BeforePlayer, version == 1);
                context = $"transaction {tx.Id}, AfterPlayer";
                tx.AfterPlayer = ReadJournalSnapshot(tx.AfterPlayer, version == 1);
                if (loaded.ContainsKey(tx.Id)) throw new InvalidDataException("Duplicate transaction identity");
                loaded.Add(tx.Id, tx);
            }
            context = "journal end";
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing transaction journal data");
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or ParseException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            throw new InvalidDataException($"Cannot read transaction journal '{path}' ({context}).", error);
        }
        catch (IOException error)
        {
            throw new IOException($"Cannot read transaction journal '{path}' ({context}).", error);
        }
        transactions.Clear();
        foreach (var tx in loaded.Values) transactions.Add(tx.Id, tx);
    }

    private static byte[] ReadJournalSnapshot(byte[] bytes, bool legacy)
    {
        if (legacy) return UpgradeSavedPlayer(bytes);
        if (bytes.Length == 0) return bytes;
        var reader = new NetDataReader(bytes);
        new PlayerData().Deserialize(reader);
        if (reader.AvailableBytes != 0) throw new InvalidDataException("Trailing transaction player snapshot data");
        return bytes;
    }

    internal static byte[] UpgradeSavedPlayer(byte[] bytes)
    {
        if (bytes.Length == 0 || ReadPlayer(bytes) != null) return bytes;
        // Only unversioned, individually bounded disk snapshots may use historical layouts.
        // Network readers and versioned journals always require their declared format.
        Exception lastError = null;
        for (var revision = 12; revision >= 9; revision--)
        {
            try
            {
                var reader = new NetDataReader(bytes);
                var player = new PlayerData(); player.Import(reader, revision);
                if (reader.AvailableBytes != 0) throw new InvalidDataException("Trailing saved transaction player data");
                var writer = new NetDataWriter(); player.Serialize(writer); return writer.CopyData();
            }
            catch (Exception error) when (error is InvalidDataException or IOException or ParseException or ArgumentException or OverflowException or IndexOutOfRangeException)
            {
                lastError = error;
            }
        }
        throw new InvalidDataException("Unknown or damaged saved transaction player snapshot.", lastError);
    }

    public void Dispose() { diagnosticWaits.Clear(); requests.Clear(); localTransactions.Clear(); serverTransactions.Clear(); }
}
