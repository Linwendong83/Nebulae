#region

using System;
using HarmonyLib;

#endregion

namespace NebulaePatcher.Patches.Dynamic.World;

[HarmonyPatch(typeof(ScatterTaskContext))]
internal class ScatterTaskContext_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(ScatterTaskContext.ResetFrame), new Type[] { typeof(int), typeof(int) })]
    public static bool ResetFrame_Prefix(ScatterTaskContext __instance, int _batchCount, int _workerThreadCount)
    {
        ResetFrame(__instance, _batchCount, _workerThreadCount);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ScatterTaskContext.ResetFrame), new Type[] { typeof(long), typeof(int), typeof(int) })]
    public static bool ResetFrameWithTime_Prefix(ScatterTaskContext __instance, long _timei, int _batchCount, int _workerThreadCount)
    {
        ResetFrame(__instance, _batchCount, _workerThreadCount);
        __instance.batchOffset = (int)(_timei % __instance.batchCount);
        return false;
    }

    private static void ResetFrame(ScatterTaskContext context, int batchCount, int workerThreadCount)
    {
        // Preserve the existing empty-factory guard for the rotating overload.
        if (batchCount == 0) batchCount = 1;
        // Include the prefix-sum sentinel. Inactive capacity is never read; a later
        // frame with more batches clears its whole active range before collecting.
        Array.Clear(context.ordinals, 0, batchCount + 1);
        context.batchCount = batchCount;
        context.workerThreadCount = workerThreadCount;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ScatterTaskContext.CalculateBatchIdRange))]
    public static bool CalculateBatchIdRange_Prefix(ScatterTaskContext __instance, int threadContextIndex)
    {
        ref var context = ref __instance.threadContexts[threadContextIndex];
        // Empty worker ranges have no batch/id consumers, including Redispatch.
        if (context.ordinalBegin >= context.ordinalEnd) return false;

        var ordinals = __instance.ordinals;
        var count = __instance.batchCount + 1;
        // Keep the native boundary comparisons: an end exactly on a batch boundary
        // belongs to that batch's exclusive id end, even across empty batches.
        for (var i = 1; i < count; i++)
        {
            if (ordinals[i] <= context.ordinalBegin) continue;
            context.batchBegin = i - 1;
            context.idBegin = context.ordinalBegin - ordinals[i - 1] + 1;
            break;
        }
        for (var i = context.batchBegin + 1; i < count; i++)
        {
            if (ordinals[i] < context.ordinalEnd) continue;
            context.batchEnd = i - 1;
            context.idEnd = context.ordinalEnd - ordinals[i - 1] + 1;
            break;
        }
        return false;
    }
}
