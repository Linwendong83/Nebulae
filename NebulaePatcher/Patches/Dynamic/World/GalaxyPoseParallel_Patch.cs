using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic.World;

internal static class GalaxyWork
{
    internal const int ParallelThreshold = 256;
    internal static void Run(int count, Action<int, int> range, int minimumPerWorker = 32, int workerLimit = 16)
    {
        // Cheap coordinate writes need larger batches than native orbital calculations.
        var workers = Math.Min(Math.Min(workerLimit, Environment.ProcessorCount), count / minimumPerWorker);
        if (workers < 2 || count < ParallelThreshold) { range(0, count); return; }
        // Callers keep dependent outputs on one worker; batches write disjoint ranges.
        Parallel.For(0, workers, worker => range(count * worker / workers, count * (worker + 1) / workers));
    }
}

// Orbital poses depend on the parent planet's pose. Partition by estimated planet
// work, but move every boundary to a whole star so moons stay after their parent.
internal sealed class GalaxyPosePlan
{
    private readonly int[] starts;
    internal readonly PlanetPoseReuse[][] Poses;
    internal readonly int Total;
    internal GalaxyPosePlan(StarData[] stars, int count)
    {
        starts = new int[count];
        Poses = new PlanetPoseReuse[count][];
        for (var i = 0; i < count; i++)
        {
            starts[i] = Total; Total += stars[i].planetCount;
            Poses[i] = new PlanetPoseReuse[stars[i].planetCount];
        }
    }

    internal int StarAt(int offset)
    {
        var low = 0; var high = starts.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (starts[middle] < offset) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}

[HarmonyPatch(typeof(GalaxyData), nameof(GalaxyData.UpdatePoses))]
internal static class GalaxyPoseParallel_Patch
{
    private static readonly ConditionalWeakTable<GalaxyData, GalaxyPosePlan> plans = new();
    internal static long ParallelUpdates;
    [HarmonyPrefix]
    internal static bool Prefix(GalaxyData __instance, double time)
    {
        if (__instance.starCount < GalaxyWork.ParallelThreshold || Environment.ProcessorCount < 2) return true;
        var stars = __instance.stars;
        var plan = plans.GetValue(__instance, galaxy => new GalaxyPosePlan(galaxy.stars, galaxy.starCount));
        GalaxyWork.Run(plan.Total, (start, end) =>
        {
            var last = plan.StarAt(end);
            for (var i = plan.StarAt(start); i < last; i++)
                for (var j = 0; j < stars[i].planetCount; j++)
                {
                    var planet = stars[i].planets[j];
                    if (GalaxyOptimization.Enabled && GalaxyOptimization.PoseReuseEnabled) (plan.Poses[i][j] ??= new PlanetPoseReuse()).Update(planet, time);
                    else planet.UpdateRuntimePose(time);
                }
        }, workerLimit: Multiplayer.IsDedicated && plan.Total >= 16384 ? 24 : 16);
        ParallelUpdates++;
        return false;
    }
}
