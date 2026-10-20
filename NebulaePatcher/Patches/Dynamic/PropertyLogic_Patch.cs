#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(PropertyLogic))]
internal class PropertyLogic_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(PropertyLogic.GameTick))]
    [HarmonyPatch(nameof(PropertyLogic.UpdateProduction))]
    public static bool PrepareTick_Prefix(PropertyLogic __instance)
    {
        if (!Multiplayer.IsActive) return !NebulaeWorld.GameStates.MetadataManager.IsRetired(__instance.gameData);

        // MetadataManager computes the world peak and credits eligible online members, including the host.
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PropertyLogic.isSelfFormalGame), MethodType.Getter)]
    public static bool IsSelfFormalGame_Prefix(ref bool __result)
    {
        if (!Multiplayer.IsActive) return true;
        __result = Multiplayer.Session.IsGameLoaded && !Multiplayer.Session.IsDedicated;
        return false;
    }
}
