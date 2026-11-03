#region

using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic.UI;

/// <summary>
/// Keeps the planetary shield panel off a factory that cannot fill it in.
/// </summary>
/// <remarks>
/// <para>
/// The panel remembers the factory it was opened for and refreshes those cached references every
/// frame. A multiplayer client imports the factories it visits: an import that has not built the
/// shield field yet, or a planet whose factory was released while the panel still names it, leaves
/// <see cref="UIPlanetShieldDetail.Refresh"/> dereferencing a null shield field, null physics
/// arguments or a null power system. Vanilla never expected the factory under an open panel to be
/// replaced — in a single-player game it cannot be — so it has no guard of its own.
/// </para>
/// <para>
/// Closing the panel is the honest answer: it would be reopened next frame by
/// <see cref="UIPlanetShieldDetail.DetermineVisible"/> with the same missing dependencies, and a
/// panel showing stale numbers from a released factory is worse than no panel.
/// </para>
/// </remarks>
[HarmonyPatch(typeof(UIPlanetShieldDetail))]
internal class UIPlanetShieldDetail_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIPlanetShieldDetail.Refresh))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static bool Refresh_Prefix(UIPlanetShieldDetail __instance)
    {
        if (!Multiplayer.IsActive) return true;
        if (CanRefresh(__instance.planet, __instance.factory, __instance.field, __instance.factoryModel)) return true;
        __instance._Close();
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIPlanetShieldDetail.DetermineVisible))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static bool DetermineVisible_Prefix(UIPlanetShieldDetail __instance)
    {
        if (!Multiplayer.IsActive) return true;

        // Everything vanilla itself refuses to show, it already closes on: leave that path alone.
        var planet = __instance.planet;
        if (planet == null || planet.type == EPlanetType.Gas || planet.factory == null ||
            planet.factory.defenseSystem == null || planet.factory.defenseSystem.fieldGenerators.count == 0) return true;

        // DetermineVisible would open the panel and hand Refresh exactly these references.
        if (CanRefresh(planet, planet.factory, planet.factory.planetATField, planet.factoryModel)) return true;
        __instance._Close();
        return false;
    }

    /// <summary>Every reference <see cref="UIPlanetShieldDetail.Refresh"/> dereferences unconditionally.</summary>
    internal static bool CanRefresh(PlanetData planet, PlanetFactory factory, PlanetATField field, FactoryModel factoryModel)
    {
        if (planet == null || factory == null || field == null || factory.powerSystem == null) return false;
        if (field.physicsArgs == null || field.physicsArgs.Length < 2 || field.physicsMeshVertsOriginal == null) return false;
        // Refresh reads the factory model only for a loaded planet, and only to mirror the checkbox.
        if (planet.factoryLoaded && factoryModel == null) return false;
        return true;
    }
}
