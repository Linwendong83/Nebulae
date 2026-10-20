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
    private bool localDirty;
    private bool restoring;
    private long lastSendTick;
    private int bulkActionDepth;
    private int directActionDepth;
    private int plantingDepth;

    public VegetableCollection ReplayCollection { get; private set; }
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
        ReplayCollection = collection;
    }

    public void EndReplay() => ReplayCollection = null;

    public void MarkLocalDirty(VegetableCollection collection)
    {
        if (restoring || ReplayCollection != null || !Multiplayer.IsActive || !Multiplayer.Session.IsGameLoaded ||
            !Multiplayer.Session.LocalPlayer.IsClient ||
            collection != GameMain.mainPlayer?.vegetableCollection) return;
        localDirty = true;
    }

    public void Tick()
    {
        if (!localDirty || !Multiplayer.IsActive || !Multiplayer.Session.IsGameLoaded ||
            !Multiplayer.Session.LocalPlayer.IsClient || GameMain.gameTick - lastSendTick < 6) return;
        localDirty = false;
        lastSendTick = GameMain.gameTick;
        var data = VegetableCollectionState.Capture(GameMain.mainPlayer.vegetableCollection);
        ((PlayerData)Multiplayer.Session.LocalPlayer.Data).VegetableCollectionData = data;
        Multiplayer.Session.Network.SendPacket(new VegetableCollectionSnapshotPacket(data));
    }

    public void RestoreLocal(byte[] data)
    {
        restoring = true;
        try
        {
            var old = GameMain.mainPlayer.vegetableCollection;
            var replacement = new VegetableCollection
            {
                onPlayerVegeChanged = old?.onPlayerVegeChanged
            };
            VegetableCollectionState.Restore(replacement, data);
            GameMain.mainPlayer.SetHiddenProperty(nameof(global::Player.vegetableCollection), replacement);
            replacement.onPlayerVegeChanged?.Invoke();
            localDirty = false;
        }
        finally { restoring = false; }
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

    public byte[] VerifySnapshot(ushort playerId, byte[] bytes)
    {
        if (bytes == null || bytes.Length > VegetableCollectionState.MaxBytes) return null;
        var player = Multiplayer.Session.Server?.Players.Get(playerId, EConnectionStatus.Connected);
        if (player?.Data is not PlayerData) return null;
        var canonical = CaptureRemote(playerId);
        if (canonical.Length == bytes.Length)
        {
            var equal = true;
            for (var i = 0; i < bytes.Length; i++)
                if (canonical[i] != bytes[i]) { equal = false; break; }
            if (equal) return null;
        }
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

    public void ForgetRemote(ushort playerId) => remoteCollections.Remove(playerId);

    public void Dispose() => remoteCollections.Clear();
}
