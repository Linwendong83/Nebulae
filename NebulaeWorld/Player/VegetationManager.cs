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
    private readonly Dictionary<VegetableCollection, CollectionCache> collectionCaches = new();
    private readonly object collectionCacheGate = new();
    private readonly Dictionary<ushort, long> installedReplicaRevisions = new();
    private long localVersion = 1;
    private long nextCommandSequence;

    private sealed class CollectionCache
    {
        public long Version = -1;
        public byte[] Bytes;
        public long LastSentVersion = -1;
        public long LastSentTick = long.MinValue;
    }
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

    private bool IsLocalPlayerCollection(VegetableCollection collection) => !restoring && ReplayCollection == null &&
        collection == VegetationAccess.LocalCollection(GameMain.mainPlayer);

    private bool IsLocalCollection(VegetableCollection collection) => IsLocalPlayerCollection(collection) &&
        Multiplayer.IsActive && Multiplayer.Session.IsGameLoaded && Multiplayer.Session.LocalPlayer.IsClient;

    public void BeforeLocalChange(VegetableCollection collection)
    {
        // The capture cache serves the host and clients alike, so the version follows the native
        // mutation even where the journal (client-only) does not.
        if (IsLocalPlayerCollection(collection)) System.Threading.Interlocked.Increment(ref localVersion);
        if (!IsLocalCollection(collection)) return;
        journal.ObserveBefore(VegetableCollectionState.Capture(collection));
    }

    public void MarkLocalDirty(VegetableCollection collection)
    {
        if (IsLocalPlayerCollection(collection)) System.Threading.Interlocked.Increment(ref localVersion);
        if (!IsLocalCollection(collection)) return;
        journal.ObserveAfter(VegetableCollectionState.Capture(collection));
        localDirty = true;
    }

    /// <summary>
    ///     Serializes the collection that an operation's replay needs on the receiving peers.
    /// </summary>
    /// <remarks>
    ///     Every building of an area dismantle used to serialize the player's whole collection again,
    ///     and every peer restored it again. The bytes only depend on the collection, so they are
    ///     captured once per version; a burst that repeats the same version in one tick sends the
    ///     full payload once and empty payloads afterwards, which receivers treat as "unchanged".
    ///     A collection that is not the local player's (the host relaying another author) is not
    ///     cached, because native replay mutates it in place.
    /// </remarks>
    public (byte[] Bytes, long Revision) CaptureRequest(VegetableCollection collection)
    {
        if (collection == null) return (Array.Empty<byte>(), 0);
        if (collection != VegetationAccess.LocalCollection(GameMain.mainPlayer))
            return (VegetableCollectionState.Capture(collection), 0);
        // Drones finish on simulation workers, so several builds can ask for the same collection at
        // once; the cache and its bookkeeping are guarded like the native pools they mirror.
        lock (collectionCacheGate)
        {
            var version = localVersion;
            if (!collectionCaches.TryGetValue(collection, out var cache))
                collectionCaches[collection] = cache = new CollectionCache();
            if (cache.Bytes == null || cache.Version != version)
            {
                cache.Bytes = VegetableCollectionState.Capture(collection);
                cache.Version = version;
            }
            if (cache.LastSentVersion == version && cache.LastSentTick == GameMain.gameTick)
                return (Array.Empty<byte>(), version);
            cache.LastSentVersion = version;
            cache.LastSentTick = GameMain.gameTick;
            return (cache.Bytes, version);
        }
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
            System.Threading.Interlocked.Increment(ref localVersion);
            lock (collectionCacheGate) collectionCaches.Clear();
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

    public void InstallReplicaCollection(int author, byte[] before, long revision = 0)
    {
        if (!Multiplayer.Session.IsClient || author <= 0 || author > ushort.MaxValue || author == Multiplayer.Session.LocalPlayer.Id) return;
        // Empty payload means the sender is repeating the revision this receiver already has (or the
        // author has no vegetation yet), so the existing replica is still correct.
        if (before == null || before.Length == 0) return;
        if (revision > 0 && installedReplicaRevisions.TryGetValue((ushort)author, out var current) && current == revision) return;
        var collection = new VegetableCollection(); VegetableCollectionState.Restore(collection, before);
        replicaCollections[(ushort)author] = collection;
        if (revision > 0) installedReplicaRevisions[(ushort)author] = revision;
    }

    /// <summary>
    /// Verifies one client snapshot against the host's cache of that player's collection.
    /// </summary>
    /// <remarks>
    /// The player owns the collection; the host only mirrors it. A snapshot that merely adds what the
    /// mirror missed — an operation the host refused while the client had already applied it — is
    /// therefore the corrected fact, and adopting it is what stops a single dropped harvest from
    /// rolling the player's inventory back. Anything that would remove a recorded item is still
    /// restored from the host, which is the fail-closed direction for a client that lost items.
    /// </remarks>
    /// <returns>The canonical bytes to restore, or null when the snapshot was accepted.</returns>
    public byte[] VerifySnapshot(ushort playerId, byte[] bytes)
    {
        if (bytes == null || bytes.Length > VegetableCollectionState.MaxBytes) return null;
        var player = Multiplayer.Session.Server?.Players.Get(playerId, EConnectionStatus.Connected);
        if (player?.Data is not PlayerData data) return null;
        var canonical = CaptureRemote(playerId);
        if (VegetableCollectionState.Equivalent(canonical, bytes)) return null;
        if (!VegetableCollectionState.OnlyAdds(canonical, bytes, out _)) return canonical;
        var adopted = new VegetableCollection();
        VegetableCollectionState.Restore(adopted, bytes);
        remoteCollections[playerId] = adopted;
        data.VegetableCollectionData = bytes;
        return null;
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

    public void ForgetRemote(ushort playerId)
    {
        remoteCollections.Remove(playerId); replicaCollections.Remove(playerId); receiptRevisions.Remove(playerId);
        commandSequences.Remove(playerId); installedReplicaRevisions.Remove(playerId);
    }

    public void Dispose()
    {
        remoteCollections.Clear(); replicaCollections.Clear(); replayStack.Clear(); commandSequences.Clear();
        receiptRevisions.Clear(); collectionCaches.Clear(); installedReplicaRevisions.Clear();
    }
}
