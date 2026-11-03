using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaePatcher.Patches.Transpilers;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
[DoNotParallelize]
internal class StationStorageTranspilerTest
{
    private static List<CodeInstruction> Write(params CodeInstruction[] address)
    {
        var result = address.ToList();
        result.AddRange([
            new CodeInstruction(OpCodes.Ldelema, typeof(StationStore)),
            new CodeInstruction(OpCodes.Ldflda, AccessTools.Field(typeof(StationStore), nameof(StationStore.inc))),
            new CodeInstruction(OpCodes.Dup), new CodeInstruction(OpCodes.Ldind_I4),
            new CodeInstruction(OpCodes.Ldloc_S, 0), new CodeInstruction(OpCodes.Sub), new CodeInstruction(OpCodes.Stind_I4)
        ]);
        return result;
    }

    [TestMethod]
    public void ReceiverRecoveryHandlesBothStationStorageAndCompilerArrayAliases()
    {
        var direct = Write(new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(StationComponent), nameof(StationComponent.storage))),
            new CodeInstruction(OpCodes.Ldloc_S, 3));
        var alias = Write(new CodeInstruction(OpCodes.Ldloc_S, 7), new CodeInstruction(OpCodes.Ldloc_S, 9),
            new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(SupplyDemandPair), nameof(SupplyDemandPair.supplyIndex))));
        var first = StationComponent_Transpiler.StorageUpdateArguments(direct, direct.Count - 1);
        var second = StationComponent_Transpiler.StorageUpdateArguments(alias, alias.Count - 1);
        TestAssert.IsNotNull(first); TestAssert.IsNotNull(second);
        TestAssert.HasCount(3, first);
        TestAssert.AreEqual(nameof(StationComponent.storage), ((FieldInfo)first[1].operand).Name);
        TestAssert.AreEqual(7, second[0].operand);
        TestAssert.AreEqual(nameof(SupplyDemandPair.supplyIndex), ((FieldInfo)second[2].operand).Name);
        var unknown = Write(new CodeInstruction(OpCodes.Call, typeof(object).GetMethod(nameof(ToString))), new CodeInstruction(OpCodes.Ldloc_0));
        TestAssert.IsNull(StationComponent_Transpiler.StorageUpdateArguments(unknown, unknown.Count - 1));
    }

    private static List<CodeInstruction> captured = [];
    private static IEnumerable<CodeInstruction> Capture(IEnumerable<CodeInstruction> instructions)
    {
        captured = StationComponent_Transpiler.InternalTickRemote_Transpiler(instructions).ToList();
        return captured;
    }

    [TestMethod]
    public void InstalledGameCapturesEveryCountAndIncrementDebitIncludingInitialDispatch()
    {
        foreach (var pair in new[] { (nameof(StationComponent.InternalTickRemote), 4), ("DispatchSupplyShip", 2) })
        {
            var original = AccessTools.Method(typeof(StationComponent), pair.Item1);
            TestAssert.IsGreaterThan(5,
                original.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0, "Storage instrumentation requires the installed game's real method bodies.");
            var harmony = new Harmony("NebulaeTests.NativeStorageDelta");
            try { harmony.Patch(original, transpiler: new HarmonyMethod(typeof(StationStorageTranspilerTest), nameof(Capture))); }
            finally { harmony.UnpatchAll("NebulaeTests.NativeStorageDelta"); }
            var writes = 0; var instrumented = 0;
            for (var i = 5; i < captured.Count; i++)
            {
                if (captured[i].opcode != OpCodes.Stind_I4 || captured[i - 5].opcode != OpCodes.Ldflda ||
                    captured[i - 5].operand is not FieldInfo field || field.DeclaringType != typeof(StationStore) ||
                    (field.Name != nameof(StationStore.count) && field.Name != nameof(StationStore.inc))) continue;
                writes++;
                for (var j = i + 1; j < Math.Min(i + 10, captured.Count); j++)
                {
                    if (captured[j].opcode != OpCodes.Call || captured[j].operand is not MethodInfo called) continue;
                    var types = called.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
                    if (types.SequenceEqual(new[] { typeof(StationStore[]), typeof(int), typeof(int), typeof(bool) })) instrumented++;
                    break;
                }
            }
            TestAssert.AreEqual(pair.Item2, writes, pair.Item1);
            TestAssert.AreEqual(writes, instrumented, pair.Item1);
        }
    }
}
