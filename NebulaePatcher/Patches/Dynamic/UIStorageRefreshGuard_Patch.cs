using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch]
internal static class UIStorageRefreshGuard_Patch
{
    [HarmonyTargetMethods]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members",
        Justification = "Harmony discovers this target provider by reflection when processing the patch class.")]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[] { "OnGridMouseDown", "OnGridMouseUp", "OnGridSplitMouseUp", "HandTake", "HandPut", "OnSort" })
            yield return AccessTools.Method(typeof(UIStorageGrid), name);
    }
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    public static bool Prefix(UIStorageGrid __instance)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsClient) return true;
        var storage = Multiplayer.Session.Storage;
        // Keep cached contents readable, but do not transfer items from an unconfirmed baseline.
        return !storage.RefreshPending || !ReferenceEquals(__instance, storage.ActiveUIStorageGrid);
    }
}

[HarmonyPatch(typeof(UIStorageGrid), nameof(UIStorageGrid.TransferToOther))]
internal static class UIStorageRefreshTransfer_Patch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    public static bool Prefix(UIStorageGrid __instance, UIStorageGrid other) =>
        UIStorageRefreshGuard_Patch.Prefix(__instance) && UIStorageRefreshGuard_Patch.Prefix(other);
}
