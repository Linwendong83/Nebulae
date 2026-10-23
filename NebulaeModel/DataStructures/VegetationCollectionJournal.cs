using System;
using System.Collections.Generic;
using System.Linq;

namespace NebulaeModel.DataStructures;

// A snapshot request is an ordered barrier behind the native operation packets. Only its
// reply acknowledges predictions: an intermediate server snapshot cannot double-credit them.
internal sealed class VegetationCollectionJournal
{
    private Dictionary<(long Owner, int Proto), int> observed;
    private readonly List<(long Sequence, Dictionary<(long Owner, int Proto), int> Delta)> pending = new();
    private long acknowledgedSequence;
    public long Sequence { get; private set; }
    public long Revision { get; private set; }
    public void ObserveBefore(byte[] state) => observed ??= VegetableCollectionState.Counts(state);
    public void ObserveAfter(byte[] state)
    {
        var current = VegetableCollectionState.Counts(state);
        if (observed != null)
        {
            var delta = new Dictionary<(long, int), int>();
            foreach (var key in observed.Keys.Union(current.Keys))
            {
                observed.TryGetValue(key, out var before); current.TryGetValue(key, out var after);
                if (after != before) delta[key] = after - before;
            }
            if (delta.Count > 0) pending.Add((++Sequence, delta));
        }
        observed = current;
    }
    public bool TryReconcile(byte[] state, long acknowledged, long revision, out byte[] merged)
    {
        merged = null;
        if (revision <= Revision || acknowledged < acknowledgedSequence || acknowledged > Sequence) return false;
        var counts = VegetableCollectionState.Counts(state);
        pending.RemoveAll(x => x.Sequence <= acknowledged);
        foreach (var operation in pending)
            foreach (var item in operation.Delta)
            {
                counts.TryGetValue(item.Key, out var count);
                counts[item.Key] = (int)Math.Max(0, Math.Min(int.MaxValue, (long)count + item.Value));
            }
        merged = VegetableCollectionState.FromCounts(counts);
        observed = counts; Revision = revision; acknowledgedSequence = acknowledged;
        return true;
    }
}
