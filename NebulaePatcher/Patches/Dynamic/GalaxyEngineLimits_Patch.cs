using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Utils;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch]
internal static class GalaxyAstroClassification_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SpaceSector), nameof(SpaceSector.IsGalaxyAstro));
        yield return AccessTools.Method(typeof(SpaceSector), nameof(SpaceSector.IsPlanetAstro));
        yield return AccessTools.Method(typeof(SpaceSector), nameof(SpaceSector.IsStarAstro));
        yield return AccessTools.Method(typeof(SectorPhysics), nameof(SectorPhysics.HashPhysBlock));
        yield return AccessTools.Method(typeof(SectorAudio), nameof(SectorAudio.HashAuBlock));
        yield return AccessTools.Method(typeof(AstroData), nameof(AstroData.IdString));
    }

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        if (code.Count(instruction => instruction.LoadsConstant(204899)) != 1)
            throw new InvalidOperationException("Galaxy astro classification boundary changed.");
        foreach (var instruction in code)
        {
            if (instruction.LoadsConstant(204899)) instruction.operand = GalaxyLimits.MaxGalaxyAstroId;
            yield return instruction;
        }
    }
}

[HarmonyPatch(typeof(SectorModel), "CalcGlobalSunLight")]
internal static class GalaxySunLightCapacity_Patch
{
    [HarmonyPrefix]
    internal static void Prefix(SectorModel __instance, ref double[] ___tmp_distLY2)
    {
        var count = __instance.gameData.galaxy.stars.Length;
        if (___tmp_distLY2.Length < count) Array.Resize(ref ___tmp_distLY2, count);
    }
}

[HarmonyPatch(typeof(SpaceColliderLogic), "MarkActivePos")]
internal static class GalaxyActiveColliderCapacity_Patch
{
    [HarmonyPrefix]
    internal static void Prefix(ref int[] ___activeColHashes, int ___activeColHashCount)
        => EnsureCapacity(ref ___activeColHashes, ___activeColHashCount);

    internal static void EnsureCapacity(ref int[] hashes, int count)
    {
        if (count >= hashes.Length) Array.Resize(ref hashes, Math.Max(768, checked(count * 2 + 1)));
    }
}

[HarmonyPatch(typeof(SpaceAudioLogic), "MarkActivePos")]
internal static class GalaxyActiveAudioCapacity_Patch
{
    [HarmonyPrefix]
    internal static void Prefix(ref int[] ___activeAuHashes, int ___activeAuHashCount)
        => GalaxyActiveColliderCapacity_Patch.EnsureCapacity(ref ___activeAuHashes, ___activeAuHashCount);
}

[HarmonyPatch(typeof(EnemyData), nameof(EnemyData.Formation),
    new[] { typeof(int), typeof(float), typeof(VectorLF3), typeof(UnityEngine.Quaternion), typeof(UnityEngine.Vector3) },
    new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Ref, ArgumentType.Ref })]
internal static class GalaxySpaceFormation_Patch
{
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var owner = AccessTools.Field(typeof(EnemyData), nameof(EnemyData.owner));
        var resolve = AccessTools.Method(typeof(GalaxyLimits), nameof(GalaxyLimits.SpaceFormationOwner));
        var count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.LoadsField(owner))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = resolve;
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Space formation owner reads changed: " + count);
    }
}

// These native previews construct temporary enemies with only a narrowed owner.
// Record the full hive index before the short conversion, just like live enemies.
[HarmonyPatch]
internal static class GalaxyTemporarySpaceEnemy_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SpaceColliderLogic), nameof(SpaceColliderLogic.RaycastAllEnemyUnitsAndForms));
        yield return AccessTools.Method(typeof(EnemyBriefInfo), nameof(EnemyBriefInfo.SetBriefInfo));
        yield return AccessTools.Method(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.UnderAttack),
            new[] { typeof(VectorLF3).MakeByRefType(), typeof(float) });
    }

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var owner = AccessTools.Field(typeof(EnemyData), nameof(EnemyData.owner));
        var setter = AccessTools.Method(typeof(GalaxyLimits), nameof(GalaxyLimits.SetSpaceFormationOwner));
        var stores = code.Select((instruction, index) => (instruction, index))
            .Where(item => item.instruction.StoresField(owner)).ToArray();
        if (stores.Length != 1 || stores[0].index == 0 || code[stores[0].index - 1].opcode != OpCodes.Conv_I2)
            throw new InvalidOperationException("Temporary space enemy owner conversion changed.");
        var index = stores[0].index;
        code[index - 1].opcode = OpCodes.Nop;
        code[index - 1].operand = null;
        code[index].opcode = OpCodes.Call;
        code[index].operand = setter;
        return code;
    }
}
