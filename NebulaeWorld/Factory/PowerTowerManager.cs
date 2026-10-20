using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NebulaeModel.Networking;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Factory.PowerTower;

namespace NebulaeWorld.Factory;

internal sealed class PowerTowerManager : IDisposable
{
    private readonly PowerTowerChargingState state = new();
    private readonly object localStateLock = new();
    private int localStateGeneration;
    private bool disposed;
    private PowerTowerChargerUpdate lastSent;

    // UpdateLocalState runs on factory simulation threads, so the scratch list has to be
    // per-thread. PowerTowerChargingState.Replace copies whatever it is handed, so the
    // list can be reused and the per-tick allocation leaves the power path.
    [ThreadStatic] private static List<int> localNodes;

    public void Dispose()
    {
        lock (localStateLock)
        {
            disposed = true;
            localStateGeneration++;
            state.Clear();
        }
        lastSent = null;
        GC.SuppressFinalize(this);
    }

    // The game world starts ticking before the handshake assigns local player data
    // (e.g. while a password prompt is up), and LocalPlayer.Id throws while Data is null.
    private static bool TryGetLocalPlayerId(out ushort id)
    {
        var data = Multiplayer.Session?.LocalPlayer?.Data;
        if (data != null)
        {
            id = data.PlayerId;
            return true;
        }
        id = 0;
        return false;
    }

    // Called before the local factory's power calculation, on its simulation thread.
    public void UpdateLocalState(PowerSystem powerSystem, bool multithreaded)
    {
        var player = GameMain.mainPlayer;
        var planetId = powerSystem.factory.planetId;
        if (player is null || player.planetId != planetId || GameMain.localPlanet?.id != planetId) return;
        if (!TryGetLocalPlayerId(out var localPlayerId)) return;
        int generation;
        lock (localStateLock)
        {
            if (disposed) return;
            generation = localStateGeneration;
        }

        localNodes ??= new List<int>();
        var nodes = localNodes;
        nodes.Clear();
        var mecha = player.mecha;
        bool needsEnergy;
        lock (mecha)
        {
            needsEnergy = player.isAlive && mecha.coreEnergy < mecha.coreEnergyCap;
        }

        // Match vanilla's surface projection, altitude limits and wireless charger radius.
        var position = multithreaded ? powerSystem.multithreadPlayerPos : player.position;
        var radius = powerSystem.factory.planet.realRadius + 0.2f;
        var magnitude = position.magnitude;
        if (needsEnergy && magnitude > 0f && magnitude > radius - 30f && magnitude < radius + 50f)
        {
            var projectedPosition = position * (radius / magnitude);
            for (var id = 1; id < powerSystem.nodeCursor; id++)
            {
                ref var node = ref powerSystem.nodePool[id];
                if (node.id != id || !node.isCharger || node.coverRadius > 20f || node.networkId <= 0) continue;
                var range = node.coverRadius < 9f ? node.coverRadius + 2.01f : node.coverRadius;
                if ((node.powerPoint * 0.988f - projectedPosition).sqrMagnitude <= range * range)
                {
                    nodes.Add(id);
                }
            }
        }
        lock (localStateLock)
        {
            // A leave-planet or demolition event can invalidate a simulation already in flight.
            if (disposed || generation != localStateGeneration || GameMain.localPlanet?.id != planetId) return;
            state.Replace(localPlayerId, planetId, nodes);
        }
    }

    // Called by network Update on the main thread, never while holding a state lock.
    public void SendLocalStateIfChanged()
    {
        if (!TryGetLocalPlayerId(out var localPlayerId)) return;
        var player = GameMain.mainPlayer;
        if (GameMain.localPlanet == null || player is null || !player.isAlive ||
            player.planetId != GameMain.localPlanet.id || player.mecha.coreEnergy >= player.mecha.coreEnergyCap)
        {
            ClearLocalState();
        }

        var snapshot = state.GetPlayerState(localPlayerId);
        if (lastSent != null && lastSent.PlanetId == snapshot.PlanetId &&
            lastSent.NodeIds.SequenceEqual(snapshot.NodeIds)) return;

        lastSent = snapshot;
        Multiplayer.Session.Network.SendPacket(snapshot);
    }

    public void ClearLocalState()
    {
        lock (localStateLock)
        {
            localStateGeneration++;
            if (TryGetLocalPlayerId(out var localPlayerId)) state.RemovePlayer(localPlayerId);
        }
    }

    public bool ApplyRemoteState(PowerTowerChargerUpdate snapshot)
    {
        // Defensive rather than load-bearing: the host no longer echoes an author's own snapshot
        // back (see PowerTowerChargerUpdateProcessor), but a self-attributed packet must still not
        // be able to rewind the local simulation, which is the freshest source for it, to an older
        // tick. RemovePlayer broadcasts are the other path that reaches here with our own id.
        if (TryGetLocalPlayerId(out var localPlayerId) && snapshot.PlayerId == localPlayerId) return false;
        return state.Replace(snapshot.PlayerId, snapshot.PlanetId, snapshot.NodeIds);
    }

    public int GetChargerCount(int planetId, int nodeId) => state.GetChargerCount(planetId, nodeId);

    public bool IsLocalCharging(int planetId, int nodeId) =>
        TryGetLocalPlayerId(out var localPlayerId) && state.IsCharging(localPlayerId, planetId, nodeId);

    public PowerTowerChargerUpdate GetPlayerState(ushort playerId) => state.GetPlayerState(playerId);

    public void SendSnapshot(INebulaeConnection connection)
    {
        foreach (var snapshot in state.GetSnapshot()) connection.SendPacket(snapshot);
    }

    public void RemovePlayer(ushort playerId)
    {
        if (state.RemovePlayer(playerId) && Multiplayer.Session.IsServer)
        {
            Multiplayer.Session.Network.SendPacket(new PowerTowerChargerUpdate(playerId, -1, []));
        }
    }

    public void RemoveNode(int planetId, int nodeId)
    {
        PowerTowerChargerUpdate[] changes;
        lock (localStateLock)
        {
            localStateGeneration++;
            changes = state.RemoveNode(planetId, nodeId);
        }
        if (!Multiplayer.Session.IsServer) return;
        foreach (var snapshot in changes) Multiplayer.Session.Network.SendPacket(snapshot);
    }
}
