using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class AuthorityRuleGuardTest
{
    [TestMethod]
    [DataRow(false, false, false, true)]
    [DataRow(true, true, false, true)]
    [DataRow(true, false, false, false)]
    [DataRow(true, false, true, true)]
    public void SharedCommitsRequireTheHostOrAnExplicitApplyWindow(bool active, bool host, bool applying, bool allowed)
    {
        var previous = AuthorityRuleGuard.Probe;
        try
        {
            AuthorityRuleGuard.Probe = () => new AuthorityGuardSession(active, host, applying);
            TestAssert.AreEqual(allowed, AuthorityRuleGuard.AllowHostRule());
        }
        finally { AuthorityRuleGuard.Probe = previous; }
    }
}

[TestClass]
public class AuthorityRequiredHookTest
{
    [TestMethod]
    public void TheFrameBoundaryIsARequiredHook()
    {
        // Without these hooks the command drain would run without a safe frame boundary.
        var labels = AuthorityRuleGuard.RequiredPolicy.Select(policy => policy.Label).ToArray();
        TestAssert.Contains(AuthorityHookLabels.ThreadManagerProcessFrame, labels);
        TestAssert.Contains(AuthorityHookLabels.GameLogicLogicFrame, labels);
    }

    [TestMethod]
    public void EveryRequiredHookResolvesAgainstTheInstalledAssemblies()
    {
        // This is the runtime check multiplayer performs before a world starts; running it here means a
        // game update that renames a method fails the build instead of failing a live room.
        var failures = AuthorityRuleGuard.VerifyRequiredHooks();
        TestAssert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void AMissingRequiredHookIsReported()
    {
        // Negative control: the verification must actually be able to fail. A resolver that finds
        // nothing stands in for a game update that renamed the method.
        var failures = AuthorityRuleGuard.VerifyRequiredHooks(_ => null);
        TestAssert.HasCount(AuthorityRuleGuard.RequiredPolicy.Count, failures);
        TestAssert.IsTrue(failures.All(failure => failure.Contains("not found")),
            "A missing hook must be reported as such:\n" + string.Join("\n", failures));
    }
}

/// <summary>
/// starting multiplayer is refused when a required hook or transformation is missing.
/// </summary>
[TestClass]
public class AuthorityGuardLoadGateTest
{
    [TestInitialize]
    public void BeforeEach()
    {

        AuthorityRuleGuard.ResetVerification();
        AuthorityRuleGuard.ClearVerifiers();
    }

    [TestCleanup]
    public void AfterEach() => BeforeEach();

    [TestMethod]
    public void RequiredHooksAreVerifiedOnceAndTheResultIsRemembered()
    {
        TestAssert.IsTrue(AuthorityRuleGuard.VerifyLoadOnce(),
            "With the installed assemblies present multiplayer must be available.");
        TestAssert.IsTrue(AuthorityRuleGuard.IsLoadVerified);
        TestAssert.IsNull(AuthorityRuleGuard.LoadFailure);
    }

    [TestMethod]
    public void AFailedVerifierPreventsStartupAndReportsWhy()
    {
        AuthorityRuleGuard.RegisterVerifier(() => ["transpiler X never ran"]);
        TestAssert.IsFalse(AuthorityRuleGuard.VerifyLoadOnce());
        TestAssert.IsFalse(AuthorityRuleGuard.IsLoadVerified);
        TestAssert.Contains("transpiler X never ran", AuthorityRuleGuard.LoadFailure);
    }

    [TestMethod]
    public void AVerifierThatThrowsIsAFailureRatherThanAnException()
    {
        // A verifier must not be able to take the whole startup path down with an unhandled throw.
        AuthorityRuleGuard.RegisterVerifier(() => throw new InvalidOperationException("verifier broke"));
        TestAssert.IsFalse(AuthorityRuleGuard.VerifyLoadOnce());
        TestAssert.Contains("verifier broke", AuthorityRuleGuard.LoadFailure);
    }
}

/// <summary>
/// a required method transformation must actually match the installed game assembly.
/// </summary>
[TestClass]
public class AuthorityTranspilerGuardTest
{
    private static readonly BindingFlags Statics = BindingFlags.Static | BindingFlags.Public |
                                                   BindingFlags.NonPublic;

    [TestInitialize]
    public void BeforeEach() => NebulaePatcher.Patches.Authority.AuthorityTranspilerGuard.Reset();

    [TestCleanup]
    public void AfterEach() => BeforeEach();

    [TestMethod]
    public void TheDamageEntryTransformationsMatchTheInstalledGameAssembly()
    {
        // Invoke the real transpiler for every method it targets, so the counts under test are the
        // counts the game will see. The single transpiler method is registered against both ground
        // damage entries, and Harmony calls it once per target.
        var skillSystem = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType(
            "NebulaePatcher.Patches.Transpilers.SkillSystem_Transpiler", true)!;

        ApplyAndAssertCount(skillSystem, "DamageObject_Transpiler", typeof(SkillSystem),
            nameof(SkillSystem.DamageObject), "SkillSystem.DamageObject.playerId");
        ApplyAndAssertCount(skillSystem, "DamageGroundObject_Transpiler", typeof(SkillSystem),
            nameof(SkillSystem.DamageGroundObjectByLocalCaster),
            "SkillSystem.DamageGroundObjectByLocalCaster.playerId");
        ApplyAndAssertCount(skillSystem, "DamageGroundObject_Transpiler", typeof(SkillSystem),
            nameof(SkillSystem.DamageGroundObjectByRemoteCaster),
            "SkillSystem.DamageGroundObjectByRemoteCaster.playerId");
    }

    [TestMethod]
    public void EveryRequiredTransformationIsSatisfiedAfterTheyAllRan()
    {
        // The load gate's real input: run every transformation against the installed assembly and
        // require an empty failure list. This is what stops multiplayer from loading on a game update
        // that moved one of the patterns.
        var assembly = typeof(NebulaePatcher.NebulaePlugin).Assembly;
        var skillSystem = assembly.GetType("NebulaePatcher.Patches.Transpilers.SkillSystem_Transpiler", true)!;
        var module = assembly.GetType(
            "NebulaePatcher.Patches.Transpilers.ConstructionModuleComponent_Transpiler", true)!;

        RunTranspiler(skillSystem, "DamageObject_Transpiler", typeof(SkillSystem),
            nameof(SkillSystem.DamageObject));
        RunTranspiler(skillSystem, "DamageGroundObject_Transpiler", typeof(SkillSystem),
            nameof(SkillSystem.DamageGroundObjectByLocalCaster));
        RunTranspiler(skillSystem, "DamageGroundObject_Transpiler", typeof(SkillSystem),
            nameof(SkillSystem.DamageGroundObjectByRemoteCaster));
        RunTranspiler(module, "PlaceItems_Transpiler", typeof(ConstructionModuleComponent),
            nameof(ConstructionModuleComponent.PlaceItems));

        var failures = NebulaePatcher.Patches.Authority.AuthorityTranspilerGuard.VerifyRequired();
        TestAssert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void ThePrebuildItemReportTransformationMatchesTheInstalledGameAssembly()
    {
        var transpiler = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType(
            "NebulaePatcher.Patches.Transpilers.ConstructionModuleComponent_Transpiler", true)!;
        ApplyAndAssertCount(transpiler, "PlaceItems_Transpiler", typeof(ConstructionModuleComponent),
            nameof(ConstructionModuleComponent.PlaceItems),
            "ConstructionModuleComponent.PlaceItems.itemRequired");
    }

    [TestMethod]
    public void EveryRequiredTransformationIsReportedWhenNoneApplied()
    {
        // Negative control: with no transpiler having run, every required transformation must be
        // reported. Without this, the load gate could pass because it checked nothing.
        var failures = NebulaePatcher.Patches.Authority.AuthorityTranspilerGuard.VerifyRequired();
        TestAssert.HasCount(NebulaePatcher.Patches.Authority.AuthorityTranspilerGuard.Required.Count,
            failures);
        TestAssert.IsTrue(failures.All(failure => failure.Contains("never ran")),
            string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void AnUnexpectedMatchCountIsAFailure()
    {
        // The transformation relies on the pattern occurring exactly once. A second occurrence means
        // the game changed shape and the rewrite may now be touching the wrong branch, so it must be
        // treated as a failure rather than as a bonus match.
        var codes = new List<CodeInstruction> { new(OpCodes.Ldc_I4_1), new(OpCodes.Ldc_I4_1) };
        var label = "test.unexpected";
        TestAssert.IsFalse(NebulaePatcher.Patches.Authority.AuthorityTranspilerGuard.VerifyCount(
            label, codes, 1, exact: true, new CodeMatch(OpCodes.Ldc_I4_1)));
        TestAssert.AreEqual(2,
            NebulaePatcher.Patches.Authority.AuthorityTranspilerGuard.ObservedMatches(label));
    }

    private static void ApplyAndAssertCount(Type transpilerType, string methodName, Type targetType,
        string targetMethodName, string label)
    {
        RunTranspiler(transpilerType, methodName, targetType, targetMethodName);

        var observed = NebulaePatcher.Patches.Authority.AuthorityTranspilerGuard.ObservedMatches(label);
        TestAssert.IsGreaterThanOrEqualTo(1,
            observed, $"The transpiler {label} recorded {observed} matches; the transformation did not apply to " +
            "the installed game assembly.");
    }

    /// <summary>
    /// Runs one transpiler over the real method body and asserts it produced a rewritten body.
    /// </summary>
    private static void RunTranspiler(Type transpilerType, string methodName, Type targetType,
        string targetMethodName)
    {
        var transpiler = transpilerType.GetMethod(methodName, Statics);
        TestAssert.IsNotNull(transpiler, transpilerType.Name + "." + methodName + " not found");

        var target = AccessTools.Method(targetType, targetMethodName);
        TestAssert.IsNotNull(target, "Missing " + targetType.Name + "." + targetMethodName);
        var body = target.GetMethodBody()?.GetILAsByteArray();
        TestAssert.IsGreaterThan(5, body?.Length ?? 0,
            targetType.Name + "." + targetMethodName + " is a reference stub; run with the real game assembly");

        var instructions = PatchProcessor.GetOriginalInstructions(target, null);
        TestAssert.IsNotEmpty(instructions,
            "No instructions read for " + targetType.Name + "." + targetMethodName);

        object[] arguments = transpiler.GetParameters().Length switch
        {
            1 => [instructions],
            2 => [instructions, target],
            _ => [instructions, target, new System.Reflection.Emit.DynamicMethod("damage", typeof(void), Type.EmptyTypes).GetILGenerator()]
        };

        var result = ((System.Collections.IEnumerable)transpiler.Invoke(null, arguments)!)
            .Cast<CodeInstruction>().ToList();
        TestAssert.IsNotEmpty(result, "The transpiler produced no instructions.");
    }
}
