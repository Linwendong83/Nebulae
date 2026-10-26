#region

using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Universe;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIDESwarmOrbitInfo))]
internal class UIDESwarmOrbitInfo_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIDESwarmOrbitInfo.CalculateSailLifeDistribution))]
    public static bool CalculateSailLifeDistribution_Prefix(UIDESwarmOrbitInfo __instance, int division, out float maxCount, ref float[] __result)
    {
        maxCount = 0f;
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return true;
        }

        __result = DysonSphereManager.SailLifeDistribution(__instance.swarm, division,
            GameMain.gameTick, GameMain.history.solarSailLife, __instance.orbits, out maxCount);
        return false;
    }
}
