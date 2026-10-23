using System;
using System.Threading.Tasks;
using HarmonyLib;

namespace NebulaePatcher.Patches.Dynamic;

internal static class GalaxyWork
{
    internal const int ParallelThreshold = 256;
    internal static void Run(int count, Action<int, int> range)
    {
        var workers = Math.Min(8, Environment.ProcessorCount);
        if (workers < 2 || count < ParallelThreshold) { range(0, count); return; }
        // Contiguous stars share no pose output. A star and all its moons stay on one worker.
        Parallel.For(0, workers, worker => range(count * worker / workers, count * (worker + 1) / workers));
    }
}

[HarmonyPatch(typeof(GalaxyData), nameof(GalaxyData.UpdatePoses))]
internal static class GalaxyPoseParallel_Patch
{
    internal static long ParallelUpdates;
    [HarmonyPrefix]
    internal static bool Prefix(GalaxyData __instance, double time)
    {
        if (__instance.starCount < GalaxyWork.ParallelThreshold || Environment.ProcessorCount < 2) return true;
        var stars = __instance.stars;
        GalaxyWork.Run(__instance.starCount, (start, end) =>
        {
            for (var i = start; i < end; i++)
                for (var j = 0; j < stars[i].planetCount; j++) stars[i].planets[j].UpdateRuntimePose(time);
        });
        ParallelUpdates++;
        return false;
    }
}
