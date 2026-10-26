using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class UnitSensorOwner_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods() => new[]
    {
        AccessTools.Method(typeof(UnitComponent), nameof(UnitComponent.SensorLogic_Groud)),
        AccessTools.Method(typeof(UnitComponent), nameof(UnitComponent.SensorLogic_Space)),
        AccessTools.Method(typeof(UnitComponent), nameof(UnitComponent.PostGameTick_Ground)),
        AccessTools.Method(typeof(UnitComponent), nameof(UnitComponent.PostGameTick_Space)),
        AccessTools.Method(typeof(UnitComponent), nameof(UnitComponent.DetermineBehavior))
    };

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var codes = new List<CodeInstruction>(instructions);
        var parameters = __originalMethod.GetParameters();
        var worldArg = Array.FindIndex(parameters, p => p.ParameterType == typeof(PlanetFactory) || p.ParameterType == typeof(SpaceSector)) + 1;
        var getter = AccessTools.PropertyGetter(typeof(GameMain), nameof(GameMain.mainPlayer));
        var count = 0;
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            if (code.Calls(getter) && i + 1 < codes.Count && codes[i + 1].Calls(AccessTools.PropertyGetter(typeof(Player), nameof(Player.mecha))))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code).MoveBlocksFrom(code);
                yield return worldArg > 0 ? new CodeInstruction(OpCodes.Ldarg, worldArg) : new CodeInstruction(OpCodes.Ldnull);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(UnitSensorOwner_Patch), nameof(Owner)));
                i++; count++;
            }
            else yield return code;
        }
        if (count == 0) throw new InvalidOperationException("Native unit sensor owner reads changed.");
    }

    internal static Mecha Owner(ref UnitComponent unit, object world)
    {
        if (!Multiplayer.IsActive) return GameMain.mainPlayer.mecha;
        var planet = world is PlanetFactory factory ? factory.planetId : world is SpaceSector ? 0 : FleetTickContext.Planet;
        return (Multiplayer.Session.IsServer
            ? Multiplayer.Session.CombatAuthority.CraftMecha(planet == 0, planet, unit.craftId)
            : NativeCraftSimulation.OwnerMecha(planet == 0, planet, unit.craftId)) ?? GameMain.mainPlayer.mecha;
    }
}

// Replica buildings deliberately have no native builder component. The vanilla
// targeting tests use builderId only as a building discriminator, so supply that
// discriminator at the reads without installing an AI component on the client.
[HarmonyPatch]
internal static class UnitTargetClassification_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods() => AccessTools.GetDeclaredMethods(typeof(UnitComponent))
        .Where(method => method.GetMethodBody() != null && PatchProcessor.GetOriginalInstructions(method)
            .Any(code => code.LoadsField(AccessTools.Field(typeof(EnemyData), nameof(EnemyData.builderId)))));

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var builder = AccessTools.Field(typeof(EnemyData), nameof(EnemyData.builderId));
        foreach (var code in instructions)
        {
            if (code.LoadsField(builder))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(UnitTargetClassification_Patch), nameof(BuilderForTargeting));
            }
            yield return code;
        }
    }

    internal static int BuilderForTargeting(ref EnemyData enemy) => enemy.builderId > 0 ? enemy.builderId :
        Multiplayer.IsActive && Multiplayer.Session.IsClient && enemy.id > 0 && !enemy.dynamic &&
        enemy.dfRelayId == 0 && enemy.dfTinderId == 0 ? 1 : 0;
}
