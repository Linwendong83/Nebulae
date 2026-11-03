using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Authority;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld.GameDataHistory;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class LaboratoryResearchTest
{
    [TestMethod]
    public void LoadedFactoriesShareOneNativeBudgetWithoutChangingGlobalFacts()
    {
        var world = new AuthorityEpoch(19, 23); var prediction = new LaboratoryResearchPrediction();
        var canonical = new TechState { curLevel = 0, hashUploaded = 90, hashNeeded = 100 };
        var firstFactory = prediction.Read(world, 1011, canonical);
        firstFactory.hashUploaded += 7; prediction.Write(1011, firstFactory);
        var secondFactory = prediction.Read(world, 1011, canonical);
        TestAssert.AreEqual(3L, secondFactory.hashNeeded - secondFactory.hashUploaded);
        secondFactory.hashUploaded += 3; prediction.Write(1011, secondFactory);
        TestAssert.AreEqual(100L, prediction.Read(world, 1011, canonical).hashUploaded);
        TestAssert.AreEqual(90L, canonical.hashUploaded);
    }

    [TestMethod]
    public void CheckpointsLevelChangesAndNewWorldsReplaceOnlyLocalPrediction()
    {
        var world = new AuthorityEpoch(19, 23); var prediction = new LaboratoryResearchPrediction();
        var canonical = new TechState { curLevel = 1, hashUploaded = 10, hashNeeded = 100 };
        var local = prediction.Read(world, 1011, canonical); local.hashUploaded = 70; prediction.Write(1011, local);
        canonical.hashUploaded = 30;
        TestAssert.AreEqual(30L, prediction.Read(world, 1011, canonical).hashUploaded);
        local.hashUploaded = 80; prediction.Write(1011, local);
        TestAssert.AreEqual(30L, prediction.Read(new AuthorityEpoch(20, 24), 1011, canonical).hashUploaded);
        canonical.curLevel = 2; canonical.hashUploaded = 0;
        TestAssert.AreEqual(2, prediction.Read(world, 1011, canonical).curLevel);
        canonical.unlocked = true; canonical.hashUploaded = 100;
        local = prediction.Read(world, 1011, canonical);
        TestAssert.IsFalse(local.unlocked, "FactorySystem waits for the host's queue transition.");
        TestAssert.IsTrue(canonical.unlocked);
        prediction.Write(0, default); TestAssert.AreEqual(100L, prediction.Read(world, 1011, canonical).hashUploaded);
    }

    [TestMethod]
    public void InstalledFactoryTickRoutesAllGlobalWritesAndLocksThroughOwnershipHooks()
    {
        var method = AccessTools.Method(typeof(FactorySystem), nameof(FactorySystem.GameTickLabResearchMode));
        var original = PatchProcessor.GetOriginalInstructions(method).ToArray();
        var transformed = LaboratoryResearch_Patch.Transpile(original).ToArray();
        var globalSetter = AccessTools.PropertySetter(typeof(Dictionary<int, TechState>), "Item");
        var hashes = AccessTools.Field(typeof(GameStatData), nameof(GameStatData.techHashedThisFrame));
        var universePoints = AccessTools.Field(typeof(GameHistoryData), nameof(GameHistoryData.universeMatrixPointUploaded));
        TestAssert.IsFalse(transformed.Any(instruction => instruction.operand is MethodInfo called && called == globalSetter));
        TestAssert.IsFalse(transformed.Any(instruction => instruction.opcode == OpCodes.Stfld && (Equals(instruction.operand, hashes) || Equals(instruction.operand, universePoints))));
        TestAssert.AreEqual(3, transformed.Count(instruction => instruction.operand is MethodInfo called && called.DeclaringType == typeof(LaboratoryResearch_Patch) && called.Name == "Write"));
        TestAssert.AreEqual(6, transformed.Count(instruction => instruction.operand is MethodInfo called && called.DeclaringType == typeof(LaboratoryResearch_Patch) && (called.Name == "EnterWrite" || called.Name == "ExitWrite")));
        var labTick = AccessTools.Method(typeof(LabComponent), nameof(LabComponent.InternalUpdateResearch));
        TestAssert.AreEqual(1, transformed.Count(instruction => Equals(instruction.operand, labTick)), "The original consumption algorithm is retained.");
        TestAssert.Throws<InvalidOperationException>(() => LaboratoryResearch_Patch.Transpile(original.Where(instruction => !Equals(instruction.operand, universePoints))).ToArray());
    }
}
