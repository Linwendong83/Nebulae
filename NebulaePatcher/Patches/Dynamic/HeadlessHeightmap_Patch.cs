using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.RenderLocalPlanetHeightmap))]
internal static class HeadlessHeightmap_Patch
{
    // Flattening has already updated the CPU terrain before this GPU-only pass.
    [HarmonyPrefix]
    internal static bool Prefix() => !Multiplayer.IsDedicated;
}
