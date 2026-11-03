using System;
using System.Collections.Generic;

namespace NebulaeWorld.Factory;

/// <summary>Resets the builder assignment the native launch pass consumed, without rewriting every prebuild each frame.</summary>
/// <remarks>
/// <para>
/// Vanilla never clears <c>builderModuleId</c>/<c>builderValue</c> after a frame - only the sites that
/// actually launched are retired - so an assignment could survive into the next launch pass and eject a
/// drone for a target whose claim was withdrawn meanwhile. Multiplayer therefore has to reset the
/// assignment before each pass, and doing it with a full pool scan costs one pass over every unfinished
/// prebuild, on every loaded factory, every frame. That is exactly the state a large blueprint leaves
/// behind, and it lasts until the blueprint finishes.
/// </para>
/// <para>
/// <c>ConstructionModuleComponent.SearchBuildTargets</c> is the only writer of those two fields, so the
/// sites it touched are recorded and only those are reset. A full sweep still runs when a factory is
/// first seen and every <see cref="FullSweepTicks"/> ticks, which covers saves and factory snapshots
/// that import an assignment directly. Clients never run the search, so they keep the full sweep (it
/// skips sites that are already clear, which is almost all of them).
/// </para>
/// </remarks>
internal static class PrebuildAssignmentSweep
{
    private const int FullSweepTicks = 600;
    // Clients never run the native target search, so their sweep only clears assignments that came
    // from a host snapshot. Doing that every frame walked the whole prebuild pool of every loaded
    // factory for nothing; thirty ticks is well below the time an assignment can influence a launch.
    private const int ClientFullSweepTicks = 30;
    private const int MaxTrackers = 256;
    private static readonly Dictionary<PlanetFactory, Tracker> trackers = new();

    private sealed class Tracker
    {
        public readonly HashSet<int> Assigned = new();
        public long NextFullSweep;
        public bool Seeded;
        public bool LastIncremental;
    }

    internal static void Reset() => trackers.Clear();

    /// <summary>Remembers a site whose assignment the native search just wrote.</summary>
    internal static void Record(PlanetFactory factory, ConstructionModuleComponent module)
    {
        if (factory?.prebuildPool == null || module?.buildTargets == null || module.buildTargets.Length == 0) return;
        var id = module.buildTargets[0].objectId;
        var pool = factory.prebuildPool;
        if (id <= 0 || id >= factory.prebuildCursor || id >= pool.Length) return;
        if (pool[id].id != id || pool[id].builderModuleId == 0) return;
        // The set is bounded like the number of modules, but a forced search may mention several
        // sites per module; never let one frame grow it past a sweep's worth of work.
        var tracker = TrackerFor(factory);
        if (tracker.Assigned.Count >= factory.prebuildCursor) return;
        tracker.Assigned.Add(id);
    }

    /// <summary>Resets the assignments the previous launch pass consumed.</summary>
    /// <param name="incremental">
    /// True when this peer runs the native target search and therefore records what it assigned.
    /// </param>
    internal static void BeginFrame(PlanetFactory factory, bool incremental)
    {
        if (factory?.prebuildPool == null) return;
        var tracker = TrackerFor(factory);
        var tick = GameMain.gameTick;
        // A tracker belongs to one role at a time; switching roles (host hand-off, re-import) must
        // re-read the whole pool like a fresh factory.
        var due = !tracker.Seeded || tracker.LastIncremental != incremental || tick >= tracker.NextFullSweep;
        tracker.LastIncremental = incremental;
        if (!incremental)
        {
            if (!due) return;
            Sweep(factory);
            tracker.Seeded = true;
            tracker.NextFullSweep = tick + ClientFullSweepTicks;
            tracker.Assigned.Clear();
            return;
        }
        if (due)
        {
            Sweep(factory);
            tracker.Seeded = true;
            tracker.NextFullSweep = tick + FullSweepTicks;
            tracker.Assigned.Clear();
            return;
        }
        foreach (var id in tracker.Assigned) Clear(factory, id);
        tracker.Assigned.Clear();
    }

    private static Tracker TrackerFor(PlanetFactory factory)
    {
        if (trackers.TryGetValue(factory, out var tracker)) return tracker;
        if (trackers.Count >= MaxTrackers) trackers.Clear();
        trackers[factory] = tracker = new Tracker();
        return tracker;
    }

    private static void Clear(PlanetFactory factory, int id)
    {
        var pool = factory.prebuildPool;
        if (id <= 0 || id >= factory.prebuildCursor || id >= pool.Length) return;
        ref var site = ref pool[id];
        if (site.id != id || site.builderLaunched) return;
        site.builderModuleId = 0;
        site.builderValue = 0f;
    }

    private static void Sweep(PlanetFactory factory)
    {
        var pool = factory.prebuildPool;
        var cursor = Math.Min(factory.prebuildCursor, pool.Length);
        for (var id = 1; id < cursor; id++)
        {
            ref var site = ref pool[id];
            if (site.id != id || site.builderLaunched) continue;
            // Most sites are already clear; skipping them keeps the whole pool out of the dirty set.
            if (site.builderModuleId == 0 && site.builderValue == 0f) continue;
            site.builderModuleId = 0;
            site.builderValue = 0f;
        }
    }
}
