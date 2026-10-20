using System;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel.Packets.Factory.PowerTower;

namespace NebulaeModel.DataStructures;

/// <summary>Session-only, planet-qualified charging membership. Snapshots never expose mutable state.</summary>
internal sealed class PowerTowerChargingState
{
    private readonly object syncRoot = new();
    private readonly Dictionary<ushort, PowerTowerChargerUpdate> players = [];
    private readonly Dictionary<long, int> chargerCounts = [];

    public bool Replace(ushort playerId, int planetId, IEnumerable<int> nodeIds)
    {
        var nodes = planetId > 0 && nodeIds != null
            ? nodeIds.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray()
            : [];
        var snapshot = new PowerTowerChargerUpdate(playerId, nodes.Length == 0 ? -1 : planetId, nodes);
        lock (syncRoot)
        {
            return ReplaceLocked(snapshot);
        }
    }

    private bool ReplaceLocked(PowerTowerChargerUpdate snapshot)
    {
        players.TryGetValue(snapshot.PlayerId, out var previous);
        if (previous == null && snapshot.NodeIds.Length == 0 ||
            previous != null && previous.PlanetId == snapshot.PlanetId && previous.NodeIds.SequenceEqual(snapshot.NodeIds))
        {
            return false;
        }

        if (previous != null)
        {
            foreach (var nodeId in previous.NodeIds)
            {
                var key = GetKey(previous.PlanetId, nodeId);
                if (chargerCounts[key] == 1) chargerCounts.Remove(key);
                else chargerCounts[key]--;
            }
        }

        if (snapshot.NodeIds.Length == 0)
        {
            players.Remove(snapshot.PlayerId);
        }
        else
        {
            players[snapshot.PlayerId] = snapshot;
            foreach (var nodeId in snapshot.NodeIds)
            {
                var key = GetKey(snapshot.PlanetId, nodeId);
                chargerCounts.TryGetValue(key, out var count);
                chargerCounts[key] = count + 1;
            }
        }
        return true;
    }

    public int GetChargerCount(int planetId, int nodeId)
    {
        lock (syncRoot)
        {
            return chargerCounts.TryGetValue(GetKey(planetId, nodeId), out var count) ? count : 0;
        }
    }

    public bool IsCharging(ushort playerId, int planetId, int nodeId)
    {
        lock (syncRoot)
        {
            return players.TryGetValue(playerId, out var state) && state.PlanetId == planetId &&
                   Array.BinarySearch(state.NodeIds, nodeId) >= 0;
        }
    }

    public PowerTowerChargerUpdate GetPlayerState(ushort playerId)
    {
        lock (syncRoot)
        {
            return players.TryGetValue(playerId, out var state)
                ? Copy(state) : new PowerTowerChargerUpdate(playerId, -1, []);
        }
    }

    public PowerTowerChargerUpdate[] GetSnapshot()
    {
        lock (syncRoot)
        {
            return players.Values.Select(Copy).ToArray();
        }
    }

    public PowerTowerChargerUpdate[] RemoveNode(int planetId, int nodeId)
    {
        lock (syncRoot)
        {
            var changes = players.Values.Where(state => state.PlanetId == planetId &&
                    Array.BinarySearch(state.NodeIds, nodeId) >= 0)
                .Select(state => new PowerTowerChargerUpdate(state.PlayerId, planetId,
                    state.NodeIds.Where(id => id != nodeId).ToArray())).ToArray();
            foreach (var change in changes)
            {
                if (change.NodeIds.Length == 0) change.PlanetId = -1;
                ReplaceLocked(change);
            }
            return changes.Select(Copy).ToArray();
        }
    }

    public bool RemovePlayer(ushort playerId) => Replace(playerId, -1, []);

    public void Clear()
    {
        lock (syncRoot)
        {
            players.Clear();
            chargerCounts.Clear();
        }
    }

    private static long GetKey(int planetId, int nodeId) => ((long)planetId << 32) | (uint)nodeId;

    private static PowerTowerChargerUpdate Copy(PowerTowerChargerUpdate state) =>
        new(state.PlayerId, state.PlanetId, (int[])state.NodeIds.Clone());
}
