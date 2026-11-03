using System;
using HarmonyLib;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic.World;

/// <summary>
/// The native cursor test projects every star to the screen and keeps the nearest one inside a
/// 600 squared pixel radius. Only stars already inside that radius can change the running
/// distance and depth thresholds, so the cached projections of the previous LateUpdate select
/// an ordered subset that yields the same star. The native body runs unchanged on that subset.
/// </summary>
[HarmonyPatch(typeof(UIStarmap), "MouseHoverCheck")]
internal static class StarmapHover_Patch
{
    [HarmonyPrefix]
    internal static bool Prefix(UIStarmap __instance)
    {
        var map = __instance;
        if (!StellarPresentation.Active || map == null || UIStarmap.isChangingToMilkyWay) return true;
        if (map.hoverOnMarkerIcon || map.screenCamera == null) return true;
        // A collider under the cursor sends the native body down its object branch, which
        // searches the whole star array; that path is left untouched.
        var mouse = (Vector3)Input.mousePosition;
        var stars = StarmapDisplay_Patch.HoverCandidates(map, mouse, out _);
        if (stars == null) return true;
        if (Physics.Raycast(map.screenCamera.ScreenPointToRay(mouse), out _, 300f, 1048576)) return true;
        var original = map.starUIs;
        if (original == null) return true;
        try
        {
            map.starUIs = stars;
            Native(map);
        }
        finally { map.starUIs = original; }
        return false;
    }

    [HarmonyReversePatch(HarmonyReversePatchType.Original)]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Native(UIStarmap instance)
        => throw new NotSupportedException("Native hover fallback was not installed.");
}
