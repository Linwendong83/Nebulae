using System;
using System.Collections.Generic;
using System.IO;
using NebulaeModel.DataStructures;
using NebulaeModel.Logger;
using NebulaeModel.Packets.GameStates;
using NebulaeModel.Utils;

namespace NebulaeWorld.GameStates;

internal sealed class MetadataManager : IDisposable
{
    private MetadataLedger ledger;
    private string path;
    private readonly HashSet<string> online = new(StringComparer.Ordinal);
    private readonly Dictionary<ushort, string> identities = new();
    private bool faulted;
    private long lastTick = -1;
    private long appliedSequence = -1;
    private static WeakReference<GameData> retiredWorld;
    public long SourceClusterKey { get; set; } = long.MinValue;
    public static bool IsRetired(GameData data) => retiredWorld != null && retiredWorld.TryGetTarget(out var old) && ReferenceEquals(data, old);

    public static string LocalIdentity => CryptoUtils.GetCurrentUserPublicKeyHash();
    public bool IsReady => ledger != null && !faulted;

    public void Initialize()
    {
        if (ledger != null || !Multiplayer.Session.IsServer || GameMain.data == null) return;
        path = Path.Combine(LegacyDataMigration.DataDirectory(GameConfig.gameSaveFolder), SaveManager.WorldId, "metadata.bin");
        ledger = File.Exists(path) ? MetadataLedger.Import(File.ReadAllBytes(path)) : new MetadataLedger();
        if (ledger.ClusterKey == long.MinValue) ledger.ClusterKey = GameMain.data.GetClusterSeedKey();
        SourceClusterKey = ledger.ClusterKey;
        // Loading/migration never awards historical production, even when the save is newer than the ledger.
        var baseline = new int[MetadataLedger.ItemCount];
        for (var i = 0; i < baseline.Length; i++)
            baseline[i] = GameMain.history.GetPropertyItemProduction(PropertySystem.productIds[i]);
        ledger.Advance(baseline, Array.Empty<string>());
        ledger.Advance(CalculateProduction(), Array.Empty<string>());
        Persist();
        if (!Multiplayer.Session.IsDedicated)
        {
            var data = (PlayerData)Multiplayer.Session.LocalPlayer.Data;
            data.PersistentId = LocalIdentity;
            Join(Multiplayer.Session.LocalPlayer.Id, LocalIdentity);
        }
    }

    public void Join(ushort id, string identity)
    {
        Initialize();
        if (faulted || identities.ContainsKey(id)) return;
        // Duplicate sessions share one award; they never multiply a member's earnings.
        identities[id] = identity;
        online.Add(identity);
        ledger.GetAccount(identity);
        Persist();
        Send(id);
    }

    public void Leave(ushort id)
    {
        if (!identities.TryGetValue(id, out var identity)) return;
        identities.Remove(id);
        if (!identities.ContainsValue(identity)) online.Remove(identity);
    }

    public void GameTick()
    {
        if (!Multiplayer.Session.IsGameLoaded || !Multiplayer.Session.IsServer || faulted) return;
        try
        {
            Initialize();
            var tick = GameMain.gameTick;
            if (tick == lastTick) return;
            lastTick = tick;
            if (CanProduce() && ledger.Advance(CalculateProduction(), online)) Persist();
            for (var i = 0; i < MetadataLedger.ItemCount; i++)
                GameMain.history.SetPropertyItemProduction(PropertySystem.productIds[i], ledger.Peak[i]);
            if (tick % 60 != 0) return;
            foreach (var entry in identities) Send(entry.Key);
        }
        catch (Exception error)
        {
            faulted = true;
            Log.Error("Metadata journal failed; awards are suspended to avoid duplicate or lost credits.", error);
        }
    }

    public static bool CanProduce()
    {
        if (GameMain.data == null || GameMain.data.gameDesc.isSandboxMode) return false;
        // Read the vanilla evidence directly, independent of the achievement patch's getter override.
        var evidence = GameMain.data.abnormalData?.runtimeDatas;
        if (evidence == null) return false;
        for (var i = 30; i < evidence.Length; i += 30)
            if (evidence[i].protoId == i / 30) return false;
        return true;
    }

    private static int[] CalculateProduction()
    {
        var values = new int[MetadataLedger.ItemCount];
        if (!CanProduce()) return values;
        var data = GameMain.data;
        var stats = data.statistics.production.factoryStatPool;
        for (var i = 0; i < values.Length; i++)
        {
            long total = 0;
            for (var j = 0; j < data.factoryCount; j++)
            {
                var stat = stats[j];
                if (stat == null) continue;
                var index = stat.productIndices[PropertySystem.productIds[i]];
                if (index > 0) total += stat.productPool[index].total[3];
            }
            var value = total * (double)data.history.minimalPropertyMultiplier / 60.0;
            values[i] = (int)Math.Max(0, Math.Min(2000000000, value + 0.01));
        }
        return values;
    }

    private void Persist() => AtomicFile.Write(path, ledger.Export());

    private void Send(ushort id)
    {
        if (faulted || !identities.TryGetValue(id, out var identity)) return;
        var account = ledger.GetAccount(identity);
        var packet = new MetadataProductionPacket
        {
            WorldId = SaveManager.WorldId,
            ClusterKey = SourceClusterKey,
            Sequence = account.Sequence,
            Earned = (int[])account.Earned.Clone(),
            WorldPeak = (int[])ledger.Peak.Clone()
        };
        if (id == Multiplayer.Session.LocalPlayer.Id)
        {
            if (Apply(packet)) Acknowledge(id, packet.Sequence);
        }
        else
        {
            foreach (var player in Multiplayer.Session.Server.Players.Connected.Values)
                if (player.Id == id) { player.SendPacket(packet); break; }
        }
    }

    public void Acknowledge(ushort id, long sequence)
    {
        if (!IsReady || !identities.TryGetValue(id, out var identity)) return;
        var account = ledger.GetAccount(identity);
        if (sequence <= account.Acknowledged || sequence > account.Sequence) return;
        account.Acknowledged = sequence;
        Persist();
    }

    public static bool Apply(MetadataProductionPacket packet)
    {
        if (packet.WorldId != SaveManager.WorldId || packet.ClusterKey != Multiplayer.Session.Metadata.SourceClusterKey ||
            packet.Earned?.Length != MetadataLedger.ItemCount || packet.WorldPeak?.Length != MetadataLedger.ItemCount) return false;
        var properties = DSPGame.propertySystem;
        var changed = false;
        for (var i = 0; i < MetadataLedger.ItemCount; i++)
            if (packet.Earned[i] < 0 || packet.Earned[i] > 2000000000 || packet.Earned[i] > packet.WorldPeak[i])
                throw new InvalidDataException("Invalid metadata award");
        for (var i = 0; i < MetadataLedger.ItemCount; i++)
        {
            var earned = packet.Earned[i];
            if (earned < 0 || earned > 2000000000 || earned > packet.WorldPeak[i])
                throw new InvalidDataException("Invalid metadata award");
            var item = PropertySystem.itemIds[i];
            if (earned > properties.GetItemProduction(packet.ClusterKey, item))
            {
                properties.SetItemProduction(packet.ClusterKey, item, earned);
                changed = true;
            }
            GameMain.history.SetPropertyItemProduction(item, Math.Max(GameMain.history.GetPropertyItemProduction(item), packet.WorldPeak[i]));
        }
        if (changed || packet.Sequence > Multiplayer.Session.Metadata.appliedSequence)
        {
            PropertyAccountStore.Save(); // Do not acknowledge a failed write just because memory was updated.
            Multiplayer.Session.Metadata.appliedSequence = packet.Sequence;
        }
        return true;
    }

    public void Dispose()
    {
        if (GameMain.data != null) retiredWorld = new WeakReference<GameData>(GameMain.data);
        online.Clear(); identities.Clear(); ledger = null;
    }
}
