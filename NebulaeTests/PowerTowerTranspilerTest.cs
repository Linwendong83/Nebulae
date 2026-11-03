using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class PowerTowerTranspilerTest
{
    private static Type PatchType => typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType(
        "NebulaePatcher.Patches.Transpilers.PowerSystem_Transpiler", true)!;

    private static List<CodeInstruction> Patch(IEnumerable<CodeInstruction> code, ILGenerator generator) =>
        (List<CodeInstruction>)PatchType.GetMethod("PatchChargerInstructions", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [code, generator])!;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BranchesKeepUniqueLabelsAndOriginalExceptionMetadata(bool shortBranches)
    {
        var generator = new DynamicMethod("ChargerFixture", typeof(void), Type.EmptyTypes).GetILGenerator();
        var node = generator.DeclareLocal(typeof(PowerNodeComponent).MakeByRefType());
        var nodeId = generator.DeclareLocal(typeof(int));
        var demand = generator.DeclareLocal(typeof(long));
        var sum = generator.DefineLabel();
        var next = generator.DefineLabel();
        var code = new List<CodeInstruction>
        {
            new(OpCodes.Ldloc, node),
            new(OpCodes.Ldfld, AccessTools.Field(typeof(PowerNodeComponent), "isCharger")),
            new(shortBranches ? OpCodes.Brfalse_S : OpCodes.Brfalse, next),
            new(OpCodes.Ldloc, node),
            new(OpCodes.Ldfld, AccessTools.Field(typeof(PowerNodeComponent), "coverRadius")),
            new(OpCodes.Pop),
            new(shortBranches ? OpCodes.Br_S : OpCodes.Br, sum),
            new(OpCodes.Ldloc, node),
            new(OpCodes.Ldfld, AccessTools.Field(typeof(PowerNodeComponent), "requiredEnergy")),
            new(OpCodes.Conv_I8),
            new(OpCodes.Stloc, demand),
            new(OpCodes.Ldloc, node),
            new(OpCodes.Ldfld, AccessTools.Field(typeof(PowerNodeComponent), "id")),
            new(OpCodes.Ldloc, nodeId),
            new(shortBranches ? OpCodes.Bne_Un_S : OpCodes.Bne_Un, next),
            new(OpCodes.Ldfld, AccessTools.Field(typeof(AnimData), "state")),
            new(OpCodes.Ldc_I4_2),
            new(shortBranches ? OpCodes.Bne_Un_S : OpCodes.Bne_Un, next),
            new(OpCodes.Nop),
            new(OpCodes.Ret)
        };
        code[7].labels.Add(sum); // Regression: the old patch reused this labeled load and jumped to itself.
        code[19].labels.Add(next);
        code[0].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        code[19].blocks.Add(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
        var before = Describe(code);

        var patched = Patch(code, generator);

        CollectionAssert.AreEqual(before, Describe(code), "Input instructions must remain untouched.");
        VerifyControlFlow(patched);
        CollectionAssert.AreEqual(code.SelectMany(i => i.blocks).ToArray(), patched.SelectMany(i => i.blocks).ToArray());
        TestAssert.IsFalse(patched.Any(i => i.opcode.OperandType == OperandType.ShortInlineBrTarget));
    }

    [TestMethod]
    public void MissingLateMatchReturnsUnmodifiedInput()
    {
        var generator = new DynamicMethod("MissingChargerFixture", typeof(void), Type.EmptyTypes).GetILGenerator();
        CodeInstruction[] code = [new(OpCodes.Nop), new(OpCodes.Ret)];
        var before = Describe(code);
        var result = (IEnumerable<CodeInstruction>)PatchType.GetMethod("PowerSystem_GameTick_Transpiler")!
            .Invoke(null, [code, generator])!;
        CollectionAssert.AreEqual(code, result.ToArray());
        CollectionAssert.AreEqual(before, Describe(code));
        TestAssert.IsFalse((bool)PatchType.GetProperty("ChargerSyncEnabled", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!);
    }

    [TestMethod]
    public void RealGameMethodsMatchAndHarmonyCanEmitThePatch()
    {
        var original = typeof(PowerSystem).GetMethod("GameTick")!;
        TestAssert.IsGreaterThanOrEqualTo(100,
            original.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0, "Transpiler tests require the installed game's real method bodies.");

        var instructions = PatchProcessor.GetOriginalInstructions(original, out var generator);
        var before = Describe(instructions);
        var patched = Patch(instructions, generator);
        VerifyControlFlow(patched);
        CollectionAssert.AreEqual(before, Describe(instructions));
        CollectionAssert.AreEqual(instructions.SelectMany(i => i.blocks).ToArray(), patched.SelectMany(i => i.blocks).ToArray());

        // Also exercise rollback after the demand anchors matched but the later charging anchor did not.
        var missingEnergy = instructions.Where(i => !i.LoadsField(AccessTools.Field(typeof(AnimData), "state"))).ToArray();
        var rollback = (IEnumerable<CodeInstruction>)PatchType.GetMethod("PowerSystem_GameTick_Transpiler")!
            .Invoke(null, [missingEnergy, generator])!;
        CollectionAssert.AreEqual(missingEnergy, rollback.ToArray());

        var harmony = new Harmony("nebulae.tests.power-tower");
        try
        {
            harmony.Patch(original, transpiler: new HarmonyMethod(PatchType.GetMethod("PowerSystem_GameTick_Transpiler")));
            TestAssert.IsTrue((bool)PatchType.GetProperty("ChargerSyncEnabled", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!);
            var uiType = PatchType.Assembly.GetType("NebulaePatcher.Patches.Transpilers.UIPowerNodeWindow_Transpiler", true)!;
            var uiMethod = AccessTools.Method(typeof(UIPowerNodeWindow), "_OnUpdate");
            harmony.Patch(uiMethod, transpiler: new HarmonyMethod(uiType.GetMethod("OnUpdate_Transpiler")));
            var uiInstructions = PatchProcessor.GetCurrentInstructions(uiMethod);
            TestAssert.IsTrue(uiInstructions.Any(i => i.operand is MethodInfo method && method.Name == "ChargingEnergy"));
            TestAssert.IsTrue(uiInstructions.Any(i => i.operand is MethodInfo method && method.Name == "ChargeStateText"));
        }
        finally
        {
            harmony.UnpatchSelf();
        }
    }

    private static string[] Describe(IEnumerable<CodeInstruction> code) => code.Select(i =>
        i + " labels=" + string.Join(",", i.labels.Select(label => label.GetHashCode())) +
        " blocks=" + string.Join(",", i.blocks.Select(block => block.blockType))).ToArray();

    private static void VerifyControlFlow(List<CodeInstruction> code)
    {
        var labels = code.SelectMany((instruction, index) => instruction.labels.Select(label => (label, index))).ToArray();
        TestAssert.AreEqual(labels.Length, labels.Select(pair => pair.label).Distinct().Count(), "Duplicate label definition.");
        var targets = labels.ToDictionary(pair => pair.label, pair => pair.index);
        foreach (var instruction in code)
        {
            if (instruction.operand is Label target) TestAssert.IsTrue(targets.ContainsKey(target), "Dangling branch.");
            if (instruction.operand is Label[] cases)
                foreach (var targetCase in cases) TestAssert.IsTrue(targets.ContainsKey(targetCase), "Dangling switch case.");
        }
        foreach (var name in new[] { "SetChargerRequiredPower", "AddMechaEnergy" })
        {
            var call = code.FindIndex(i => i.operand is MethodInfo method && method.Name == name);
            TestAssert.IsGreaterThanOrEqualTo(0, call, name);
            TestAssert.IsTrue(code[call + 1].operand is Label, name + " must branch to a forward continuation.");
            TestAssert.IsGreaterThan(call + 1, targets[(Label)code[call + 1].operand], name + " must not jump back to itself.");
        }
    }
}
