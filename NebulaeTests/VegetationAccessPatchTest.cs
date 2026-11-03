using HarmonyLib;
using NebulaePatcher.Patches.Transpilers;
using NebulaeWorld.Player;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class VegetationAccessPatchTest
{
    [TestMethod]
    public void VanillaFortyCellPitSearchUsesFourHundredBufferEntries()
    {
        var packet = new global::NebulaeModel.Packets.Factory.Foundation.FoundationBuildUpdatePacket
        {
            IsCircle = true,
            ReformSize = 40,
            ReformIndices = new int[400],
            CirclePointCount = 177,
            Radius = 11.25f,
            Fade0 = 3,
            ReformType = 1,
            ReformColor = 0,
            PitRuinId = 1
        };
        TestAssert.IsTrue(global::NebulaeNetwork.PacketProcessors.Factory.Foundation.FoundationBuildUpdateProcessor.ValidBrush(packet));
        packet.CirclePointCount = 401;
        TestAssert.IsFalse(global::NebulaeNetwork.PacketProcessors.Factory.Foundation.FoundationBuildUpdateProcessor.ValidBrush(packet));
        packet.IsCircle = false; packet.PitRuinId = 0;
        TestAssert.IsFalse(global::NebulaeNetwork.PacketProcessors.Factory.Foundation.FoundationBuildUpdateProcessor.ValidBrush(packet));
    }

    [TestMethod]
    public void NativeFoundationCallsKeepSquareAndCircleOperationsDistinct()
    {
        var replacement = AccessTools.Method(typeof(FoundationTool_Transpiler), nameof(FoundationTool_Transpiler.Flatten));
        foreach (var method in FoundationTool_Transpiler.TargetMethods())
        {
            var result = FoundationTool_Transpiler.Transpiler(PatchProcessor.GetOriginalInstructions(method), method).ToArray();
            var calls = result.Select((instruction, index) => (instruction, index)).Where(x => x.instruction.Calls(replacement)).ToArray();
            TestAssert.HasCount(method.Name == nameof(BuildTool_Reform.RemoveBasePit) ? 1 : 2, calls);
            for (var i = 0; i < calls.Length; i++)
                TestAssert.IsTrue(result[calls[i].index - 1].LoadsConstant(method.Name == nameof(BuildTool_Reform.RemoveBasePit) || i > 0 ? 1 : 0));
        }
    }

    [TestMethod]
    public void FoundationPacketsUseTheActualFactoryAndOwnTheirBrushArrays()
    {
        var factory = (PlanetFactory)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PlanetFactory));
        AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, new PlanetData { id = 207 });
        var tool = (BuildTool_Reform)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(BuildTool_Reform));
        tool.cursorIndices = new[] { 1, 2, 3, 4 }; tool.extraCircleIndices = new[] { 5, 6, 7, 8 }; tool.circlePointCount = 4;
        var square = new global::NebulaeModel.Packets.Factory.Foundation.FoundationBuildUpdatePacket(factory, tool, default, 3, 2, false, 3, false);
        var circle = new global::NebulaeModel.Packets.Factory.Foundation.FoundationBuildUpdatePacket(factory, tool, default, 3, 2, false, 3, true);
        tool.cursorIndices[0] = 99; tool.extraCircleIndices[0] = 99;
        TestAssert.AreEqual(207, square.PlanetId); TestAssert.AreEqual(1, square.ReformIndices[0]);
        TestAssert.IsFalse(square.IsCircle); TestAssert.IsTrue(circle.IsCircle); TestAssert.AreEqual(5, circle.ReformIndices[0]);
    }

    [TestMethod]
    public void NativeTerrainBuildAndPlantingCallsCannotInlinePastCollectionOwnership()
    {
        var getter = AccessTools.PropertyGetter(typeof(Player), nameof(Player.vegetableCollection));
        var replacement = AccessTools.Method(typeof(VegetationAccess), nameof(VegetationAccess.Collection));
        var targets = VegetationAccess_Transpiler.TargetMethods().ToHashSet();
        var covered = 0;
        foreach (var type in new[] { typeof(PlanetFactory), typeof(BuildTool_BlueprintPaste), typeof(PlayerAction_Mine), typeof(PlayerAction_Plant), typeof(PlayerAction_Build) })
            foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (method.GetMethodBody() == null) continue;
                var source = PatchProcessor.GetOriginalInstructions(method);
                var count = source.Count(i => i.Calls(getter));
                if (count == 0) continue;
                covered++;
                TestAssert.Contains(method, targets);
                var result = VegetationAccess_Transpiler.Transpiler(source).ToArray();
                TestAssert.AreEqual(0, result.Count(i => i.Calls(getter)), method.Name);
                TestAssert.AreEqual(count, result.Count(i => i.Calls(replacement)), method.Name);
            }
        TestAssert.IsGreaterThanOrEqualTo(5, covered);
    }
}
