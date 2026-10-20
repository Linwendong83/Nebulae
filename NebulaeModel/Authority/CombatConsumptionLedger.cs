using System;
using System.Collections.Generic;

namespace NebulaeModel.Authority;

/// <summary>Cumulative combat debits survive duplicate deliveries and stale personal checkpoints.</summary>
internal sealed class CombatConsumptionLedger
{
    private readonly Dictionary<int, int> totals = new();
    public IReadOnlyDictionary<int, int> Totals => totals;
    public void Debit(int itemId, int count)
    {
        if (itemId <= 0 || count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        totals.TryGetValue(itemId, out var value);
        totals[itemId] = checked(value + count);
    }
    public int Pending(int itemId, int acknowledged)
    {
        totals.TryGetValue(itemId, out var total);
        if (acknowledged < 0 || acknowledged > total) throw new ArgumentOutOfRangeException(nameof(acknowledged));
        return total - acknowledged;
    }
    public static bool TryDelta(int previous, int current, out int delta)
    {
        delta = 0;
        if (previous < 0 || current < previous) return false;
        delta = current - previous;
        return true;
    }
}
