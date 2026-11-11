using HarmonyLib;
using NebulaePatcher.Patches.Transpilers;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class BuildToolGcTest
{
    [TestMethod]
    public void AllFiveNativeBuildToolsReplaceOnlyTheExplicitCollection()
    {
        var collect = AccessTools.Method(typeof(GC), nameof(GC.Collect), Type.EmptyTypes);
        var replacement = AccessTools.Method(typeof(BuildToolGc_Transpiler), "CollectOnlyInSingleplayer");
        var transpiler = AccessTools.Method(typeof(BuildToolGc_Transpiler), nameof(BuildToolGc_Transpiler.Transpiler));
        var targets = transpiler.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>()
            .Select(attribute => attribute.info.declaringType).ToArray();
        var expected = new[] { typeof(BuildTool_Click), typeof(BuildTool_Path), typeof(BuildTool_Addon),
            typeof(BuildTool_Inserter), typeof(BuildTool_BlueprintPaste) };
        CollectionAssert.AreEquivalent(expected, targets);
        foreach (var type in expected)
        {
            var method = AccessTools.Method(type, "CreatePrebuilds");
            var original = PatchProcessor.GetOriginalInstructions(method).ToArray();
            Check.AreEqual(1, original.Count(code => code.Calls(collect)), type.Name);
            var calls = original.Where(code => code.Calls(collect)).ToArray();
            var labels = calls[0].labels.ToArray();
            var blocks = calls[0].blocks.ToArray();
            var patched = BuildToolGc_Transpiler.Transpiler(original).ToArray();
            Check.AreEqual(original.Length, patched.Length, type.Name);
            Check.AreEqual(0, patched.Count(code => code.Calls(collect)), type.Name);
            var changed = patched.Single(code => code.Calls(replacement));
            Check.AreSame(calls[0], changed);
            CollectionAssert.AreEqual(labels, changed.labels.ToArray());
            CollectionAssert.AreEqual(blocks, changed.blocks.ToArray());
        }
    }
}
