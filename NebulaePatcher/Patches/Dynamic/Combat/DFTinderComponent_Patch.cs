#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic.Combat;

[HarmonyPatch(typeof(DFTinderComponent))]
internal class DFTinderComponent_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(DFTinderComponent.DispatchFromHive))]
    public static bool DispatchFromHive_Prefix(ref DFTinderComponent __instance, int _targetHiveAstroId)
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient) return Multiplayer.Session.Enemies.IsIncomingRequest;

        var hive1 = GameMain.spaceSector.GetHiveByAstroId(__instance.originHiveAstroId);
        var hive2 = GameMain.spaceSector.GetHiveByAstroId(_targetHiveAstroId);
        if (hive1 != null && hive2 != null)
        {
            __instance.targetHiveAstroId = _targetHiveAstroId;
        }
        return true;
    }
}
