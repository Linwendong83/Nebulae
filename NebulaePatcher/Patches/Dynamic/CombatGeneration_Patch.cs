using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.AddEnemyData))]
internal class GroundEnemyGeneration_Patch
{
    [HarmonyPostfix]
    public static void Postfix(PlanetFactory __instance, int __result)
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Generations.Created(__instance.planetId, __result);
    }
}

[HarmonyPatch(typeof(SpaceSector), nameof(SpaceSector.AddEnemyData))]
internal class SpaceEnemyGeneration_Patch
{
    [HarmonyPostfix]
    public static void Postfix(int __result)
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Generations.Created(0, __result);
    }
}
