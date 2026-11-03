using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic.Game;

// The dedicated host's dummy player has no cursor. Keep real creation physics,
// projectile queries and collider storage; suppress only the local input query.
[HarmonyPatch(typeof(SpaceColliderLogic), nameof(SpaceColliderLogic.GameTick))]
internal static class HeadlessLocalInteraction_Patch
{
    internal static bool Enabled = true;
    [HarmonyPrepare]
    internal static bool Prepare() => Multiplayer.IsDedicated;

    [HarmonyPrefix]
    internal static bool Prefix(SpaceColliderLogic __instance)
    {
        if (!Enabled || !Multiplayer.IsDedicated) return true;
        __instance.cursorCastAllCount = 0;
        __instance.castEnemyhiveIndex = -1;
        __instance.castEnemyFormId = 0;
        __instance.castEnemyPortId = 0;
        __instance.castPlanetAstroId = 0;
        return false;
    }

    // Sailing/projectile collision queries still consume relative poses. Their
    // native guards use this same dummy player, not the connected remote players.
    internal static bool SkipRelativePoses(SectorModel model) => Enabled && Multiplayer.IsDedicated &&
        model.gameData.mainPlayer != null && !model.gameData.mainPlayer.sailing;
}

[HarmonyPatch(typeof(SectorModel), "Update")]
internal static class HeadlessSectorPresentation_Patch
{
    [HarmonyPrepare]
    internal static bool Prepare() => Multiplayer.IsDedicated;
    [HarmonyPrefix]
    internal static bool Prefix() => !HeadlessLocalInteraction_Patch.Enabled || !Multiplayer.IsDedicated;
}
