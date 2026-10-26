#region

using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Universe;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIDESphereInfo))]
internal class UIDESphereInfo_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIDESphereInfo.CalculateSailLifeDistribution))]
    public static bool CalculateSailLifeDistribution_Prefix(UIDESphereInfo __instance, int division, out float maxCount, ref float[] __result)
    {
        maxCount = 0f;
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return true;
        }

        __result = DysonSphereManager.SailLifeDistribution(__instance.dysonSphere?.swarm, division,
            GameMain.gameTick, GameMain.history.solarSailLife, null, out maxCount);
        return false;
    }
}
