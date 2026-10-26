using System;

namespace NebulaeModel.DataStructures;

internal static class RepairDemand
{
    // ConstructionSystem.DetermineLaunch rounds according to the target's current HP band.
    public static int Count(int hp, int maximum, float demand, int existing)
    {
        if (float.IsNaN(demand) || float.IsInfinity(demand)) return 0;
        var ratio = maximum > 0 ? (float)hp / maximum : 1f;
        var count = (int)(demand + (ratio > .8f ? 0f : ratio > .75f ? .5f : .9999f));
        return existing < 1 ? Math.Max(1, count) : Math.Max(0, count);
    }
}
