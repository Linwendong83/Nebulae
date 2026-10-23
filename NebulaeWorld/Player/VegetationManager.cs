using System;
using System.Collections.Generic;
using NebulaeModel.Networking;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Players;
using NebulaeModel.Utils;

namespace NebulaeWorld.Player;

internal sealed class VegetationManager : IDisposable
{
    private readonly Dictionary<ushort, VegetableCollection> remoteCollections = new();
    private readonly Dictionary<ushort, VegetableCollection> replicaCollections = new();
    private bool localDirty;
    private bool restoring;
    private long lastSendTick;
    private int bulkActionDepth;
    private int directActionDepth;
    private int plantingDepth;
    private readonly Stack<VegetableCollection> replayStack = new();
    private readonly VegetationCollectionJournal journal = new();
    private readonly Dictionary<ushort, long> receiptRevisions = new();
    private readonly Dictionary<ushort, long> commandSequences = new();
    private long nextCommandSequence;
    public long NextCommandSequence() => System.Threading.Interlocked.Increment(ref nextCommandSequence);
    public bool AcceptCommand(ushort player, long sequence)
    {
        if (sequence < 0) return false;
        if (sequence == 0) return true; // Host-originated/internal operations have no client command.
        if (commandSequences.TryGetValue(player, out var previous) && sequence <= previous) return false;
        commandSequences[player] = sequence; return true;
    }

    public VegetableCollection ReplayCollection { get; private set; }
    public BuildTool_Reform ReformTool { get; set; }
    public bool? ReformIsCircle { get; set; }
    public int ReformPitId { get; set; }
    public bool IsBulkAction => bulkActionDepth > 0;
    public bool IsDirectAction => directActionDepth > 0;
    public bool IsPlanting => plantingDepth > 0;

    public void EnterBulkAction() => bulkActionDepth++;
    public void LeaveBulkAction() => bulkActionDepth = Math.Max(0, bulkActionDepth - 1);
    public void EnterDirectAction() => directActionDepth++;
    public void LeaveDirectAction() => directActionDepth = Math.Max(0, directActionDepth - 1);
    public void EnterPlanting() => plantingDepth++;
    public void LeavePlanting() => plantingDepth = Math.Max(0, plantingDepth - 1);

    public void BeginReplay(byte[] before)
    {
        var collection = new VegetableCollection();
        VegetableCollectionState.Restore(collection, before);
        replayStack.Push(ReplayCollection);
        ReplayCollection = collection;
    }

    public void EndReplay() => ReplayCollection = replayStack.Count > 0 ? replayStack.Pop() : null;

    private bool IsLocalCollection(VegetableCollection collection) => !restoring && ReplayCollection == null &&
        Multiplayer.IsActive && Multiplayer.Session.IsGameLoaded && Multiplayer.Session.LocalPlayer.IsClient &&
        collection == VegetationAccess.LocalCollection(GameMain.mainPlayer);

    public void BeforeLocalChange(VegetableCollection collection)
    {
        if (IsLocalCollection(collection)) journal.ObserveBefore(VegetableCollectionState.Capture(collection));
    }

    public void MarkLocalDirty(VegetableCollection collection)
    {
        if (!IsLocalCollection(collection)) return;
        journal.ObserveAfter(VegetableCollectionState.Capture(collection));
        localDirty = true;
    }

    public void Tick()
    {
        if (!localDirty || !Multiplayer.IsActive || !Multiplayer.Session.IsGameLoaded ||
            !Multiplayer.Session.LocalPlayer.IsClient || GameMain.gameTick - lastSendTick < 6) return;
        localDirty = false;
        lastSendTick = GameMain.gameTick;
        var data = VegetableCollectionState.Capture(VegetationAccess.LocalCollection(GameMain.mainPlayer));
        ((PlayerData)Multiplayer.Session.LocalPlayer.Data).VegetableCollectionData = data;
        Multiplayer.Session.Network.SendPacket(new VegetableCollectionSnapshotPacket(data) { OperationSequence = journal.Sequence });
    }

    public VegetableCollectionSnapshotPacket Receipt(ushort author, long sequence)
    {
        receiptRevisions.TryGetValue(author, out var revision);
        receiptRevisions[author] = ++revision;
        return new VegetableCollectionSnapshotPacket(CaptureRemote(author), true)
        { OperationSequence = sequence, Revision = revision };
    }

    public void Reconcile(VegetableCollectionSnapshotPacket packet)
    {
        if (journal.TryReconcile(packet.Data, packet.OperationSequence, packet.Revision, out var merged))
        {
            RestoreLocal(merged);
            localDirty = journal.Sequence > packet.OperationSequence;
            ((PlayerData)Multiplayer.Session.LocalPlayer.Data).VegetableCollectionData = merged;
        }
    }

    public void RestoreLocal(byte[] data)
    {
        var previousRestoring = restoring;
        restoring = true;
        try
        {
            var old = VegetationAccess.LocalCollection(GameMain.mainPlayer);
            var replacement = new VegetableCollection
            {
                onPlayerVegeChanged = old?.onPlayerVegeChanged
            };
            VegetableCollectionState.Restore(replacement, data);
            GameMain.mainPlayer.SetHiddenProperty(nameof(global::Player.vegetableCollection), replacement);
            replacement.onPlayerVegeChanged?.Invoke();
            localDirty = false;
        }
        finally { restoring = previousRestoring; }
    }

    public VegetableCollection GetRemote(ushort playerId)
    {
        if (remoteCollections.TryGetValue(playerId, out var collection)) return collection;
        var player = Multiplayer.Session.Server?.Players.Get(playerId, EConnectionStatus.Connected);
        if (player?.Data is not PlayerData data) return null;
        collection = new VegetableCollection();
        VegetableCollectionState.Restore(collection, data.VegetableCollectionData);
        remoteCollections.Add(playerId, collection);
        return collection;
    }

    public VegetableCollection GetReplicaCollection(ushort playerId)
    {
        if (!replicaCollections.TryGetValue(playerId, out var collection)) replicaCollections[playerId] = collection = new VegetableCollection();
        return collection;
    }

    public void InstallReplicaCollection(int author, byte[] before)
    {
        if (!Multiplayer.Session.IsClient || author <= 0 || author > ushort.MaxValue || author == Multiplayer.Session.LocalPlayer.Id) return;
        var collection = new VegetableCollection(); VegetableCollectionState.Restore(collection, before);
        replicaCollections[(ushort)author] = collection;
    }

    public byte[] VerifySnapshot(ushort playerId, byte[] bytes)
    {
        if (bytes == null || bytes.Length > VegetableCollectionState.MaxBytes) return null;
        var player = Multiplayer.Session.Server?.Players.Get(playerId, EConnectionStatus.Connected);
        if (player?.Data is not PlayerData) return null;
        var canonical = CaptureRemote(playerId);
        if (VegetableCollectionState.Equivalent(canonical, bytes)) return null;
        return canonical;
    }

    public void CaptureRemoteForSave()
    {
        if (Multiplayer.Session.Server == null) return;
        foreach (var entry in remoteCollections)
        {
            var player = Multiplayer.Session.Server.Players.Get(entry.Key, EConnectionStatus.Connected);
            if (player?.Data is PlayerData data)
                data.VegetableCollectionData = VegetableCollectionState.Capture(entry.Value);
        }
    }

    public byte[] CaptureRemote(ushort playerId)
    {
        var collection = GetRemote(playerId);
        var data = VegetableCollectionState.Capture(collection);
        var player = Multiplayer.Session.Server?.Players.Get(playerId, EConnectionStatus.Connected);
        if (player?.Data is PlayerData playerData) playerData.VegetableCollectionData = data;
        return data;
    }

    public void ForgetRemote(ushort playerId) { remoteCollections.Remove(playerId); replicaCollections.Remove(playerId); receiptRevisions.Remove(playerId); commandSequences.Remove(playerId); }

    public void Dispose() { remoteCollections.Clear(); replicaCollections.Clear(); replayStack.Clear(); commandSequences.Clear(); receiptRevisions.Clear(); }
}
