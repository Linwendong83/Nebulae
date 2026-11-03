using System;
using HarmonyLib;

namespace NebulaePatcher.Patches.Dynamic.Game;

[HarmonyPatch(typeof(BatchTaskContext), nameof(BatchTaskContext.ResetFrame))]
internal static class BatchTaskContext_Patch
{
    [HarmonyPrefix]
    internal static bool Prefix(BatchTaskContext __instance, int _batchCount, int _workerThreadCount)
    {
        // Capacity follows the galaxy's planet slots; only live factories are batches.
        Array.Clear(__instance.batchValues, 0, _batchCount);
        for (var i = 0; i < _batchCount; i++) __instance.batchIndices[i] = i;
        __instance.batchCursor = 0;
        __instance.batchCount = _batchCount;
        __instance.workerThreadCount = _workerThreadCount;
        return false;
    }
}
