using System;
using System.Collections.Generic;
using NebulaeModel.Packets.GameHistory;

namespace NebulaeModel.Authority;

/// <summary>Material points consumed locally but not yet committed to global research.</summary>
internal sealed class ResearchContributionBuffer
{
    internal sealed class Batch
    {
        public int Tech, Level;
        public long Hashes;
        public readonly Dictionary<int, int> Costs = new();
        public ResearchContributionIntent Intent => new(Tech, Level, Hashes);
    }
    private readonly Queue<Batch> queued = new();
    private readonly Dictionary<int, int> reserved = new();
    private Batch tail;
    public Batch Active { get; private set; }
    public long Hashes { get; private set; }
    public bool CanRecord(int tech, int level, int speed, int[] items, int[] costs)
    {
        if (speed <= 0 || Hashes + speed > ResearchContributionIntent.MaxHashes ||
            queued.Count >= 32 && (tail == null || tail.Tech != tech || tail.Level != level) ||
            items == null || costs == null || items.Length != costs.Length || items.Length > 32) return false;
        for (var i = 0; i < items.Length; i++)
        {
            reserved.TryGetValue(items[i], out var points);
            if (items[i] <= 0 || costs[i] <= 0 || (long)points + (long)costs[i] * speed > 720_000_000) return false;
        }
        return true;
    }
    public void Record(int tech, int level, long hashes, int[] items, int[] costs)
    {
        if (hashes > int.MaxValue || !CanRecord(tech, level, (int)hashes, items, costs)) throw new InvalidOperationException("Research contribution buffer is full.");
        if (tail == null || tail.Tech != tech || tail.Level != level)
        {
            tail = new Batch { Tech = tech, Level = level };
            for (var i = 0; i < items.Length; i++) tail.Costs.Add(items[i], costs[i]);
            queued.Enqueue(tail);
        }
        tail.Hashes += hashes; Hashes += hashes;
        foreach (var cost in tail.Costs)
        {
            reserved.TryGetValue(cost.Key, out var points);
            reserved[cost.Key] = checked(points + (int)(cost.Value * hashes));
        }
    }
    public Batch Peek() => queued.Count > 0 ? queued.Peek() : null;
    public long Outstanding(int tech, int level)
    {
        var hashes = Active != null && Active.Tech == tech && Active.Level == level ? Active.Hashes : 0;
        foreach (var batch in queued) if (batch.Tech == tech && batch.Level == level) hashes += batch.Hashes;
        return hashes;
    }
    public void Begin()
    {
        if (Active != null || queued.Count == 0) throw new InvalidOperationException("Invalid research batch dispatch.");
        Active = queued.Dequeue(); if (ReferenceEquals(Active, tail)) tail = null;
    }
    public Dictionary<int, int> Complete(long accepted)
    {
        var batch = Active;
        if (batch == null || accepted < 0 || accepted > batch.Hashes) throw new InvalidOperationException("Invalid research receipt.");
        var refund = new Dictionary<int, int>();
        foreach (var cost in batch.Costs)
        {
            reserved[cost.Key] -= checked((int)(cost.Value * batch.Hashes));
            var points = checked((int)(cost.Value * (batch.Hashes - accepted)));
            if (points > 0) refund.Add(cost.Key, points);
        }
        Hashes -= batch.Hashes; Active = null;
        return refund;
    }
    public void Overlay(IDictionary<int, int> checkpoint)
    {
        foreach (var item in reserved)
        {
            checkpoint.TryGetValue(item.Key, out var points);
            checkpoint[item.Key] = checked(points + item.Value);
        }
    }
    public void Clear() { queued.Clear(); reserved.Clear(); Active = tail = null; Hashes = 0; }
}
