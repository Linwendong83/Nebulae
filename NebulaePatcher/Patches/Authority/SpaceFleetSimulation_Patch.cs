using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(CombatSpaceSystem), nameof(CombatSpaceSystem.GameTick))]
internal static class SpaceFleetSimulation_Patch
{
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            if (code.opcode == OpCodes.Ldelem_Ref && i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Isinst &&
                Equals(codes[i + 1].operand, typeof(SpaceDynamicRenderer)))
            { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(SpaceFleetSimulation_Patch), nameof(Renderer)); }
            else if (code.LoadsField(AccessTools.Field(typeof(Player), nameof(Player.isAlive))))
            { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(SpaceFleetSimulation_Patch), nameof(CheckOwnerEnergy)); }
            yield return code;
        }
    }
    // The native loop indexes by model ID before testing the renderer's type. A dedicated host
    // owns no renderers; an empty array aborts simulation as soon as the first space fleet exists.
    internal static SpaceObjectRenderer Renderer(SpaceObjectRenderer[] renderers, int index) =>
        !Multiplayer.IsDedicated && renderers != null && index >= 0 && index < renderers.Length ? renderers[index] : null;
    private static bool CheckOwnerEnergy(Player player) => Multiplayer.IsActive || player.isAlive;
}

[HarmonyPatch(typeof(UnitComponent), nameof(UnitComponent.UpdateMechaEnergy))]
internal static class FleetOwnerEnergyLife_Patch
{
    // UnitCombatAuthority_Patch has already selected this unit's real owner. The caller's camera
    // player may be dead (always true for a dedicated host), which must not exempt other fleets.
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static bool Prefix(Mecha mecha, ref bool __result)
    {
        if (!Multiplayer.IsActive || mecha?.player?.isAlive == true) return true;
        __result = true; return false;
    }
}
