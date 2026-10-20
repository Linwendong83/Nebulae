using System;
using NebulaeModel.Packets.Factory.Belt;

namespace NebulaeModel.Utils;

/// <summary>Visits every receipt and consumes its quantity, rather than stopping at its first cargo.</summary>
internal static class BeltPickupBatch
{
    public static void Apply(BeltUpdate[] updates, Func<BeltUpdate, int, int> pick, Action<BeltUpdate, int> missing)
    {
        if (updates == null || updates.Length > 65536) return;
        foreach (var update in updates)
        {
            if (update.BeltId <= 0 || update.ItemId <= 0 || update.Count <= 0 || update.Count > 100) continue;
            var remaining = update.Count;
            while (remaining > 0)
            {
                var removed = pick(update, remaining);
                if (removed <= 0) break;
                if (removed > remaining) throw new InvalidOperationException("Belt receipt removed excess cargo.");
                remaining -= removed;
            }
            if (remaining > 0) missing(update, remaining);
        }
    }
    public static (int Taken, int Count, int Inc) SplitStack(int stack, int inc, int needed)
    {
        if (stack <= 0 || needed <= 0 || inc < 0) throw new ArgumentOutOfRangeException(nameof(stack));
        var taken = Math.Min(stack, needed); var remaining = stack - taken;
        var takenInc = inc / stack * taken + Math.Max(0, inc % stack - remaining);
        return (taken, remaining, inc - takenInc);
    }
}
