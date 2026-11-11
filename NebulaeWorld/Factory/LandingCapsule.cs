using System;
using NebulaeModel.Logger;

namespace NebulaeWorld.Factory;

internal static class LandingCapsule
{
    internal const int ProtoId = 9999;
    [ThreadStatic] private static bool repairing;
    internal static bool IsRepairing => repairing;

    internal static bool Exists(PlanetFactory factory)
    {
        if (factory?.vegePool == null) return false;
        for (var i = 1; i < Math.Min(factory.vegeCursor, factory.vegePool.Length); i++)
            if (factory.vegePool[i].id == i && factory.vegePool[i].protoId == ProtoId) return true;
        return false;
    }

    internal static int RepairDuplicates(PlanetFactory factory)
    {
        if (factory?.vegePool == null) return 0;
        var removed = 0;
        var pool = factory.vegePool;
        var end = Math.Min(factory.vegeCursor, pool.Length);
        for (var i = 1; i < end; i++)
        {
            if (pool[i].id != i || pool[i].protoId != ProtoId) continue;
            for (var j = i + 1; j < end; j++)
            {
                if (pool[j].id != j || pool[j].protoId != ProtoId ||
                    (pool[j].pos - pool[i].pos).sqrMagnitude > .01f) continue;
                // Repair the world before any client snapshot. Removing a duplicate
                // is not a harvest: do not credit resources or vegetation collection.
                var previous = repairing;
                try
                {
                    repairing = true;
                    using (Multiplayer.Session.Planets.IsIncomingRequest.On())
                        factory.RemoveVegeWithComponents(j);
                }
                finally { repairing = previous; }
                removed++;
            }
        }
        if (removed > 0) Log.Warn($"[capsule] removed {removed} overlapping landing capsules planet={factory.planetId}");
        return removed;
    }
}
