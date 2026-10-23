using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Authority;

namespace NebulaePatcher.Patches.Authority;

/// <summary>Reuse native refund rules while leaving inventory and recipe mutation to the commit.</summary>
[HarmonyPatch(typeof(FactorySystem))]
internal static class FactoryRefunds
{
    [ThreadStatic] private static List<PlayerInventoryTransferLedger.Transfer> collecting;
    public static IReadOnlyList<PlayerInventoryTransferLedger.Transfer> Collect(FactorySystem system, int assembler)
    {
        if (collecting != null) throw new InvalidOperationException("Nested native assembler refund collection.");
        collecting = [];
        try { ReadRefunds(system, null, assembler); return collecting.ToArray(); }
        finally { collecting = null; }
    }
    public static IReadOnlyList<PlayerInventoryTransferLedger.Transfer> CollectLab(FactorySystem system, int laboratory)
    {
        if (collecting != null) throw new InvalidOperationException("Nested native factory refund collection.");
        collecting = [];
        try { ReadLabRefunds(system, null, laboratory); return collecting.ToArray(); }
        finally { collecting = null; }
    }

    [HarmonyReversePatch(HarmonyReversePatchType.Original)]
    [HarmonyPatch(nameof(FactorySystem.TakeBackItems_Assembler))]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter",
        Justification = "Harmony reverse patches must retain the native instance and argument signature.")]
    private static void ReadRefunds(FactorySystem instance, Player player, int assembler)
    {
        _ = Transpiler(null);
        throw new NotImplementedException("Native assembler refund reverse patch was not installed.");
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => Transform(instructions);
    }
    [HarmonyReversePatch(HarmonyReversePatchType.Original)]
    [HarmonyPatch(nameof(FactorySystem.TakeBackItems_Lab))]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter",
        Justification = "Harmony reverse patches must retain the native instance and argument signature.")]
    private static void ReadLabRefunds(FactorySystem instance, Player player, int laboratory)
    {
        _ = Transpiler(null);
        throw new NotImplementedException("Native laboratory refund reverse patch was not installed.");
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => Transform(instructions);
    }
    private static IEnumerable<CodeInstruction> Transform(IEnumerable<CodeInstruction> instructions)
    {
        var add = AccessTools.Method(typeof(Player), nameof(Player.TryAddItemToPackage));
        var tip = AccessTools.Method(typeof(UIItemup), nameof(UIItemup.Up), [typeof(int), typeof(int)]);
        var recipe = AccessTools.Method(typeof(AssemblerComponent), nameof(AssemblerComponent.SetRecipe));
        var lab = AccessTools.Method(typeof(LabComponent), nameof(LabComponent.SetFunction));
        foreach (var instruction in instructions)
        {
            CodeInstruction replacement = null;
            if (instruction.Calls(add)) replacement = HarmonyLib.Transpilers.EmitDelegate<Func<Player, int, int, int, bool, int, bool, int>>(
                (_, item, count, inc, _, _, _) => { collecting.Add(new PlayerInventoryTransferLedger.Transfer(item, count, inc)); return count; });
            else if (instruction.Calls(tip)) replacement = HarmonyLib.Transpilers.EmitDelegate<Action<int, int>>((_, _) => { });
            else if (instruction.Calls(recipe)) replacement = HarmonyLib.Transpilers.EmitDelegate<IgnoreRecipe>((ref AssemblerComponent _, int _, SignData[] _) => { });
            else if (instruction.Calls(lab)) replacement = HarmonyLib.Transpilers.EmitDelegate<IgnoreLab>((ref LabComponent _, bool _, int _, int _, SignData[] _) => { });
            if (replacement != null) { instruction.opcode = replacement.opcode; instruction.operand = replacement.operand; }
            yield return instruction;
        }
    }
    private delegate void IgnoreRecipe(ref AssemblerComponent assembler, int recipe, SignData[] signs);
    private delegate void IgnoreLab(ref LabComponent laboratory, bool research, int recipe, int tech, SignData[] signs);
}
