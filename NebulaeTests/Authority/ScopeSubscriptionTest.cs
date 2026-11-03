
using NebulaeModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// which scopes a subscriber may hold and how deep, and what a broken scope's recovery is.
/// </summary>
[TestClass]
public class ScopeSubscriptionTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0A0A0A0A0A0A0A0A, 0x0A0A0A0A0A0A0A0A);

    private static ObjectKey Key(PoolKind kind, int scope, int nativeId, long generation) =>
        ObjectKey.Create(Epoch, kind, scope, nativeId, generation);

    // --- ScopeSubscriptionPolicy.MaySubscribe: the host's registry facts. ---

    [TestMethod]
    public void TheHostServesAPlanetPoolOnlyForThePlanetItAcceptedThePlayerOn()
    {
        TestAssert.AreEqual(ScopeSubscriptionDecision.Allowed,
            ScopeSubscriptionPolicy.MaySubscribe(new ScopeKey(PoolKind.GroundEnemy, 101),
                subscriberOnline: true, subscriberPlanetId: 101));
        TestAssert.AreEqual(ScopeSubscriptionDecision.Refused,
            ScopeSubscriptionPolicy.MaySubscribe(new ScopeKey(PoolKind.GroundEnemy, 102),
                subscriberOnline: true, subscriberPlanetId: 101));
        TestAssert.AreEqual(ScopeSubscriptionDecision.Refused,
            ScopeSubscriptionPolicy.MaySubscribe(new ScopeKey(PoolKind.GroundEnemy, 101),
                subscriberOnline: false, subscriberPlanetId: 101));
        // Global-visibility pools have no per-player planet rule.
        TestAssert.AreEqual(ScopeSubscriptionDecision.Allowed,
            ScopeSubscriptionPolicy.MaySubscribe(new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector),
                subscriberOnline: true, subscriberPlanetId: 101));
    }

    // --- ScopeSubscriptionPolicy.DesiredScopes: the standing set. ---

    [TestMethod]
    public void TheStandingSetIsWhereTheSubscriberLives()
    {
        var desired = new List<ScopeKey>();
        ScopeSubscriptionPolicy.DesiredScopes(101, desired);
        TestAssert.Contains(new ScopeKey(PoolKind.Entity, 101), desired);
        TestAssert.Contains(new ScopeKey(PoolKind.GroundEnemy, 101), desired);
        TestAssert.Contains(new ScopeKey(PoolKind.DroneTask, 101), desired);
        TestAssert.Contains(new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector),
            desired, "Orbital combat is observable from a planet too.");

        ScopeSubscriptionPolicy.DesiredScopes(0, desired);
        TestAssert.Contains(new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector), desired);
        TestAssert.Contains(new ScopeKey(PoolKind.SpaceCraft, AuthorityScope.Sector), desired);
        TestAssert.IsEmpty(desired.FindAll(scope => scope.Kind == PoolKind.GroundEnemy),
            "A player in space holds no planet pool at full depth.");
    }

    // --- ScopeRecoveryPlanner: the break → recovery mapping. ---

    [TestMethod]
    public void AStreamGapRebuildsOnlyItsOwnScope()
    {
        var scope = new ScopeKey(PoolKind.GroundEnemy, 101);
        var plan = ScopeRecoveryPlanner.Plan(ScopeRecoveryReason.StreamGap, scope);

        TestAssert.HasCount(2, plan.Actions);
        TestAssert.AreEqual(ScopeRecoveryActionKind.SuspendInput, plan.Actions[0].Kind);
        TestAssert.AreEqual(ScopeRecoveryActionKind.RequestScopeBaseline, plan.Actions[1].Kind);
        TestAssert.AreEqual(scope, plan.Actions[1].Scope);
    }

    [TestMethod]
    public void AnUnknownCoreTopologyAsksForTheDependencyBaselineFirst()
    {
        var baseScope = new ScopeKey(PoolKind.Base, 101);
        var plan = ScopeRecoveryPlanner.Plan(ScopeRecoveryReason.UnknownCoreTopology, baseScope);

        TestAssert.HasCount(3, plan.Actions);
        TestAssert.AreEqual(ScopeRecoveryActionKind.RequestDependencyBaseline, plan.Actions[1].Kind);
        TestAssert.AreEqual(new ScopeKey(PoolKind.Entity, 101), plan.Actions[1].Scope,
            "A base is displayed through the factory's entity graph.");
        TestAssert.AreEqual(ScopeRecoveryActionKind.RequestScopeBaseline, plan.Actions[2].Kind);

        var hiveScope = new ScopeKey(PoolKind.Hive, 1000001);
        var hivePlan = ScopeRecoveryPlanner.Plan(ScopeRecoveryReason.UnknownCoreTopology, hiveScope);
        TestAssert.AreEqual(new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector), hivePlan.Actions[1].Scope,
            "A hive's topology lives with the sector's space-enemy pool.");
    }

    [TestMethod]
    public void EveryReasonEndsInTheBrokenScopesOwnBaselineAndNoneResurrectsByPatch()
    {
        foreach (ScopeRecoveryReason reason in Enum.GetValues(typeof(ScopeRecoveryReason)))
        {
            if (reason == ScopeRecoveryReason.None) continue;
            var scope = new ScopeKey(PoolKind.GroundEnemy, 101);
            var plan = ScopeRecoveryPlanner.Plan(reason, scope);
            TestAssert.IsNotEmpty(plan.Actions, "A named reason must plan something.");
            TestAssert.AreEqual(ScopeRecoveryActionKind.SuspendInput, plan.Actions[0].Kind);
            TestAssert.AreEqual(ScopeRecoveryActionKind.RequestScopeBaseline, plan.Actions[plan.Actions.Length - 1].Kind);
        }
        TestAssert.IsEmpty(ScopeRecoveryPlanner.Plan(ScopeRecoveryReason.None,
            new ScopeKey(PoolKind.GroundEnemy, 101)).Actions);
    }

    // ScopeRecoveryPolicy: enemy state repair channel.

    // --- ScopeDigestComputer: the comparison itself. ---

    [TestMethod]
    public void TheDigestIsOrderIndependentAndDetectsEveryDifference()
    {
        var a = new List<ScopeDigestMember>
        {
            new(Key(PoolKind.GroundEnemy, 101, 1, 1), 3, [10, 20]),
            new(Key(PoolKind.GroundEnemy, 101, 2, 1), 1, null)
        };
        var b = new List<ScopeDigestMember>
        {
            new(Key(PoolKind.GroundEnemy, 101, 2, 1), 1, null),
            new(Key(PoolKind.GroundEnemy, 101, 1, 1), 3, [10, 20])
        };
        TestAssert.AreEqual(ScopeDigestComputer.Compute(a), ScopeDigestComputer.Compute(b),
            "Dictionary enumeration order must not change the digest.");

        TestAssert.AreNotEqual(ScopeDigestComputer.Compute(a), ScopeDigestComputer.Compute(new List<ScopeDigestMember>
        {
            new(Key(PoolKind.GroundEnemy, 101, 1, 1), 3, [10, 21])
        }), "A changed state byte is a divergence.");

        TestAssert.AreNotEqual(ScopeDigestComputer.Compute(a), ScopeDigestComputer.Compute(new List<ScopeDigestMember>
        {
            new(Key(PoolKind.GroundEnemy, 101, 1, 1), 4, [10, 20])
        }), "A changed revision is a divergence.");

        TestAssert.AreNotEqual(ScopeDigestComputer.Compute(a), ScopeDigestComputer.Compute(new List<ScopeDigestMember>
        {
            new(Key(PoolKind.GroundEnemy, 101, 1, 1), 3, [10, 20]),
            new(Key(PoolKind.GroundEnemy, 101, 2, 1), 1, [1])
        }), "Identity-without-state and identity-with-state are different facts.");

        TestAssert.AreNotEqual(ScopeDigestComputer.Compute(a), ScopeDigestComputer.Compute(new List<ScopeDigestMember>
        {
            new(Key(PoolKind.GroundEnemy, 101, 1, 1), 3, [10, 20])
        }), "A missing member is a divergence — this is what a lost spawn looks like.");

        TestAssert.AreNotEqual(ScopeDigestComputer.Compute(a), ScopeDigestComputer.Compute(new List<ScopeDigestMember>
        {
            new(Key(PoolKind.GroundEnemy, 101, 2, 1), 3, [10, 20]),
            new(Key(PoolKind.GroundEnemy, 101, 1, 1), 1, null)
        }), "A recycled slot is a different key, not the same member.");
    }
}
