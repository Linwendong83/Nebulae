using System;
using System.Collections.Generic;

namespace NebulaeModel.Authority;

/// <summary>Lifecycle and explicit changes are immediate; continuous state is sampled in bounded slices.</summary>
internal sealed class ReplicationCandidateIndex
{
    private const int AuditIntervalTicks = 1200;
    private readonly object gate = new();
    private readonly Dictionary<int, ObjectKey> live = new();
    private readonly HashSet<ObjectKey> dirty = new(), retired = new();
    private readonly HashSet<ObjectKey> fast = new();
    private readonly HashSet<ObjectKey> frequent = new();
    private readonly List<ObjectKey>[] unitDue = new List<ObjectKey>[EnemyStatePolicy.UnitCheckpointIntervalTicks];
    private readonly List<ObjectKey>[] due = new List<ObjectKey>[EnemyStatePolicy.CheckpointIntervalTicks];
    private object identity;
    private long auditTick;

    public void Rebuild(object poolIdentity, long tick, IReadOnlyList<ObjectKey> keys)
    {
        lock (gate)
        {
            identity = poolIdentity; auditTick = tick;
            live.Clear(); dirty.Clear(); retired.Clear();
            fast.Clear();
            frequent.Clear();
            foreach (var bucket in due) bucket?.Clear();
            foreach (var bucket in unitDue) bucket?.Clear();
            foreach (var key in keys) Add(key, false);
        }
    }

    public bool Read(object poolIdentity, int count, long tick, List<ObjectKey> candidates, List<ObjectKey> removed)
    {
        lock (gate)
        {
            if (identity == null || !ReferenceEquals(identity, poolIdentity) || count != live.Count ||
                tick < auditTick || tick - auditTick >= AuditIntervalTicks) return false;
            candidates.AddRange(fast);
            foreach (var key in dirty) if (!fast.Contains(key)) candidates.Add(key);
            var units = unitDue[(int)(tick % unitDue.Length)];
            if (units != null)
                foreach (var key in units) if (!dirty.Contains(key) && !fast.Contains(key)) candidates.Add(key);
            var bucket = due[(int)(tick % due.Length)];
            if (bucket != null)
                foreach (var key in bucket) if (!dirty.Contains(key) && !fast.Contains(key) && !frequent.Contains(key)) candidates.Add(key);
            removed.AddRange(retired); retired.Clear();
            return true;
        }
    }

    public void Add(ObjectKey key, bool changed = true)
    {
        lock (gate)
        {
            if (live.TryGetValue(key.NativeId, out var previous))
            {
                if (previous == key) { if (changed) dirty.Add(key); return; }
                Remove(previous);
            }
            live[key.NativeId] = key;
            (due[key.NativeId % due.Length] ??= new List<ObjectKey>()).Add(key);
            if (changed) dirty.Add(key);
        }
    }

    public void Remove(ObjectKey key)
    {
        lock (gate)
        {
            if (live.TryGetValue(key.NativeId, out var current) && current == key) live.Remove(key.NativeId);
            due[key.NativeId % due.Length]?.Remove(key); dirty.Remove(key); fast.Remove(key); retired.Add(key);
            frequent.Remove(key); unitDue[key.NativeId % unitDue.Length]?.Remove(key);
        }
    }
    public void SetFast(ObjectKey key, bool value) { lock (gate) { if (value) fast.Add(key); else fast.Remove(key); } }
    public void SetCadence(ObjectKey key, bool fastValue, bool frequentValue)
    {
        lock (gate)
        {
            if (fastValue) fast.Add(key); else fast.Remove(key);
            UpdateFrequent(key, frequentValue);
        }
    }
    public void SetFrequent(ObjectKey key, bool value)
    {
        lock (gate) UpdateFrequent(key, value);
    }
    private void UpdateFrequent(ObjectKey key, bool value)
    {
        if (value)
        {
            if (frequent.Add(key)) (unitDue[key.NativeId % unitDue.Length] ??= new()).Add(key);
        }
        else if (frequent.Remove(key)) unitDue[key.NativeId % unitDue.Length]?.Remove(key);
    }
    public void Changed(int id) { lock (gate) { if (live.TryGetValue(id, out var key)) dirty.Add(key); } }
    public void Sampled(ObjectKey key) { lock (gate) dirty.Remove(key); }
}
