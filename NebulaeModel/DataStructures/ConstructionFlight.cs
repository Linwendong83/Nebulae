using System.Collections.Generic;

namespace NebulaeModel.DataStructures;

internal static class ConstructionFlight
{
    public static bool Contains(int head, int next1, int next2, int next3, int target) =>
        head == target || next1 == target || next2 == target || next3 == target;

    public static bool ValidTargets(int[] targets, long[] instances)
    {
        if (targets == null || instances == null || targets.Length != 4 || instances.Length != 4 || targets[0] >= 0) return false;
        var seen = new HashSet<int>();
        for (var i = 0; i < 4; i++)
            if (targets[i] == 0 ? instances[i] != 0 : targets[i] >= 0 || targets[i] == int.MinValue || instances[i] <= 0 || !seen.Add(targets[i])) return false;
        return true;
    }
}
