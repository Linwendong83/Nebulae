#region

using HarmonyLib;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(EnemyFormation))]
internal class EnemyFormation_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(EnemyFormation), nameof(EnemyFormation.RemoveUnit))]
    public static bool RemoveUnit_Prefix(EnemyFormation __instance, int port, int[] ___vacancies, ref int ___vacancyCursor)
    {
        if (__instance.units[port] != 0)
        {
            if (___vacancyCursor < ___vacancies.Length) // guard
            {
                ___vacancies[___vacancyCursor++] = port;
            }
            __instance.units[port] = 0;
        }
        return false;
    }
}
