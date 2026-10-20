#region

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld.Combat;
using NebulaeWorld.Authority;

#endregion

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch(typeof(SkillSystem))]
internal class SkillSystem_Transpiler
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(SkillSystem.DamageGroundObjectByLocalCaster))]
    [HarmonyPatch(nameof(SkillSystem.DamageGroundObjectByRemoteCaster))]
    public static IEnumerable<CodeInstruction> DamageGroundObject_Transpiler(
        IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod, ILGenerator generator)
    {
        /*  Overwrite player id 1 to PlayerId
            from:
                else if (target.type == ETargetType.Player && target.id == 1)
            to:
                else if (target.type == ETargetType.Player && target.id == NebulaeWorld.Combat.CombatManager.PlayerId)
        */
        var matches = new[]
        {
            new CodeMatch(i => i.IsLdarg()),
            new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(SkillTargetLocal), nameof(SkillTargetLocal.id))),
            new CodeMatch(OpCodes.Ldc_I4_1),
            new CodeMatch(OpCodes.Bne_Un)
        };

        var codes = new List<CodeInstruction>(instructions);
        var label = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name + ".playerId";
        if (!AuthorityTranspilerGuard.VerifyCount(label, codes, 1, exact: true, matches))
        {
            return codes;
        }

        return GuardPredictedDamage(RoutePlayerDamage(codes, typeof(SkillTargetLocal)), generator);
    }

    [HarmonyTranspiler]
    [HarmonyPatch(nameof(SkillSystem.DamageObject))]
    public static IEnumerable<CodeInstruction> DamageObject_Transpiler(
        IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod, ILGenerator generator)
    {
        /*  Overwrite player id 1 to PlayerId
            from:
                if (target.id == 1)
            to:
                if (target.id == NebulaeWorld.Combat.CombatManager.PlayerId)
        */
        var matches = new[]
        {
            new CodeMatch(i => i.IsLdarg()),
            new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(SkillTarget), nameof(SkillTarget.id))),
            new CodeMatch(OpCodes.Ldc_I4_1),
            new CodeMatch(OpCodes.Bne_Un)
        };

        var codes = new List<CodeInstruction>(instructions);
        var label = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name + ".playerId";
        if (!AuthorityTranspilerGuard.VerifyCount(label, codes, 1, exact: true, matches))
        {
            return codes;
        }

        return GuardPredictedDamage(RoutePlayerDamage(codes, typeof(SkillTarget)), generator);
    }

    private static IEnumerable<CodeInstruction> GuardPredictedDamage(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var codes = new List<CodeInstruction>(instructions);
        var native = generator.DefineLabel();
        codes[0].labels.Add(native);
        codes.InsertRange(0, new[]
        {
            new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(WorldSimulation), nameof(WorldSimulation.IsClient))),
            new CodeInstruction(OpCodes.Brfalse, native),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(WorldSimulation), nameof(WorldSimulation.PredictedHit))),
            new CodeInstruction(OpCodes.Ret)
        });
        return codes;
    }

    private static IEnumerable<CodeInstruction> RoutePlayerDamage(List<CodeInstruction> codes, System.Type targetType)
    {
        var idField = AccessTools.Field(targetType, "id");
        CodeInstruction targetLoad = null;
        for (var i = 2; i < codes.Count - 1; i++)
            if (codes[i].opcode == OpCodes.Ldc_I4_1 && codes[i - 1].LoadsField(idField) && codes[i + 1].opcode == OpCodes.Bne_Un)
            {
                targetLoad = new CodeInstruction(codes[i - 2].opcode, codes[i - 2].operand);
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = AccessTools.Method(typeof(CombatTargetContext), nameof(CombatTargetContext.IsPlayerTarget));
                codes[i + 1].opcode = OpCodes.Brfalse;
                break;
            }
        if (targetLoad == null) return codes;
        var mechaField = AccessTools.Field(typeof(SkillSystem), nameof(SkillSystem.mecha));
        for (var i = 0; i < codes.Count - 2; i++)
            if (codes[i].LoadsField(mechaField) && codes[i + 2].Calls(AccessTools.Method(typeof(Mecha), nameof(Mecha.TakeDamage))))
            {
                var load = new CodeInstruction(targetLoad.opcode, targetLoad.operand);
                load.labels.AddRange(codes[i].labels);
                load.blocks.AddRange(codes[i].blocks);
                codes[i] = load;
                codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldfld, idField));
                codes.Insert(i + 2, new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(CombatTargetContext), nameof(CombatTargetContext.ResolveDamageMecha))));
                break;
            }
        return codes;
    }
}
