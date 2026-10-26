using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Combat;
using UnityEngine;

namespace NebulaePatcher.Patches.Authority;

// Vanilla keeps owner = -1 for a player's fleet. Resolve the real owner from the
// host's module foreign keys instead of letting every fleet use the local pilot.
[HarmonyPatch]
internal static class FleetCombatAuthority_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods() => typeof(FleetComponent).GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Where(x => x.GetParameters().Any(p => p.ParameterType == typeof(Mecha)));

    [HarmonyPrefix]
    internal static void Prefix(ref FleetComponent __instance, ref Mecha mecha)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsGameLoaded) return;
        var craftId = __instance.craftId;
        var planet = FleetTickContext.Planet;
        var space = planet <= 0;
        var owner = Multiplayer.Session.IsServer ? Multiplayer.Session.CombatAuthority.CraftMecha(space, planet, craftId) :
            NativeCraftSimulation.OwnerMecha(space, planet, craftId);
        if (owner != null) mecha = owner;
    }
}

[HarmonyPatch]
internal static class UnitCombatAuthority_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods() => typeof(UnitComponent).GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Where(x => x.GetParameters().Any(p => p.ParameterType == typeof(Mecha)));
    [HarmonyPrefix]
    internal static void Prefix(ref UnitComponent __instance, ref Mecha mecha)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsGameLoaded) return;
        var planet = FleetTickContext.Planet;
        var owner = Multiplayer.Session.IsServer ? Multiplayer.Session.CombatAuthority.CraftMecha(planet <= 0, planet, __instance.craftId) :
            NativeCraftSimulation.OwnerMecha(planet <= 0, planet, __instance.craftId);
        if (owner != null) mecha = owner;
    }
}

[HarmonyPatch(typeof(FleetComponent), nameof(FleetComponent.InternalUpdate_Ground))]
internal static class FleetGroundPoseAuthority_Patch
{
    [HarmonyPrefix]
    internal static void Prefix(ref FleetComponent __instance, PlanetFactory factory, ref Pose playerPose)
    {
        if (!Multiplayer.IsActive) return;
        var mecha = Multiplayer.Session.IsServer ? Multiplayer.Session.CombatAuthority.CraftMecha(false, factory.planetId, __instance.craftId) :
            NativeCraftSimulation.OwnerMecha(false, factory.planetId, __instance.craftId);
        if (mecha != null) playerPose = new Pose(mecha.player.position, Quaternion.Inverse(factory.planet.runtimeRotation) * mecha.player.uRotation);
    }
}

[HarmonyPatch]
internal static class FleetTickContext
{
    [ThreadStatic] public static int Planet;
    internal static IEnumerable<MethodBase> TargetMethods() => new[]
    {
        AccessTools.Method(typeof(CombatGroundSystem), nameof(CombatGroundSystem.GameTick)),
        AccessTools.Method(typeof(CombatSpaceSystem), nameof(CombatSpaceSystem.GameTick)),
        AccessTools.Method(typeof(CombatGroundSystem), nameof(CombatGroundSystem.PostGameTick)),
        AccessTools.Method(typeof(CombatSpaceSystem), nameof(CombatSpaceSystem.PostGameTick))
    };
    [HarmonyPrefix]
    internal static void Prefix(object __instance, out int __state)
    { __state = Planet; Planet = __instance is CombatGroundSystem ground ? ground.factory.planetId : 0; }
    [HarmonyFinalizer]
    private static Exception Finalizer(int __state, Exception __exception)
    { Planet = __state; return __exception; }
}

[HarmonyPatch]
internal static class FleetRemovalAuthority_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods() => new[]
    {
        AccessTools.Method(typeof(SpaceSector), nameof(SpaceSector.ClearReferencesOnCraftRemove)),
        AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.ClearReferencesOnCraftRemove))
    };

    [HarmonyPrefix]
    internal static bool Prefix(object __instance, int removingCraftId)
    {
        if (!Multiplayer.IsActive) return true;
        if (CombatAuthorityManager.ReclaimingFleets) return false;
        if (Multiplayer.Session.IsClient) return false; // Display shells have no combat backlinks.
        var factory = __instance as PlanetFactory;
        var space = factory == null;
        var pool = space ? ((SpaceSector)__instance).craftPool : factory.craftPool;
        var mecha = Multiplayer.Session.CombatAuthority.CraftMecha(space, factory?.planetId ?? 0, removingCraftId);
        if (mecha == null) return true;
        ref var craft = ref pool[removingCraftId];
        if (craft.unitId > 0 && craft.owner > 0)
        {
            ref var parent = ref pool[craft.owner];
            var fleets = space ? GameMain.spaceSector.combatSpaceSystem.fleets : factory.combatGroundSystem.fleets;
            var units = space ? GameMain.spaceSector.combatSpaceSystem.units : factory.combatGroundSystem.units;
            ref var unit = ref units.buffer[craft.unitId];
            if (unit.behavior == EUnitBehavior.Initialize) fleets.buffer[parent.fleetId].currentAssembleUnitsCount--;
            if (unit.isCharging) { unit.isCharging = false; fleets.buffer[parent.fleetId].currentChargingUnitsCount--; }
            (space ? mecha.spaceCombatModule : mecha.groundCombatModule).NotifyFighterRemoved(parent.port, craft.port, null);
        }
        if (craft.fleetId > 0) craft.isInvincible = false;
        return false;
    }
}
