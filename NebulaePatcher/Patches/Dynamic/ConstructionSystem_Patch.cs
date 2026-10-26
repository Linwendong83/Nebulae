#region

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using NebulaeModel.Authority;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(ConstructionSystem))]
internal class ConstructionSystem_Patch
{
    [ThreadStatic] private static int currentNativeDrone;
    [HarmonyPrefix, HarmonyPatch(nameof(ConstructionSystem.DetermineLaunch))]
    public static void DetermineLaunch(ConstructionSystem __instance)
    {
        if (!Multiplayer.IsActive) return;
        Multiplayer.Session.BuildDispatch.PrepareLaunches(__instance.factory);
        Multiplayer.Session.Repairs?.Prepare(__instance.factory);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionSystem.UpdateModules))]
    public static bool UpdateModules_Prefix(ConstructionSystem __instance, long time)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsServer)
            Multiplayer.Session.Repairs?.TickFactory(__instance.factory, time);
        return !Multiplayer.IsActive || Multiplayer.Session.IsServer;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(ConstructionSystem.UpdateModules))]
    public static void RecountRepairers(ConstructionSystem __instance)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsServer) Multiplayer.Session.Repairs?.Recount(__instance.factory);
    }

    [HarmonyTranspiler, HarmonyPatch(nameof(ConstructionSystem.UpdateModules))]
    public static IEnumerable<CodeInstruction> UpdateModules_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getter = AccessTools.PropertyGetter(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.allDronesIdle));
        var count = 0;
        foreach (var code in instructions)
        {
            if (code.Calls(getter))
            {
                count++;
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code);
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(ConstructionSystem_Patch), nameof(AllDronesIdle));
            }
            yield return code;
        }
        if (count == 0) throw new InvalidOperationException("Native repair idle check changed.");
    }
    public static bool AllDronesIdle(ConstructionModuleComponent module, ConstructionSystem system) => module.allDronesIdle &&
        (!Multiplayer.IsActive || !Multiplayer.Session.IsServer || Multiplayer.Session.Repairs?.HasRemoteRepairs(system.factory) != true);

    [HarmonyPrefix, HarmonyPatch(nameof(ConstructionSystem.FindNextRepair))]
    public static bool FindNextRepair(ConstructionSystem __instance, int moduleId, ref bool __result)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer || moduleId != 0) return true;
        if (Multiplayer.Session.Repairs?.IsReplica(__instance.factory, currentNativeDrone) != true) return true;
        // Bound repair flights receive their continuation from the host's target search.
        __result = false;
        return false;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(ConstructionSystem.FindNextRepair))]
    public static void OfferRepairContinuation(ConstructionSystem __instance, int moduleId, int targetId, ref bool __result)
    {
        if (!__result || !Multiplayer.IsActive || Multiplayer.Session.IsServer || moduleId != 0) return;
        Multiplayer.Session.Repairs?.OfferContinuation(__instance.factory, currentNativeDrone, targetId);
        // The native return leg remains reversible while the host arbitrates this continuation.
        __result = false;
    }

    // The host runs native base simulation; client mecha drones keep native local flight.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionSystem.Repair))]
    public static bool Repair_Prefix(ConstructionSystem __instance, int entityId, ref bool __result)
    {
        if (AuthorityRuleGuard.AllowHostRule()) return true;
        // A client presents a repair drone until the host's damage record disappears.
        var factory = __instance.factory;
        __result = entityId <= 0 || entityId >= factory.entityCursor ||
                   factory.entityPool[entityId].id != entityId ||
                   factory.entityPool[entityId].constructStatId == 0;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionSystem.AddConstructStat))]
    public static bool AddConstructStat_Prefix() =>
        AuthorityRuleGuard.AllowHostRule();

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionSystem.RemoveConstructStat))]
    public static bool RemoveConstructStat_Prefix() =>
        AuthorityRuleGuard.AllowHostRule();

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionSystem.UpdateDrones))]
    public static void UpdateDrones(ConstructionSystem __instance, ObjectRenderer[] renderers, bool sync_gpu_inst, float dt)
    {
        if (!Multiplayer.IsActive) return;

        // Update remote drones from other players
        var factory = __instance.factory;
        Multiplayer.Session.Drones.UpdateDrones(factory, renderers, sync_gpu_inst, dt);
        Multiplayer.Session.Repairs?.Render(factory, renderers, sync_gpu_inst, dt);
        if (!Multiplayer.Session.IsServer) Multiplayer.Session.BuildDispatch.RenderBaseDrones(factory, renderers, sync_gpu_inst, dt);
    }

    [HarmonyPostfix, HarmonyPatch(nameof(ConstructionSystem.GameTick))]
    public static void PublishBaseDrones(ConstructionSystem __instance, long time)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsServer) return;
        Multiplayer.Session.BuildDispatch.PublishConstructionStorage(__instance.factory, time);
        Multiplayer.Session.BuildDispatch.PublishBaseDrones(__instance.factory, time);
    }

    [HarmonyTranspiler, HarmonyPatch(nameof(ConstructionSystem.UpdateDrones))]
    [HarmonyPatch(nameof(ConstructionSystem.ExecuteBuildTasks))]
    public static IEnumerable<CodeInstruction> UpdateDrones_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var field = AccessTools.Field(typeof(DroneComponent), nameof(DroneComponent.id));
        var count = 0;
        foreach (var code in instructions)
        {
            if (code.LoadsField(field))
            {
                count++;
                code.opcode = OpCodes.Call;
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code);
                code.operand = AccessTools.Method(typeof(ConstructionSystem_Patch), nameof(SimulatedDroneId));
            }
            yield return code;
        }
        if (count == 0) throw new InvalidOperationException("Native construction drone loop membership changed.");
    }
    public static int SimulatedDroneId(ref DroneComponent drone, ConstructionSystem system)
    {
        currentNativeDrone = drone.id;
        return Multiplayer.IsActive && !Multiplayer.Session.IsServer && (drone.owner > 0 ||
            Multiplayer.Session.Repairs?.IsReplica(system.factory, drone.id) == true) ? 0 : drone.id;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(ConstructionSystem.ResetDroneTargets))]
    public static void ResetDroneTargets_Prefix(ConstructionSystem __instance, ref DroneComponent drone, out int[] __state)
    {
        if (Multiplayer.IsActive && drone.owner == 0) Multiplayer.Session.Repairs?.BeforeReset(__instance.factory, drone.id);
        __state = Multiplayer.IsActive ? new[] { drone.targetObjectId, drone.nextTarget1ObjectId,
            drone.nextTarget2ObjectId, drone.nextTarget3ObjectId } : null;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(ConstructionSystem.ResetDroneTargets))]
    public static void ResetDroneTargets_Postfix(ConstructionSystem __instance, ref DroneComponent drone, int[] __state)
    {
        if (!Multiplayer.IsActive) return;
        if (drone.owner < 0 || drone.owner >= __instance.constructionModules.cursor) return;
        var module = drone.owner == 0 ? GameMain.mainPlayer.mecha.constructionModule :
            __instance.constructionModules.buffer[drone.owner];
        if (module == null) return;
        if (__state == null) return;
        foreach (var target in __state)
            if (target < 0) Multiplayer.Session.BuildDispatch.ReleaseLocalTarget(__instance.factory, -target, module);
    }

}
