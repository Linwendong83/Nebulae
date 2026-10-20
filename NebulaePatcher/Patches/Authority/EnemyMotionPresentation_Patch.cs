using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(FactoryModel), nameof(FactoryModel.DrawInstancedBatches))]
internal static class GroundEnemyMotionPresentation_Patch
{
    [HarmonyPrefix]
    internal static void Prefix(FactoryModel __instance, out EnemyMotionPresentation.DrawState __state)
    {
        __state = Multiplayer.IsActive && Multiplayer.Session.IsClient && Multiplayer.Session.IsGameLoaded
            ? Multiplayer.Session.EnemyMotion?.BeginDraw(__instance) : null;
    }

    [HarmonyFinalizer]
    internal static void Finalizer(EnemyMotionPresentation.DrawState __state) => __state?.Restore();
}

[HarmonyPatch(typeof(SectorModel), nameof(SectorModel.DrawInstancedBatches))]
internal static class SpaceEnemyMotionPresentation_Patch
{
    [HarmonyPrefix]
    internal static void Prefix(SectorModel __instance, out EnemyMotionPresentation.DrawState __state)
    {
        __state = Multiplayer.IsActive && Multiplayer.Session.IsClient && Multiplayer.Session.IsGameLoaded
            ? Multiplayer.Session.EnemyMotion?.BeginDraw(__instance) : null;
    }

    [HarmonyFinalizer]
    internal static void Finalizer(EnemyMotionPresentation.DrawState __state) => __state?.Restore();
}

[HarmonyPatch(typeof(PlanetData), nameof(PlanetData.UnloadFactory))]
internal static class GroundEnemyMotionUnload_Patch
{
    [HarmonyPrefix]
    internal static void Prefix(PlanetData __instance) => Multiplayer.Session?.EnemyMotion?.Forget(__instance.factory);
}

[HarmonyPatch(typeof(SpaceSector), nameof(SpaceSector.Free))]
internal static class SpaceEnemyMotionUnload_Patch
{
    [HarmonyPrefix]
    internal static void Prefix(SpaceSector __instance) => Multiplayer.Session?.EnemyMotion?.Forget(__instance);
}
