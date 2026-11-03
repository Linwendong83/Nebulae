using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic.Combat;

// Combat particles (hit flashes, shield impacts, EMP bursts) and combat stat bars carry an
// astro id straight into their shaders. Those shaders classify an id as a galaxy astro only
// through 204899, the same ceiling GalaxyRenderAstros mirrors for SPACEOBJECT instances, but
// the particle and stat instance buffers never went through that copy. If the id is not
// remapped, a hit flash on a star above 2048 falls into neither the galaxy nor the hive branch
// and loses its pose. Keep the native buffer untouched and upload an aliased copy instead.
internal static class CombatAstroAliases
{
    // Runtime probes flip this to compare the raw upload with the mirrored one in one session.
    internal static bool Enabled = Environment.GetEnvironmentVariable("NEBULAE_COMBAT_ASTRO_ALIAS") != "0";
    private static readonly ConditionalWeakTable<ParticleData[], ParticleData[]> particleCopies = new();
    private static readonly ConditionalWeakTable<GeneralShieldBurst[], GeneralShieldBurst[]> burstCopies = new();
    private static readonly ConditionalWeakTable<CombatStat[], CombatStat[]> statCopies = new();

    private static int Alias(GalaxyRenderAstros map, int id) => id > 0 ? (int)map.RenderId((uint)id) : id;

    private static bool NeedsAlias(int id) => id > GalaxyRenderAstros.ShaderMaxAstroId && id < 1000000;

    internal static ParticleData[] CopyParticles(ParticleData[] source, int start, int count, GalaxyRenderAstros map)
    {
        var copy = particleCopies.GetValue(source, value => new ParticleData[value.Length]);
        Array.Copy(source, start, copy, start, count);
        for (var i = start; i < start + count; i++) copy[i].astroId = Alias(map, copy[i].astroId);
        return copy;
    }

    internal static GeneralShieldBurst[] CopyShieldBursts(GeneralShieldBurst[] source, int start, int count, GalaxyRenderAstros map)
    {
        var copy = burstCopies.GetValue(source, value => new GeneralShieldBurst[value.Length]);
        Array.Copy(source, start, copy, start, count);
        for (var i = start; i < start + count; i++) copy[i].astroId = Alias(map, copy[i].astroId);
        return copy;
    }

    internal static CombatStat[] CopyCombatStats(CombatStat[] source, GalaxyRenderAstros map)
    {
        var copy = statCopies.GetValue(source, value => new CombatStat[value.Length]);
        Array.Copy(source, copy, source.Length);
        for (var i = 0; i < copy.Length; i++)
        {
            copy[i].astroId = Alias(map, copy[i].astroId);
            copy[i].originAstroId = Alias(map, copy[i].originAstroId);
        }
        return copy;
    }

    private static GalaxyRenderAstros Map => Enabled ? GalaxyRenderAstros.For(GameMain.data?.galaxy) : null;

    private static bool AnyParticle(ParticleData[] data, int start, int count)
    {
        for (var i = start; i < start + count; i++) if (NeedsAlias(data[i].astroId)) return true;
        return false;
    }

    internal static void UploadParticles(ComputeBuffer buffer, ParticleData[] data, int sourceStart, int bufferStart, int count)
    {
        if (Map is not { } map || !AnyParticle(data, sourceStart, count))
        {
            buffer.SetData(data, sourceStart, bufferStart, count);
            return;
        }
        buffer.SetData(CopyParticles(data, sourceStart, count, map), sourceStart, bufferStart, count);
    }

    internal static void UploadShieldBursts(ComputeBuffer buffer, GeneralShieldBurst[] data, int sourceStart, int bufferStart, int count)
    {
        var needs = false;
        for (var i = sourceStart; i < sourceStart + count && !needs; i++) needs = NeedsAlias(data[i].astroId);
        if (Map is not { } map || !needs)
        {
            buffer.SetData(data, sourceStart, bufferStart, count);
            return;
        }
        buffer.SetData(CopyShieldBursts(data, sourceStart, count, map), sourceStart, bufferStart, count);
    }

    internal static void UploadCombatStats(ComputeBuffer buffer, CombatStat[] data)
    {
        var needs = false;
        for (var i = 0; i < data.Length && !needs; i++)
            needs = NeedsAlias(data[i].astroId) || NeedsAlias(data[i].originAstroId);
        if (Map is not { } map || !needs)
        {
            buffer.SetData(data);
            return;
        }
        buffer.SetData(CopyCombatStats(data, map));
    }
}

[HarmonyPatch]
internal static class ParticleInstanceUpload_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(DataPoolRenderer<ParticleData>), "Render");
        yield return AccessTools.Method(typeof(DataPoolRendererMultiMat<ParticleData>), "Render");
        yield return AccessTools.Method(typeof(DataPoolRendererMultiMat<GeneralShieldBurst>), "Render");
    }

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var setData = AccessTools.Method(typeof(ComputeBuffer), nameof(ComputeBuffer.SetData),
            new[] { typeof(Array), typeof(int), typeof(int), typeof(int) });
        var element = __originalMethod.DeclaringType!.GetGenericArguments()[0];
        var name = element == typeof(ParticleData) ? nameof(CombatAstroAliases.UploadParticles) :
            element == typeof(GeneralShieldBurst) ? nameof(CombatAstroAliases.UploadShieldBursts) :
            throw new InvalidOperationException("Unexpected combat instance type: " + element);
        var replacement = AccessTools.Method(typeof(CombatAstroAliases), name);
        var count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(setData))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Combat instance upload changed: " + __originalMethod.DeclaringType);
    }
}

[HarmonyPatch(typeof(CombatStatRenderer), nameof(CombatStatRenderer.Update))]
internal static class CombatStatUpload_Patch
{
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var setData = AccessTools.Method(typeof(ComputeBuffer), nameof(ComputeBuffer.SetData), new[] { typeof(Array) });
        var replacement = AccessTools.Method(typeof(CombatAstroAliases), nameof(CombatAstroAliases.UploadCombatStats));
        var count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(setData))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Combat stat upload changed.");
    }
}
