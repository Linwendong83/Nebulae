using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

/// <summary>Creation and loss belong to the pilot owning the fleet, including remote pilots.</summary>
[HarmonyPatch]
internal static class FleetOwnerReferences_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods() => new[]
    {
        AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.SetupReferenceOnCraftCreate)),
        AccessTools.Method(typeof(SpaceSector), nameof(SpaceSector.SetupReferenceOnCraftCreate)),
        AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.KillCraftFinally)),
        AccessTools.Method(typeof(SpaceSector), nameof(SpaceSector.KillCraftFinal))
    };
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var code in instructions)
        {
            if (code.LoadsField(AccessTools.Field(typeof(Mecha), nameof(Mecha.groundCombatModule))) ||
                code.LoadsField(AccessTools.Field(typeof(Mecha), nameof(Mecha.spaceCombatModule))))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code).MoveBlocksFrom(code);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(FleetOwnerReferences_Patch), nameof(Module));
            }
            yield return code;
        }
    }
    private static CombatModuleComponent Module(Mecha fallback, object world, int id)
    {
        var planet = world is PlanetFactory f ? f.planetId : 0;
        var mecha = Multiplayer.IsActive && Multiplayer.Session.IsServer
            ? Multiplayer.Session.CombatAuthority.CraftMecha(planet == 0, planet, id) ?? fallback : fallback;
        return planet == 0 ? mecha.spaceCombatModule : mecha.groundCombatModule;
    }
}
