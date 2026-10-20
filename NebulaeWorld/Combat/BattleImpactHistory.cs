using System;
using System.Collections.Generic;
using NebulaeModel.DataStructures;

namespace NebulaeWorld.Combat;

// A source can only reconcile its own hits. Index that identity before examining the
// short timing window, instead of searching every hit from every enemy per projectile.
internal sealed class BattleImpactHistory
{
    internal sealed class Entry
    {
        public BattleEffectKind Origin;
        public int Astro, Caster, Kind, Id;
        public long Tick, Born, Generation;
        public bool Authority, Matched;
    }

    private readonly Dictionary<(BattleEffectKind, int, int, long), List<Entry>> sources = new();
    private readonly List<(BattleEffectKind, int, int, long)> expired = new();
    private long prunedTick = long.MinValue;

    internal void Add(Entry entry)
    {
        var key = (entry.Origin, entry.Astro, entry.Caster, entry.Generation);
        if (!sources.TryGetValue(key, out var list)) sources.Add(key, list = new List<Entry>());
        list.Add(entry);
    }

    internal Entry Find(BattleEffectKind origin, int astro, int caster, long generation, long tick,
        bool authority, int kind = -1, bool exactKind = false)
    {
        if (!sources.TryGetValue((origin, astro, caster, generation), out var list)) return null;
        Entry best = null;
        foreach (var entry in list)
        {
            if (entry.Authority != authority || entry.Matched || Math.Abs(entry.Tick - tick) > 120 || exactKind && entry.Kind != kind) continue;
            // Authority-first matching preserves arrival order. Prediction-first matching
            // prefers the same particle subtype, then the nearest tick, as before.
            if (authority) return entry;
            if (best == null || entry.Kind == kind && best.Kind != kind ||
                (entry.Kind == kind) == (best.Kind == kind) && Math.Abs(entry.Tick - tick) < Math.Abs(best.Tick - tick)) best = entry;
        }
        return best;
    }

    internal void Prune(long tick)
    {
        if (prunedTick == tick) return;
        prunedTick = tick;
        expired.Clear();
        foreach (var pair in sources)
        {
            var list = pair.Value;
            var kept = 0;
            for (var i = 0; i < list.Count; i++)
                if (Math.Abs(tick - list[i].Tick) <= 180) list[kept++] = list[i];
            if (kept < list.Count) list.RemoveRange(kept, list.Count - kept);
            if (pair.Value.Count == 0) expired.Add(pair.Key);
        }
        foreach (var key in expired) sources.Remove(key);
    }

    internal void Clear() { sources.Clear(); expired.Clear(); prunedTick = long.MinValue; }
}
