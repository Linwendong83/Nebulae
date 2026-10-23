using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic;

// SpaceLODCulling and the space/starmap materials only recognize galaxy IDs
// through 204899. CPU classification patches cannot change that shader constant.
// Mirror high astros into unused low GPU slots; simulation/save IDs stay intact.
internal sealed class GalaxyRenderAstros
{
    internal const int ShaderMaxAstroId = 204899;
    private static readonly ConditionalWeakTable<GalaxyData, GalaxyRenderAstros> maps = new();
    private static readonly ConditionalWeakTable<SPACEOBJECT[], SPACEOBJECT[]> uploads = new();
    private readonly Dictionary<int, int> aliases = new();

    internal GalaxyRenderAstros(GalaxyData galaxy)
    {
        var occupied = new HashSet<int>();
        var high = new List<int>();
        foreach (var star in galaxy.stars)
        {
            Add(star.astroId);
            foreach (var planet in star.planets) Add(planet.astroId);
        }
        var next = 100;
        foreach (var id in high)
        {
            while (occupied.Contains(next)) next++;
            if (next > ShaderMaxAstroId)
                throw new InvalidOperationException("No free galaxy astro slots for GPU rendering.");
            aliases.Add(id, next++);
        }

        void Add(int id)
        {
            if (id <= ShaderMaxAstroId) occupied.Add(id);
            else high.Add(id);
        }
    }

    internal static GalaxyRenderAstros For(GalaxyData galaxy) => galaxy == null || galaxy.starCount <= 2048
        ? null : maps.GetValue(galaxy, value => new GalaxyRenderAstros(value));

    internal uint RenderId(uint id) => aliases.TryGetValue((int)id, out var alias) ? (uint)alias : id;

    internal void Mirror(AstroPoseR[] poses)
    {
        foreach (var pair in aliases) poses[pair.Value] = poses[pair.Key];
    }

    // Keep the original pool untouched: native simulation also writes it directly.
    // Every upload (including partial updates and capacity growth) uses a separate
    // render copy, shared by the world and starmap renderers.
    internal SPACEOBJECT[] CopyForUpload(SPACEOBJECT[] source, int start, int count)
    {
        var copy = uploads.GetValue(source, value => new SPACEOBJECT[value.Length]);
        Array.Copy(source, start, copy, start, count);
        for (var i = start; i < start + count; i++) copy[i].astroId = RenderId(copy[i].astroId);
        return copy;
    }

    internal static void Upload(ComputeBuffer buffer, Array data, SpaceObjectRenderer renderer)
        => UploadRange(buffer, data, 0, 0, data.Length, renderer);

    internal static void UploadRange(ComputeBuffer buffer, Array data, int sourceStart, int bufferStart, int count, SpaceObjectRenderer renderer)
    {
        if (data is SPACEOBJECT[] instances && For(renderer.manager?.sectorModel?.gameData?.galaxy) is { } map)
            data = map.CopyForUpload(instances, sourceStart, count);
        buffer.SetData(data, sourceStart, bufferStart, count);
    }
}

[HarmonyPatch]
internal static class GalaxyRenderUpload_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[] { "SetInstCapacity", "SyncInstBuffer", "AddInst", "AlterInst", "RemoveInst", "Clear" })
            yield return AccessTools.DeclaredMethod(typeof(SpaceObjectRenderer), name);
        yield return AccessTools.DeclaredMethod(typeof(SpaceDynamicRenderer), "SyncInstBuffer");
        yield return AccessTools.DeclaredMethod(typeof(SpaceRuinRenderer), "SyncInstBuffer");
    }

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var full = AccessTools.Method(typeof(ComputeBuffer), nameof(ComputeBuffer.SetData), new[] { typeof(Array) });
        var range = AccessTools.Method(typeof(ComputeBuffer), nameof(ComputeBuffer.SetData),
            new[] { typeof(Array), typeof(int), typeof(int), typeof(int) });
        var count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(full) || instruction.Calls(range))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0)
                    .WithLabels(instruction.ExtractLabels()).WithBlocks(instruction.ExtractBlocks());
                instruction.operand = AccessTools.Method(typeof(GalaxyRenderAstros),
                    instruction.Calls(full) ? nameof(GalaxyRenderAstros.Upload) : nameof(GalaxyRenderAstros.UploadRange));
                instruction.opcode = OpCodes.Call;
                count++;
            }
            yield return instruction;
        }
        if (count == 0) throw new InvalidOperationException("Space instance buffer upload path changed.");
    }
}
