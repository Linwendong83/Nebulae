using System.Reflection;
using HarmonyLib;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

// Dedicated shield backends work independently of Unity's graphics device.
[TestClass]
public class HeadlessShieldGuardTest
{
    private static Type Patches => typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType(
        "NebulaePatcher.Patches.Misc.Dedicated_Server_Patches", true)!;

    private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [TestMethod]
    [DataRow("ComputeShaderKernel_Prefix")]
    [DataRow("ComputeShaderDispatch_Prefix")]
    [DataRow("ComputeBuffer_Prefix")]
    public void UnityComputeRemainsDisabled(string name)
    {
        var prefix = Patches.GetMethod(name, Statics)!;
        TestAssert.IsTrue(prefix.GetCustomAttributes(typeof(HarmonyPrefix), false).Any());
        TestAssert.IsFalse((bool)prefix.Invoke(null, null)!,
            "Dedicated servers must skip Unity compute even during shield recalculation.");
    }

    [TestMethod]
    public void RelayLandingOverrideIsRemoved()
    {
        // Relay landing must use the original per-planet raycast and shield coverage values.
        TestAssert.IsNull(Patches.GetMethod("StopLanding", Statics),
            "StopLanding must stay removed so TestRelayCondition keeps its original behaviour.");
    }

    [TestMethod]
    public void ShieldFailureHandlerRemainsAFinalizerWithoutScopeState()
    {
        var finalizer = Patches.GetMethod("RecalculatePhysicsShape_Finalizer", Statics);
        TestAssert.IsNotNull(finalizer);
        TestAssert.IsTrue(finalizer!.GetCustomAttributes(typeof(HarmonyFinalizer), false).Any(),
            "Backend failures must be handled even when RecalculatePhysicsShape throws.");
        CollectionAssert.AreEqual(new[] { "__instance", "__exception" },
            finalizer.GetParameters().Select(parameter => parameter.Name).ToArray());
    }
}
