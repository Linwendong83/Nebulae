using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Utils;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UniverseGen))]
internal static class ExtendedGalaxy_Patch
{
    private static readonly System.Reflection.FieldInfo GraphState = AccessTools.Field(typeof(UniverseGen), "tmp_state");
    [HarmonyPrefix, HarmonyPatch(nameof(UniverseGen.CreateGalaxy))]
    public static void Validate(GameDesc gameDesc) => GalaxyLimits.ValidateStarCount(gameDesc.starCount);

    [HarmonyPostfix, HarmonyPatch("GenerateTempPoses")]
    public static void CheckCount(int targetCount, int __result)
    {
        if (targetCount > GalaxyLimits.VanillaMaxStars && __result != targetCount)
            throw new InvalidOperationException($"Galaxy generation requested {targetCount} stars but produced {__result}.");
    }

    [HarmonyTranspiler, HarmonyPatch("RandomPoses")]
    public static IEnumerable<CodeInstruction> RandomPoses(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var bounds = code.Where(instruction => instruction.LoadsConstant(256)).ToArray();
        // The two collision retry loops precede the outer walk loop's final comparison.
        if (bounds.Length != 3) throw new InvalidOperationException("UniverseGen.RandomPoses loop bounds changed.");
        var index = code.IndexOf(bounds[2]);
        code[index] = new CodeInstruction(OpCodes.Ldarg_1).WithLabels(code[index].labels).WithBlocks(code[index].blocks);
        code.Insert(index + 1, new CodeInstruction(OpCodes.Call,
            AccessTools.Method(typeof(ExtendedGalaxy_Patch), nameof(WalkBudget))));
        return code;
    }

    public static int WalkBudget(int maxCount) => maxCount > GalaxyLimits.VanillaMaxStars * 4 ? Math.Max(256, maxCount) : 256;

    [HarmonyPrefix, HarmonyPatch(nameof(UniverseGen.CreateGalaxyStarGraph))]
    public static void GraphCapacity(GalaxyData galaxy)
    {
        var state = (int[])GraphState.GetValue(null);
        if (state == null || state.Length < galaxy.starCount)
            GraphState.SetValue(null, new int[Math.Max(128, galaxy.starCount)]);
    }
}

[HarmonyPatch(typeof(UIGalaxySelect))]
internal static class ExtendedGalaxyMenu_Patch
{
    [HarmonyTranspiler, HarmonyPatch(nameof(UIGalaxySelect.UpdateUIDisplay))]
    public static IEnumerable<CodeInstruction> Display_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var setter = AccessTools.PropertySetter(typeof(UnityEngine.UI.Slider), nameof(UnityEngine.UI.Slider.value));
        var display = AccessTools.Method(typeof(UIGalaxySelect_Patch), nameof(UIGalaxySelect_Patch.SetStarCountDisplay));
        var code = instructions.ToList();
        if (code.Count(instruction => instruction.Calls(setter)) != 1)
            throw new InvalidOperationException("UIGalaxySelect star count display changed.");
        return code.Select(instruction => instruction.Calls(setter)
            ? new CodeInstruction(OpCodes.Call, display).WithLabels(instruction.labels).WithBlocks(instruction.blocks)
            : instruction);
    }
}

[HarmonyPatch(typeof(StarGen), nameof(StarGen.CreateBirthStar))]
internal static class ExtendedGalaxyAstros_Patch
{
    [HarmonyPrefix]
    public static void Prefix(GalaxyData galaxy) => GalaxyLimits.EnsureAstros(galaxy);
}

[HarmonyPatch(typeof(SpaceSector), nameof(SpaceSector.SetForNewGame))]
internal static class ExtendedGalaxyHives_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        if (code.Count(instruction => instruction.LoadsConstant(100)) != 1)
            throw new InvalidOperationException("SpaceSector initial hive star limit changed.");
        foreach (var instruction in code)
            yield return instruction.LoadsConstant(100)
                ? new CodeInstruction(OpCodes.Ldc_I4, GalaxyLimits.MaxStars).WithLabels(instruction.labels).WithBlocks(instruction.blocks)
                : instruction;
    }
}

[HarmonyPatch(typeof(SectorModel), nameof(SectorModel.CreateGalaxyAstroBuffer))]
internal static class ExtendedGalaxyRender_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        if (code.Count(instruction => instruction.LoadsConstant(25600)) != 4)
            throw new InvalidOperationException("SectorModel galaxy astro buffer allocations changed.");
        foreach (var instruction in code)
        {
            if (!instruction.LoadsConstant(25600)) { yield return instruction; continue; }
            yield return new CodeInstruction(OpCodes.Ldarg_0).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ExtendedGalaxyRender_Patch), nameof(Capacity)));
        }
    }

    public static int Capacity(SectorModel model) => Math.Max(25600, checked(((model.sector?.galaxy?.starCount ?? 0) + 1) * 100));
}

[HarmonyPatch(typeof(GalaxyData), nameof(GalaxyData.ImportRuntimeData))]
internal static class ExtendedGalaxyRuntimeData_Patch
{
    [HarmonyPrefix]
    internal static bool Prefix(GalaxyData __instance, BinaryReader r)
    {
        var version = r.ReadInt32();
        var budget = RecordBudget(__instance);
        for (var count = 0; ; count++)
        {
            var id = r.ReadInt32();
            if (id == -1) break;
            if (count >= budget) throw new InvalidDataException("Too many scanned planet records.");
            var planet = __instance.PlanetByAstroId(id) ?? throw new InvalidDataException("Unknown scanned planet: " + id);
            lock (PlanetModelingManager.planetProcessingLock) planet.ImportScannedData(r);
        }
        if (version < 1) return false;
        for (var count = 0; ; count++)
        {
            var id = r.ReadInt32();
            if (id == -1) break;
            if (count >= budget) throw new InvalidDataException("Too many planet vegetation records.");
            var planet = __instance.PlanetByAstroId(id) ?? throw new InvalidDataException("Unknown vegetation planet: " + id);
            var active = r.ReadInt32();
            if (active is not (0 or 1)) throw new InvalidDataException("Invalid planet vegetation flag.");
            planet.vegeCollectionActive = active == 1;
        }
        return false;
    }

    // Both sentinel-terminated lists can contain a record for every planet.
    // Ending either loop early leaves the reader inside that list and corrupts
    // every following native save section, even though the file is complete.
    internal static int RecordBudget(GalaxyData galaxy) => Math.Max(25600, galaxy.stars.Sum(star => star.planetCount));
}
