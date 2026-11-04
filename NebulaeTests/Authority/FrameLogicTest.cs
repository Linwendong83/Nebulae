using System.Collections;
using System.Reflection;
using HarmonyLib;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class FrameLogicTest
{
    private static readonly HashSet<object> ready = new();

    private static bool Ready(object __0, ref bool __result)
    {
        __result = ready.Contains(__0);
        return false;
    }

    [TestMethod]
    public void TurretLookupPreservesEnemyIdentityScopeAndWildcardReadiness()
    {
        var turrets = new NativeDarkFogTurrets();
        var entries = (IDictionary)AccessTools.Field(typeof(NativeDarkFogTurrets), "entries").GetValue(turrets);
        object Add(int astro, int enemy)
        {
            var type = typeof(NativeDarkFogTurrets).GetNestedType("Entry", BindingFlags.NonPublic)!;
            var entry = Activator.CreateInstance(type, true)!;
            AccessTools.Field(type, "Astro").SetValue(entry, astro);
            AccessTools.Field(type, "Enemy").SetValue(entry, enemy);
            entries.Add((astro, enemy), entry);
            return entry;
        }

        var first = Add(101, 3);
        var second = Add(101, 4);
        var other = Add(102, 3);
        var harmony = new Harmony("nebulae.tests.frame-turret-lookup");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(NativeDarkFogTurrets), "Ready"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(FrameLogicTest), nameof(Ready))));
            ready.Add(second); ready.Add(other);
            TestAssert.IsFalse(turrets.CanRun(101, 3));
            TestAssert.IsTrue(turrets.CanRun(101, 4));
            TestAssert.IsTrue(turrets.CanRun(102, 3));
            TestAssert.IsFalse(turrets.CanRun(102, 4));
            TestAssert.IsFalse(turrets.CanRun(101, 999));
            TestAssert.IsTrue(turrets.CanRun(101), "A later ready turret must satisfy the wildcard lookup.");
            ready.Clear();
            TestAssert.IsFalse(turrets.CanRun(101));
            ready.Add(first);
            TestAssert.IsTrue(turrets.CanRun(101, 3), "Readiness must be checked again when it changes.");
            turrets.Dispose();
            TestAssert.IsFalse(turrets.CanRun(101, 3));
            TestAssert.IsFalse(turrets.CanRun(101));
        }
        finally { harmony.UnpatchSelf(); ready.Clear(); }
    }
}
