using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(SpaceLaserSweep), nameof(SpaceLaserSweep.TickSkillLogic))]
internal static class SpaceSweepAuthority_Patch
{
    internal readonly struct SavedContext : IScopeFrame
    {
        public SavedContext(SweepPlayerContext.State state, Mecha target) { State = state; Target = target; Native = new PlayerSkillScope(GameMain.spaceSector.skillSystem); }
        public SweepPlayerContext.State State { get; }
        public Mecha Target { get; }
        public PlayerSkillScope Native { get; }
        public void Restore() { Native.Restore(); SweepPlayerContext.Current = State; CombatTargetContext.TargetMecha = Target; }
    }
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    public static void Prefix()
    {
        ScopeStack<SavedContext>.Push(new SavedContext(SweepPlayerContext.Current, CombatTargetContext.TargetMecha));
    }
    [HarmonyFinalizer]
    public static Exception Finalizer(Exception __exception)
    { ScopeStack<SavedContext>.Pop(); return __exception; }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var codes = new List<CodeInstruction>(instructions);
        var mask = AccessTools.Field(typeof(SpaceLaserSweep), nameof(SpaceLaserSweep.mask));
        var starts = new List<(int Start, Label End)>();
        for (var i = 0; i < codes.Count - 5; i++)
            if (codes[i].LoadsField(mask) && codes[i + 1].LoadsConstant((int)ETargetTypeMask.Player) && codes[i + 2].opcode == OpCodes.And)
            {
                var branch = i + 3;
                if (codes[branch].LoadsConstant(0) && codes[branch + 1].opcode == OpCodes.Cgt_Un) branch += 2;
                if (codes[branch].opcode == OpCodes.Brfalse || codes[branch].opcode == OpCodes.Brfalse_S)
                    starts.Add((branch + 1, (Label)codes[branch].operand));
            }
        if (starts.Count != 2) throw new InvalidOperationException("Space sweep player collision blocks changed: " + starts.Count);
        for (var block = starts.Count - 1; block >= 0; block--)
        {
            var start = starts[block].Start; var endLabel = starts[block].End;
            var end = codes.FindIndex(start, x => x.labels.Contains(endLabel));
            if (end < 0) throw new InvalidOperationException("Space sweep player block end missing.");
            var loop = generator.DefineLabel();
            codes[start].labels.Add(loop);
            codes.InsertRange(end, new[] { new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SweepPlayerContext), nameof(SweepPlayerContext.Next))),
                new CodeInstruction(OpCodes.Brtrue, loop) });
            // Existing branches that skip the player block retain their original end label.
            codes.InsertRange(start, new[] { new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SweepPlayerContext), nameof(SweepPlayerContext.Begin))),
                new CodeInstruction(OpCodes.Brfalse, endLabel) });
        }
        var getters = new Dictionary<string, string>
        {
            [nameof(SkillSystem.mecha)] = nameof(SweepPlayerContext.Mecha),
            [nameof(SkillSystem.localPlanetAstroId)] = nameof(SweepPlayerContext.Planet),
            [nameof(SkillSystem.playerEnergyShieldRadius)] = nameof(SweepPlayerContext.ShieldRadius),
            [nameof(SkillSystem.playerSkillTargetL)] = nameof(SweepPlayerContext.LocalTarget),
            [nameof(SkillSystem.playerSkillTargetU)] = nameof(SweepPlayerContext.UniversalTarget),
            [nameof(SkillSystem.playerSkillColliderL)] = nameof(SweepPlayerContext.Collider)
        };
        for (var i = 0; i < codes.Count; i++)
        {
            if ((codes[i].opcode == OpCodes.Ldfld || codes[i].opcode == OpCodes.Ldflda) &&
                codes[i].operand is System.Reflection.FieldInfo field && field.DeclaringType == typeof(SkillSystem) &&
                getters.TryGetValue(field.Name, out var getter))
            {
                var method = AccessTools.Method(typeof(SweepPlayerContext), getter);
                var valueRead = codes[i].opcode == OpCodes.Ldfld && method.ReturnType.IsByRef;
                codes[i].opcode = OpCodes.Call; codes[i].operand = method;
                if (valueRead) codes.Insert(++i, new CodeInstruction(OpCodes.Ldobj, field.FieldType));
            }
            if (i + 1 < codes.Count && codes[i].opcode == OpCodes.Ldc_I4_1 &&
                codes[i + 1].StoresField(AccessTools.Field(typeof(SkillTarget), nameof(SkillTarget.id))))
            { codes[i].opcode = OpCodes.Call; codes[i].operand = AccessTools.Method(typeof(SweepPlayerContext), nameof(SweepPlayerContext.PlayerId)); }
        }
        return codes;
    }
}
