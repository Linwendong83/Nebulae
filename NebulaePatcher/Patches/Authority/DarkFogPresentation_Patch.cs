using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class DarkFogPresentation_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DFGBaseComponent), nameof(DFGBaseComponent.totalAvailableBuildingCount), MethodType.Getter)]
    public static bool BaseBuildings(DFGBaseComponent __instance, ref int __result)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        var facts = Multiplayer.Session.AuthorityRuntime.DarkFogReplica;
        var planet = __instance.groundSystem?.factory?.planetId ?? 0;
        if (facts?.BaseBuildings.TryGetValue((planet, __instance.id), out var count) != true) { __result = 0; return false; }
        __result = count; return false;
    }
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DFGBaseComponent), nameof(DFGBaseComponent.totalUnitCount), MethodType.Getter)]
    public static bool BaseUnits(DFGBaseComponent __instance, ref int __result)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        var facts = Multiplayer.Session.AuthorityRuntime.DarkFogReplica;
        var planet = __instance.groundSystem?.factory?.planetId ?? 0;
        __result = facts != null && facts.BaseUnits.TryGetValue((planet, __instance.id), out var count) ? count : 0;
        return false;
    }
    [HarmonyPrefix]
    [HarmonyPatch(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.totalAvailableBuildingCount), MethodType.Getter)]
    public static bool HiveBuildings(EnemyDFHiveSystem __instance, ref int __result)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        var facts = Multiplayer.Session.AuthorityRuntime.DarkFogReplica;
        __result = facts != null && facts.HiveBuildings.TryGetValue(__instance.hiveAstroId, out var count) ? count : 0;
        return false;
    }
    [HarmonyPrefix]
    [HarmonyPatch(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.totalUnitCount), MethodType.Getter)]
    public static bool HiveUnits(EnemyDFHiveSystem __instance, ref int __result)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        var facts = Multiplayer.Session.AuthorityRuntime.DarkFogReplica;
        __result = facts != null && facts.HiveUnits.TryGetValue(__instance.hiveAstroId, out var count) ? count : 0;
        return false;
    }
}
