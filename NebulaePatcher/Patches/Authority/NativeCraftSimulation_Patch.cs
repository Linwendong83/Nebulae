using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class NativeCraftSimulation_Patch
{
    private static IEnumerable<MethodBase> TargetMethods() => new[] { typeof(CombatGroundSystem), typeof(CombatSpaceSystem) }
        .SelectMany(type => type.GetMethods().Where(method => method.Name is "GameTick" or "PostGameTick"));
    [HarmonyPrefix]
    private static void Prefix(object __instance, out int __state)
    {
        __state = NativeCraftSimulation.FleetPlanet;
        NativeCraftSimulation.FleetPlanet = __instance is CombatGroundSystem ground ? ground.factory.planetId : 0;
    }
    [HarmonyFinalizer]
    private static Exception Finalizer(int __state, Exception __exception)
    { NativeCraftSimulation.FleetPlanet = __state; return __exception; }
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions); var count = 0;
        foreach (var instruction in code)
        {
            var fleet = instruction.LoadsField(AccessTools.Field(typeof(FleetComponent), nameof(FleetComponent.id)));
            var unit = instruction.LoadsField(AccessTools.Field(typeof(UnitComponent), nameof(UnitComponent.id)));
            if (!fleet && !unit) continue;
            instruction.opcode = OpCodes.Call;
            instruction.operand = AccessTools.Method(typeof(NativeCraftSimulation), fleet ? nameof(NativeCraftSimulation.FleetId) : nameof(NativeCraftSimulation.UnitId));
            count++;
        }
        if (count == 0) throw new InvalidOperationException("Native craft loop membership checks changed.");
        return code;
    }
}

[HarmonyPatch]
internal static class NativeCraftCommit_Patch
{
    [HarmonyPrefix, HarmonyPatch(typeof(FleetComponent), nameof(FleetComponent.AssembleUnits_Ground))]
    [HarmonyPatch(typeof(FleetComponent), nameof(FleetComponent.AssembleUnits_Space))]
    private static bool Assemble() => !WorldSimulation.IsClient;
    [HarmonyPrefix, HarmonyPatch(typeof(FleetComponent), nameof(FleetComponent.DeterminActiveEnemyUnits))]
    private static bool Activate(ref bool __result)
    { if (!WorldSimulation.IsClient) return true; __result = false; return false; }
    [HarmonyPrefix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.RemoveCraftDeferred))]
    private static bool RemoveGround(PlanetFactory __instance, int craftId) => !WorldSimulation.IsClient ||
        craftId > 0 && craftId < __instance.craftPool.Length && __instance.craftPool[craftId].prototype == ECraftProto.ConstructionDrone;
    [HarmonyPrefix, HarmonyPatch(typeof(SpaceSector), nameof(SpaceSector.RemoveCraftDeferred))]
    private static bool RemoveSpace() => !WorldSimulation.IsClient;
}

[HarmonyPatch]
internal static class NativeUnitHealth_Patch
{
    private static IEnumerable<MethodBase> TargetMethods() => typeof(UnitComponent).GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Where(method => method.GetMethodBody() != null);
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.StoresField(AccessTools.Field(typeof(CombatStat), nameof(CombatStat.hp))))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(NativeUnitHealth_Patch), nameof(SetHp)); }
            yield return instruction;
        }
    }
    private static void SetHp(ref CombatStat stat, int hp) { if (!WorldSimulation.IsClient) stat.hp = hp; }
}
