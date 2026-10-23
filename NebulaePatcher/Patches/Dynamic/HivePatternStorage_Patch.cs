using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch]
internal static class HivePatternAccess_Patch
{
    private static readonly FieldInfo field = AccessTools.Field(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.pbuilders));
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in new[] { typeof(EnemyDFHiveSystem), typeof(SpaceSector), typeof(EnemyDFHiveRenderer),
                     typeof(DFSCoreComponent), typeof(DFRelayComponent), typeof(BGMController),
                     typeof(UIDarkFogMonitorEntry), AccessTools.TypeByName("TestCombatDetails") })
        {
            if (type == null) continue;
            foreach (var method in AccessTools.GetDeclaredMethods(type))
            {
                if (method.GetMethodBody() == null) continue;
                // These virtual-state queries only test nullness, or use the retained core prefix.
                if (type == typeof(EnemyDFHiveSystem) && (method.Name == "get_isAlive" || method.Name == "get_isVisible" || method.Name == "get_hasAnyStructureOrUnit")) continue;
                if (type == typeof(EnemyDFHiveSystem) && method.Name == nameof(EnemyDFHiveSystem.Export)) continue;
                // The count prefix already handles cold geometry. Keep the native warm-array loop
                // so each builder access does not repeat a weak-table lookup during replication.
                if (type == typeof(EnemyDFHiveSystem) && method.Name == "get_totalAvailableBuildingCount") continue;
                if (type == typeof(DFSCoreComponent) && method.Name == nameof(DFSCoreComponent.LogicTickVirtual)) continue;
                if (PatchProcessor.GetOriginalInstructions(method).Any(i => i.opcode == OpCodes.Ldfld && Equals(i.operand, field))) yield return method;
            }
        }
    }
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var read = AccessTools.Method(typeof(HivePatternStorage), nameof(HivePatternStorage.Read));
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, field))
            { instruction.opcode = OpCodes.Call; instruction.operand = read; }
            yield return instruction;
        }
    }
}

// Tinder target selection scans distant hives, but only reads a core's instance id.
// Preserve that read from the retained prefix; uncommon out-of-prefix indices still expand.
[HarmonyPatch]
internal static class HivePatternCoreAccess_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        var field = AccessTools.Field(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.pbuilders));
        return AccessTools.GetDeclaredMethods(typeof(DFTinderComponent)).Where(m => m.GetMethodBody() != null &&
            PatchProcessor.GetOriginalInstructions(m).Any(i => i.opcode == OpCodes.Ldfld && Equals(i.operand, field)));
    }
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
    {
        var instructions = source.ToList();
        var field = AccessTools.Field(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.pbuilders));
        var instance = AccessTools.Field(typeof(GrowthPattern_DFSpace.Builder), nameof(GrowthPattern_DFSpace.Builder.instBuilderId));
        var enemy = AccessTools.Field(typeof(GrowthPattern_DFSpace.Builder), nameof(GrowthPattern_DFSpace.Builder.instId));
        for (var i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].opcode != OpCodes.Ldfld || !Equals(instructions[i].operand, field)) continue;
            var element = i + 1;
            while (element < Math.Min(i + 16, instructions.Count) && instructions[element].opcode != OpCodes.Ldelema) element++;
            if (element + 1 >= instructions.Count || instructions[element].opcode != OpCodes.Ldelema ||
                !Equals(instructions[element].operand, typeof(GrowthPattern_DFSpace.Builder)) ||
                instructions[element + 1].opcode != OpCodes.Ldfld ||
                (!Equals(instructions[element + 1].operand, instance) && !Equals(instructions[element + 1].operand, enemy)))
                throw new InvalidOperationException("Tinder core pattern access changed: " + string.Join(" | ", instructions.Skip(i).Take(20)));
            instructions[i].opcode = OpCodes.Nop; instructions[i].operand = null;
            instructions[element].opcode = OpCodes.Call;
            instructions[element].operand = AccessTools.Method(typeof(HivePatternStorage),
                Equals(instructions[element + 1].operand, instance) ? nameof(HivePatternStorage.CoreInstanceId) : nameof(HivePatternStorage.CoreEnemyId));
            instructions[element + 1].opcode = OpCodes.Nop; instructions[element + 1].operand = null;
        }
        return instructions;
    }
}

[HarmonyPatch]
internal static class HivePatternLoaded_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.Import));
        yield return AccessTools.Method(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.SetForNewCreate));
        yield return AccessTools.Method(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.ClosePreview));
    }
    [HarmonyPrefix]
    internal static void Prefix(EnemyDFHiveSystem __instance, MethodBase __originalMethod)
    {
        if (__originalMethod.Name != nameof(EnemyDFHiveSystem.ClosePreview))
        { HivePatternStorage.WaitImport(__instance); HivePatternStorage.Forget(__instance); }
    }
    [HarmonyPostfix]
    internal static void Postfix(EnemyDFHiveSystem __instance, MethodBase __originalMethod)
    {
        if (__originalMethod.Name == nameof(EnemyDFHiveSystem.Import)) HivePatternStorage.ScheduleImport(__instance);
        else HivePatternStorage.Compact(__instance);
    }
}

[HarmonyPatch(typeof(SpaceSector), nameof(SpaceSector.Import))]
internal static class HivePatternImportBarrier_Patch
{
    [HarmonyFinalizer]
    internal static void Finalizer() => HivePatternStorage.CompleteImports();
}

[HarmonyPatch(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.Export))]
internal static class HivePatternExport_Patch
{
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
    {
        var instructions = source.ToList();
        var field = AccessTools.Field(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.pbuilders));
        var realized = AccessTools.Field(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.realized));
        var start = instructions.FindIndex(i => i.opcode == OpCodes.Ldfld && Equals(i.operand, field)) - 2;
        var end = instructions.FindIndex(i => i.opcode == OpCodes.Ldfld && Equals(i.operand, realized)) - 2;
        var export = AccessTools.Method(typeof(GrowthPattern_DFSpace.Builder), nameof(GrowthPattern_DFSpace.Builder.Export));
        if (start < 0 || end <= start || instructions[start].opcode != OpCodes.Ldarg_1 || instructions[start + 1].opcode != OpCodes.Ldarg_0 ||
            instructions[end].opcode != OpCodes.Ldarg_1 || instructions[end + 1].opcode != OpCodes.Ldarg_0 ||
            instructions.Skip(start).Take(end - start).Count(i => i.Calls(export)) != 1)
            throw new InvalidOperationException("Hive builder export loop changed.");
        var first = new CodeInstruction(OpCodes.Ldarg_0);
        first.labels.AddRange(instructions[start].labels); first.blocks.AddRange(instructions[start].blocks);
        var replacement = new[] { first, new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HivePatternStorage), nameof(HivePatternStorage.WriteBuilders))) };
        instructions.RemoveRange(start, end - start); instructions.InsertRange(start, replacement);
        return instructions;
    }
    [HarmonyPrefix]
    internal static void Prefix(EnemyDFHiveSystem __instance, out bool __state)
    {
        __state = HivePatternStorage.IsCold(__instance);
        if (!HivePatternStorage.CanExportCold(__instance)) HivePatternStorage.Read(__instance);
    }
    [HarmonyFinalizer]
    internal static void Finalizer(EnemyDFHiveSystem __instance, bool __state)
    {
        if (__state) HivePatternStorage.RecompactExport(__instance);
        else HivePatternStorage.Compact(__instance);
    }
}

[HarmonyPatch]
internal static class HivePatternActivate_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.Realize));
        yield return AccessTools.Method(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.OpenPreview));
    }
    [HarmonyPrefix]
    internal static void Prefix(EnemyDFHiveSystem __instance)
    {
        HivePatternStorage.Read(__instance);
        HivePatternStorage.Forget(__instance);
    }
}

[HarmonyPatch(typeof(EnemyDFHiveSystem), "get_totalAvailableBuildingCount")]
internal static class HivePatternCount_Patch
{
    [HarmonyPrefix]
    internal static bool Prefix(EnemyDFHiveSystem __instance, ref int __result)
    {
        if (!HivePatternStorage.TryCompletedCount(__instance, out var count)) return true;
        __result = count;
        return false;
    }
}

[HarmonyPatch]
internal static class HiveFormationWritable_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(EnemyFormation), nameof(EnemyFormation.AddUnit));
        yield return AccessTools.Method(typeof(EnemyFormation), nameof(EnemyFormation.RemoveUnit));
        yield return AccessTools.Method(typeof(EnemyFormation), nameof(EnemyFormation.Clear));
    }
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(EnemyFormation __instance) => HiveSharedArrays.Writable(__instance);
}

[HarmonyPatch(typeof(GameData), nameof(GameData.Destroy))]
internal static class HivePatternLifetime_Patch
{
    [HarmonyPrefix]
    internal static void Prefix(GameData __instance, out bool __state)
    {
        // No compression job may retain arrays while native teardown frees the sector.
        HivePatternStorage.CompleteImports();
        __state = ReferenceEquals(__instance, GameMain.data);
    }
    [HarmonyPostfix]
    internal static void Postfix(bool __state) { if (__state) HivePatternStorage.Reset(); }
}

[HarmonyPatch(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.Free))]
internal static class HivePatternRelease_Patch
{
    [HarmonyPrefix]
    internal static void Prefix(EnemyDFHiveSystem __instance)
    {
        HivePatternStorage.WaitImport(__instance);
        if (__instance.realized || __instance.isPreview) HivePatternStorage.Read(__instance);
        HivePatternStorage.Forget(__instance);
    }
}
