using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace NebulaePatcher.Patches.Misc;

// Replace only the compute transport. All original capacity, collision, state and timing logic
// after readback remains in the game method, including the zero-generator cleanup path.
internal static class HeadlessShieldPipeline
{
    private static bool allocationPatched, computationPatched;
    internal static bool Ready => allocationPatched && computationPatched;

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(GameMain), nameof(GameMain.End))]
    internal static void EndSession()
    {
        HeadlessShieldBackend.ReleaseSession();
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(PlanetATField), nameof(PlanetATField.CreatePhysics))]
    internal static IEnumerable<CodeInstruction> CreatePhysics(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.Select(instruction => new CodeInstruction(instruction)).ToList();
        var constructor = AccessTools.Constructor(typeof(ComputeBuffer), [typeof(int), typeof(int), typeof(ComputeBufferType)]);
        var matches = code.Where(instruction => instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, constructor)).ToList();
        if (matches.Count != 2) throw new InvalidOperationException("Shield physics allocation layout changed.");
        foreach (var instruction in matches)
        {
            instruction.opcode = OpCodes.Call;
            instruction.operand = AccessTools.Method(typeof(HeadlessShieldBackend), nameof(HeadlessShieldBackend.CreatePhysicsBuffer));
        }
        allocationPatched = true;
        return code;
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(PlanetATField), nameof(PlanetATField.RecalculatePhysicsShape))]
    internal static IEnumerable<CodeInstruction> Recalculate(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.Select(instruction => new CodeInstruction(instruction)).ToList();
        var meshBuffer = AccessTools.Field(typeof(PlanetATField), nameof(PlanetATField.physicsMeshBuffer));
        var upload = AccessTools.Method(typeof(ComputeBuffer), nameof(ComputeBuffer.SetData), [typeof(Array), typeof(int), typeof(int), typeof(int)]);
        var readback = AccessTools.Method(typeof(ComputeBuffer), nameof(ComputeBuffer.GetData), [typeof(Array), typeof(int), typeof(int), typeof(int)]);
        var dispatch = AccessTools.Method(typeof(ComputeShader), nameof(ComputeShader.Dispatch));
        var start = code.FindIndex(instruction => instruction.LoadsField(meshBuffer)) - 1;
        var end = code.FindLastIndex(instruction => instruction.Calls(readback));
        if (start < 0 || end <= start || code[start].opcode != OpCodes.Ldarg_0)
            throw new InvalidOperationException("Shield compute block not found.");
        var block = code.Skip(start).Take(end - start + 1).ToList();
        if (block.Count(instruction => instruction.Calls(upload)) != 2 ||
            block.Count(instruction => instruction.Calls(readback)) != 3 ||
            block.Count(instruction => instruction.Calls(dispatch)) != 1 ||
            block.Skip(1).Any(instruction => instruction.labels.Count != 0) ||
            block.Any(instruction => instruction.blocks.Count != 0 || instruction.opcode.FlowControl == FlowControl.Branch || instruction.opcode.FlowControl == FlowControl.Cond_Branch))
            throw new InvalidOperationException("Shield compute block control flow changed; refusing partial replacement.");
        var load = new CodeInstruction(OpCodes.Ldarg_0);
        load.labels.AddRange(code[start].labels);
        code.RemoveRange(start, end - start + 1);
        code.InsertRange(start, [load, new CodeInstruction(OpCodes.Call,
            AccessTools.Method(typeof(HeadlessShieldBackend), nameof(HeadlessShieldBackend.Compute)))]);
        computationPatched = true;
        return code;
    }
}
