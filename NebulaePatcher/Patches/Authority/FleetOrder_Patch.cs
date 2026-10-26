using System;
using HarmonyLib;
using NebulaeModel.Packets.Authority;
using NebulaeWorld;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

// CommandFleets reads the local mouse/collider once; send its resolved orders rather than
// making the host repeat that UI query. Native AI CancelOrder calls never enter this scope.
[HarmonyPatch]
internal static class FleetOrder_Patch
{
    [ThreadStatic] private static CombatModuleComponent commanding;

    [HarmonyPrefix, HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.CommandFleets))]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.CommandFleetsToPos))]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.CancelFleetOrder))]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.CancelAllFleetsOrder))]
    internal static void Enter(CombatModuleComponent __instance, out CombatModuleComponent __state)
    { __state = commanding; commanding = __instance; }

    [HarmonyFinalizer, HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.CommandFleets))]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.CommandFleetsToPos))]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.CancelFleetOrder))]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.CancelAllFleetsOrder))]
    internal static Exception Exit(CombatModuleComponent __state, Exception __exception)
    { commanding = __state; return __exception; }

    private static FleetCommandPrediction.Order Send(in FleetComponent fleet, CombatIntentKind kind, SkillTarget target = default, double x = 0, double y = 0, double z = 0)
    {
        if (commanding == null || !Multiplayer.IsActive || !Multiplayer.Session.IsClient || !Multiplayer.Session.Authority.IsActive) return null;
        var mecha = GameMain.mainPlayer.mecha;
        if (commanding != mecha.groundCombatModule && commanding != mecha.spaceCombatModule) return null;
        for (var i = 0; i < commanding.fleetCount; i++)
            if (commanding.moduleFleets[i].fleetId == fleet.id)
            {
                return Multiplayer.Session.CombatAuthority.Send(new CombatIntent(kind, i + (commanding.isSpace ? 100 : 0), x, y, z), target, out var sequence)
                    ? Multiplayer.Session.FleetCommands.CaptureOrder(commanding, i, sequence, fleet) : null;
            }
        return null;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(FleetComponent), nameof(FleetComponent.GiveAttackTargetOrder))]
    internal static void Target(ref FleetComponent __instance, ref SkillTarget attackTarget, out FleetCommandPrediction.Order __state) => __state = Send(__instance, CombatIntentKind.FleetAttackTarget, attackTarget);

    [HarmonyPrefix, HarmonyPatch(typeof(FleetComponent), nameof(FleetComponent.GiveAttackPosOrder))]
    internal static void Position(ref FleetComponent __instance, VectorLF3 targetPos, out FleetCommandPrediction.Order __state) => __state = Send(__instance, CombatIntentKind.FleetAttackPosition, x: targetPos.x, y: targetPos.y, z: targetPos.z);

    [HarmonyPrefix, HarmonyPatch(typeof(FleetComponent), nameof(FleetComponent.GiveOrbitingAstroOrder))]
    internal static void Orbit(ref FleetComponent __instance, int orbitingAstroId, out FleetCommandPrediction.Order __state) => __state = Send(__instance, CombatIntentKind.FleetOrbit, x: orbitingAstroId);

    [HarmonyPrefix, HarmonyPatch(typeof(FleetComponent), nameof(FleetComponent.CancelOrder))]
    internal static void Cancel(ref FleetComponent __instance, out FleetCommandPrediction.Order __state) => __state = Send(__instance, CombatIntentKind.FleetCancel);

    [HarmonyPostfix, HarmonyPatch(typeof(FleetComponent), nameof(FleetComponent.GiveAttackTargetOrder))]
    [HarmonyPatch(typeof(FleetComponent), nameof(FleetComponent.GiveAttackPosOrder))]
    [HarmonyPatch(typeof(FleetComponent), nameof(FleetComponent.GiveOrbitingAstroOrder))]
    [HarmonyPatch(typeof(FleetComponent), nameof(FleetComponent.CancelOrder))]
    internal static void Applied(ref FleetComponent __instance, FleetCommandPrediction.Order __state)
    { if (__state != null) __state.After = __instance; }
}
