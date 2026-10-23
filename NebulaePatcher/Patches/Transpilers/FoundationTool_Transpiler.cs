using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;
using UnityEngine;

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch]
internal static class FoundationTool_Transpiler
{
    private static readonly AccessTools.FieldRef<BuildTool_Reform, int> NativePit =
        AccessTools.FieldRefAccess<BuildTool_Reform, int>("removeBasePitRuinId");
    public static int PitId(BuildTool_Reform tool) => tool == null ? 0 : NativePit(tool);
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(BuildTool_Reform), "DoFlattenExecute");
        yield return AccessTools.Method(typeof(BuildTool_Reform), nameof(BuildTool_Reform.RemoveBasePit));
    }
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var flatten = AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.FlattenTerrainReform));
        var replace = AccessTools.Method(typeof(FoundationTool_Transpiler), nameof(Flatten));
        var call = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(flatten))
            {
                var tool = new CodeInstruction(OpCodes.Ldarg_0);
                tool.labels.AddRange(instruction.labels); instruction.labels.Clear();
                yield return tool;
                yield return new CodeInstruction(__originalMethod.Name == nameof(BuildTool_Reform.RemoveBasePit) || call > 0 ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                instruction.opcode = OpCodes.Call; instruction.operand = replace; call++;
            }
            yield return instruction;
        }
        if (call != (__originalMethod.Name == nameof(BuildTool_Reform.RemoveBasePit) ? 1 : 2))
            throw new System.InvalidOperationException("Native foundation operation boundaries changed: " + __originalMethod.Name);
    }
    public static void Flatten(PlanetFactory factory, Vector3 center, float radius, int size, bool buried, float fade,
        BuildTool_Reform tool, bool circle)
    {
        var vegetation = Multiplayer.Session?.Vegetation;
        if (vegetation == null) { factory.FlattenTerrainReform(center, radius, size, buried, fade); return; }
        var previousTool = vegetation.ReformTool; var previousCircle = vegetation.ReformIsCircle;
        vegetation.ReformTool = tool; vegetation.ReformIsCircle = circle;
        try { factory.FlattenTerrainReform(center, radius, size, buried, fade); }
        finally { vegetation.ReformTool = previousTool; vegetation.ReformIsCircle = previousCircle; }
    }
}

[HarmonyPatch(typeof(BuildTool_Reform), nameof(BuildTool_Reform.RemoveBasePit))]
internal static class FoundationPitContext_Patch
{
    [HarmonyPrefix]
    public static void Prefix(int removeBasePitRuinId, out int __state)
    {
        var vegetation = Multiplayer.Session?.Vegetation;
        __state = vegetation?.ReformPitId ?? 0;
        if (vegetation != null) vegetation.ReformPitId = removeBasePitRuinId;
    }
    [HarmonyFinalizer]
    public static void Finalizer(int __state)
    {
        if (Multiplayer.Session?.Vegetation is { } vegetation) vegetation.ReformPitId = __state;
    }
}
