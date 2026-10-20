using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class CraftReplicationEvents_Patch
{
    private static NebulaeWorld.Authority.Adapters.CraftSnapshotAdapter Source => Multiplayer.Session?.IsServer == true ? Multiplayer.Session.AuthorityRuntime?.CraftHost : null;
    [HarmonyPostfix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.AddCraftData))]
    public static void AddedGround(PlanetFactory __instance, int __result) => Source?.Added(__instance, __result);
    [HarmonyPostfix, HarmonyPatch(typeof(SpaceSector), nameof(SpaceSector.AddCraftData))]
    public static void AddedSpace(SpaceSector __instance, int __result) => Source?.Added(__instance, __result);
    [HarmonyPostfix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.RemoveCraftWithComponents))]
    public static void RemovedGround(PlanetFactory __instance, int id)
    { if (id > 0 && (id >= __instance.craftPool.Length || __instance.craftPool[id].id != id)) Source?.Removed(__instance, id); }
    [HarmonyPostfix, HarmonyPatch(typeof(SpaceSector), nameof(SpaceSector.RemoveCraftWithComponents))]
    public static void RemovedSpace(SpaceSector __instance, int id)
    { if (id > 0 && (id >= __instance.craftPool.Length || __instance.craftPool[id].id != id)) Source?.Removed(__instance, id); }
}
