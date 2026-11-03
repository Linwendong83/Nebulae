#region

using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeModel.Utils;

#endregion

namespace NebulaePatcher.Patches.Dynamic.UI;

/// <summary>
/// Keeps the vein detail panel from unsubscribing through a factory that has already been released.
/// </summary>
/// <remarks>
/// <para>
/// A multiplayer client unloads every factory when it leaves a star system, but the vein panel still
/// names the planet it was showing. <see cref="UIVeinDetail.SetInspectPlanet"/> then runs
/// <c>inspectPlanet.factory.onVeinGroupsRecalculated -= ...</c> before it can switch to the next
/// planet, and the released planet's null factory throws inside that unsubscribe. Vanilla never
/// releases a factory while the panel is open, so it has no guard of its own.
/// </para>
/// <para>
/// Forgetting the stale planet first lets the method reach its own cleanup: the remaining tips are
/// freed and, if a planet is still requested, its factory is subscribed normally.
/// </para>
/// </remarks>
[HarmonyPatch(typeof(UIVeinDetail))]
internal class UIVeinDetail_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIVeinDetail.SetInspectPlanet))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void SetInspectPlanet_Prefix(UIVeinDetail __instance)
    {
        if (!IsStale(__instance.inspectPlanet)) return;
        __instance.SetHiddenProperty(nameof(UIVeinDetail.inspectPlanet), null);
    }

    /// <summary>A planet released behind the panel still occupies the inspect slot.</summary>
    internal static bool IsStale(PlanetData planet) => planet != null && planet.factory == null;
}
