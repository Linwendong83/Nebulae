using HarmonyLib;
using System;
using NebulaeWorld;
using NebulaeWorld.Factory;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class BuildingDeath_Patch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.KillEntityFinally))]
    private static void Begin(ref CombatStat combatStat, out IDisposable __state)
    {
        __state = Multiplayer.IsActive && Multiplayer.Session.IsServer
            ? BuildingDeathVisuals.BeginDeath(combatStat.lastImpact) : null;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.KillEntityFinally))]
    private static void End(IDisposable __state) => __state?.Dispose();

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.BeforeKillObject))]
    private static void Before(PlanetFactory __instance, int objId)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsServer)
            BuildingDeathReplication.Capture(__instance, objId);
    }

    // At this point native removal and prebuild creation have committed, but callbacks
    // have not yet been allowed to reconstruct/build using the new slots.
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.OnKillObject))]
    private static void Committed(PlanetFactory __instance, int newPrebuildId, int oldObjId)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsServer)
            BuildingDeathReplication.Publish(__instance, newPrebuildId, oldObjId);
    }
}
