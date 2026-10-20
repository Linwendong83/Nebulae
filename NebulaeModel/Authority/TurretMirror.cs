using System;
using System.Collections.Generic;
using NebulaeModel.Packets.Authority;

namespace NebulaeModel.Authority;

/// <summary>Deferred local component binding; lifecycle owns buildings, never this mirror.</summary>
internal sealed class TurretMirror
{
    private readonly AuthorityEpoch epoch;
    private readonly Func<ObjectKey, TurretState, bool> apply;
    private readonly Action<ObjectKey> clear;
    private readonly Dictionary<ObjectKey, TurretState> states = new();
    private readonly Dictionary<(int Planet, int Entity), ObjectKey> owners = new();
    private readonly Dictionary<(int Planet, int Entity), long> generations = new();
    private readonly HashSet<ObjectKey> pending = new();
    public TurretMirror(AuthorityEpoch epoch, Func<ObjectKey, TurretState, bool> apply, Action<ObjectKey> clear)
    { this.epoch = epoch; this.apply = apply; this.clear = clear; }
    public int PendingCount => pending.Count;
    public bool TryGet(int planet, int entity, out ObjectKey key, out TurretState state)
    {
        state = default;
        return owners.TryGetValue((planet, entity), out key) && !pending.Contains(key) && states.TryGetValue(key, out state);
    }

    public IEnumerable<int> PendingPlanets
    {
        get { var planets = new HashSet<int>(); foreach (var key in pending) planets.Add(key.Scope); return planets; }
    }
    public bool Put(ObjectKey key, TurretState state)
    {
        if (!key.IsValid || key.Epoch != epoch || key.Kind != PoolKind.Turret || key.NativeId != state.EntityId) return false;
        var slot = (key.Scope, key.NativeId);
        if (generations.TryGetValue(slot, out var generation) && (generation > key.Generation || generation == key.Generation && !owners.ContainsKey(slot))) return false;
        if (owners.TryGetValue(slot, out var old) && old != key) { states.Remove(old); pending.Remove(old); }
        generations[slot] = key.Generation; owners[slot] = key; states[key] = state;
        if (apply(key, state)) pending.Remove(key); else pending.Add(key);
        return true;
    }
    public void Remove(ObjectKey key)
    {
        states.Remove(key); pending.Remove(key);
        var slot = (key.Scope, key.NativeId);
        if (!owners.TryGetValue(slot, out var owner) || owner != key) return;
        owners.Remove(slot); clear(key);
    }
    public void Install(int planet, IReadOnlyList<(ObjectKey Key, TurretState State)> members)
    {
        var keep = new HashSet<ObjectKey>(); foreach (var member in members) keep.Add(member.Key);
        foreach (var key in new List<ObjectKey>(states.Keys)) if (key.Scope == planet && !keep.Contains(key)) Remove(key);
        foreach (var member in members)
        {
            var slot = (member.Key.Scope, member.Key.NativeId);
            // A validated baseline can restore a live key after a local factory re-import.
            if (generations.TryGetValue(slot, out var generation) && generation == member.Key.Generation) generations.Remove(slot);
            Put(member.Key, member.State);
        }
    }
    public void Retry(int planet)
    {
        foreach (var key in new List<ObjectKey>(pending))
            if (key.Scope == planet && states.TryGetValue(key, out var state) && apply(key, state)) pending.Remove(key);
    }
    public void ClearScope(int planet)
    {
        foreach (var key in new List<ObjectKey>(states.Keys)) if (key.Scope == planet) Remove(key);
    }
}
