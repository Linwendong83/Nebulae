using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Combat;
using NebulaeModel.Logger;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.RunBehavior_Defense_Ground))]
internal static class GroundDefenseReferences_Patch
{
    [HarmonyFinalizer]
    private static void Diagnose(Exception __exception, ref EnemyUnitComponent __instance, EnemyData[] enemyPool,
        DFGBaseComponent[] bases, ref EnemyData enemy)
    {
        if (__exception == null || !Multiplayer.IsActive) return;
        var unit = __instance;
        var owner = enemy.owner;
        var astro = enemy.originAstroId;
        Diagnostics.Warn("ground-defense:" + astro, () =>
            $"[ground-defense] planet={astro} unit={unit.id} enemy={unit.enemyId} baseEnemy={unit.baseEnemyId} owner={owner} enemies={enemyPool?.Length} bases={bases?.Length} skill={unit.blockSkill.type}:{unit.blockSkill.id} error={__exception}");
    }
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        var block = AccessTools.Field(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.blockSkill));
        var id = AccessTools.Field(typeof(SKILLID), nameof(SKILLID.id));
        var validate = AccessTools.Method(typeof(GroundDefenseReferences_Patch), nameof(ValidId));
        var count = 0;
        for (var i = code.Count - 1; i > 0; i--)
        {
            if (!code[i].LoadsField(id) || !code[i - 1].LoadsField(block, true)) continue;
            var first = new CodeInstruction(OpCodes.Ldarg_2);
            first.labels.AddRange(code[i].labels); code[i].labels.Clear();
            code.InsertRange(i, new[] { first, new CodeInstruction(OpCodes.Ldarg_3),
                new CodeInstruction(OpCodes.Ldarg_S, (byte)4), new CodeInstruction(OpCodes.Ldarg_S, (byte)6) });
            code[i + 4].opcode = OpCodes.Call; code[i + 4].operand = validate;
            count++;
        }
        if (count < 3) throw new InvalidOperationException("Native ground interception checks changed.");
        // Another ground worker can retarget a shared projectile after the id checks above.
        // Check the index on the actual array access, including the formation's base link.
        var enemyAt = AccessTools.Method(typeof(GroundDefenseReferences_Patch), nameof(EnemyAt));
        foreach (var instruction in code)
            if (instruction.opcode == OpCodes.Ldelema && Equals(instruction.operand, typeof(EnemyData)))
            { instruction.opcode = OpCodes.Call; instruction.operand = enemyAt; }
        return code;
    }

    internal static ref EnemyData EnemyAt(EnemyData[] enemies, int id) =>
        ref enemies[(uint)id < (uint)enemies.Length ? id : 0];

    private static int ValidId(ref SKILLID skill, SkillSystem skills, EnemyData[] enemies,
        DFGBaseComponent[] bases, ref EnemyData enemy) => Multiplayer.IsActive
        ? GroundDefenseReferences.ValidId(ref skill, skills, enemies, bases, ref enemy) : skill.id;
}
