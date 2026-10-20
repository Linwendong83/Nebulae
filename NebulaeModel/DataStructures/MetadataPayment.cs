using System;
using System.IO;

namespace NebulaeModel.DataStructures;

internal static class MetadataPayment
{
    public static int[] Prepare(int[] current, int[] cost, long[] available)
    {
        if (current.Length != 6 || cost.Length != 6 || available.Length != 6)
            throw new ArgumentException("A payment requires six matrix balances");
        var after = new int[6];
        for (var i = 0; i < 6; i++)
        {
            if (cost[i] < 0 || cost[i] > available[i] || current[i] < 0 || (long)current[i] + cost[i] > 2000000000)
                throw new InvalidOperationException("Insufficient or unrepresentable metadata balance");
            after[i] = current[i] + cost[i];
        }
        return after;
    }

    public static int[] RecoverDebit(int[] current, int[] before, int[] after)
    {
        if (current.Length != 6 || before.Length != 6 || after.Length != 6) throw new InvalidDataException("Invalid payment record");
        for (var i = 0; i < 6; i++)
            if (current[i] != before[i] && current[i] != after[i])
                throw new InvalidDataException("Property wallet changed during an unfinished payment");
        return (int[])after.Clone();
    }
}
