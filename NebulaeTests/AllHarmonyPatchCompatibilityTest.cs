using HarmonyLib;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
[DoNotParallelize]
public class AllHarmonyPatchCompatibilityTest
{
    [TestMethod]
    public void EveryDeclaredGamePatchResolvesAgainstTheInstalledGameBody()
    {
        var method = AccessTools.Method(typeof(GameLogic), nameof(GameLogic.LogicFrame));
        TestAssert.IsGreaterThan(5, method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0,
            "Run with the real game assembly, not reference stubs.");

        var failures = new List<string>();
        foreach (var patchClass in typeof(NebulaePatcher.NebulaePlugin).Assembly.GetTypes().Where(type =>
                     type.Namespace?.StartsWith("NebulaePatcher.Patches.", StringComparison.Ordinal) == true &&
                     type.GetCustomAttributesData().Any(attribute => attribute.AttributeType == typeof(HarmonyPatch))))
        {
            try
            {
                foreach (var (target, patch) in DeclaredPatches(patchClass))
                {
                    if (target == null) failures.Add(patchClass.FullName + "." + patch.Name + ": target missing");
                    else if (target.DeclaringType?.Assembly == typeof(GameLogic).Assembly &&
                             target.GetMethodBody() == null)
                        failures.Add(patchClass.FullName + "." + patch.Name + ": missing game body");
                }
            }
            catch (Exception ex) { failures.Add(patchClass.FullName + ": " + ex.GetType().Name + " " + ex.Message); }
        }
        TestAssert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }
}
