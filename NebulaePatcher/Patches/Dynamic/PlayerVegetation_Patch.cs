using NebulaeModel.Networking;
using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(Player), nameof(Player.vegetableCollection), MethodType.Getter)]
internal static class PlayerVegetation_Patch
{
    [HarmonyPrefix]
    public static bool GetCollection(Player __instance, ref VegetableCollection __result)
    {
        __result = NebulaeWorld.Player.VegetationAccess.Collection(__instance);
        return false;
    }
}
