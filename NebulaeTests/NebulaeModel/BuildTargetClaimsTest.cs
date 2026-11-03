using global::NebulaeModel.DataStructures;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class BuildTargetClaimsTest
{
    [TestMethod]
    public void ReassignmentAndIdReuseRejectOldMessages()
    {
        var host = new BuildTargetClaims();
        var first = host.Assign(101, 7, BuildOwnerKind.Player, 2);
        TestAssert.IsTrue(host.Remove(101, 7, first.Generation));
        var second = host.Assign(101, 7, BuildOwnerKind.Base, 31);
        TestAssert.IsGreaterThan(first.Generation, second.Generation);

        var client = new BuildTargetClaims();
        TestAssert.IsTrue(client.Apply(second));
        TestAssert.IsFalse(client.Apply(first));
        TestAssert.IsFalse(client.Remove(101, 7, first.Generation));
        TestAssert.IsTrue(client.TryGet(101, 7, out var current));
        TestAssert.AreEqual(31, current.OwnerId);
    }

    [TestMethod]
    public void LaunchedClaimCannotGoBackToPendingOrLaunchTwice()
    {
        var claims = new BuildTargetClaims();
        var claim = claims.Assign(101, 9, BuildOwnerKind.Player, 3);
        TestAssert.IsTrue(claims.MarkLaunched(101, 9, claim.Generation, BuildOwnerKind.Player, 3));
        TestAssert.IsFalse(claims.MarkLaunched(101, 9, claim.Generation, BuildOwnerKind.Player, 3));
        TestAssert.IsFalse(claims.Apply(claim));
        TestAssert.IsTrue(claims.TryGet(101, 9, out var current));
        TestAssert.IsTrue(current.Launched);
    }

    [TestMethod]
    public void RemovedGenerationDoesNotReappearFromDelayedGrant()
    {
        var claims = new BuildTargetClaims();
        var claim = new BuildTargetClaim(101, 11, 4, BuildOwnerKind.Base, 20);
        TestAssert.IsTrue(claims.Apply(claim));
        TestAssert.IsTrue(claims.Remove(101, 11, 4));
        TestAssert.IsFalse(claims.Apply(claim));
        TestAssert.IsFalse(claims.TryGet(101, 11, out _));
    }

    [TestMethod]
    public void VanillaBaseWeightAndRangeArePreserved()
    {
        var playerScore = BuildCandidateScore.Calculate(BuildOwnerKind.Player, 100f, 20f);
        var baseScore = BuildCandidateScore.Calculate(BuildOwnerKind.Base, 110f, 20f);
        TestAssert.IsGreaterThan(playerScore, baseScore);
        TestAssert.AreEqual(0f, BuildCandidateScore.Calculate(BuildOwnerKind.Player, 401f, 20f));
    }

    [TestMethod]
    public void FourTargetsRetainTheirOwnersAndGenerationsInFactorySnapshot()
    {
        var source = new List<BuildTargetClaim>();
        for (var id = 1; id <= 4; id++)
            source.Add(new BuildTargetClaim(101, id, id + 10, BuildOwnerKind.Player, 2, id == 1));
        source.Add(new BuildTargetClaim(102, 7, 20, BuildOwnerKind.Base, 32));
        var snapshot = BuildTargetClaimSnapshot.Export(source, 101);
        var restored = BuildTargetClaimSnapshot.Import(snapshot, 101, 10);
        TestAssert.HasCount(4, restored);
        for (var i = 0; i < restored.Count; i++)
        {
            TestAssert.AreEqual(i + 1, restored[i].PrebuildId);
            TestAssert.AreEqual(i + 11L, restored[i].Generation);
            TestAssert.AreEqual(2, restored[i].OwnerId);
        }
        TestAssert.IsTrue(restored[0].Launched);
        TestAssert.IsFalse(restored[1].Launched);
    }

    [TestMethod]
    public void TruncatedOrDuplicateFactorySnapshotIsRejectedBeforeImport()
    {
        var claim = new BuildTargetClaim(101, 3, 1, BuildOwnerKind.Base, 42);
        var encoded = BuildTargetClaimSnapshot.Export(new[] { claim, claim }, 101);
        TestAssert.ThrowsExactly<InvalidDataException>(() => BuildTargetClaimSnapshot.Import(encoded, 101, 10));
        System.Array.Resize(ref encoded, 10);
        TestAssert.ThrowsExactly<EndOfStreamException>(() => BuildTargetClaimSnapshot.Import(encoded, 101, 10));
    }

    [TestMethod]
    public void LaunchValidationAcceptsOneToFourOwnedTargetsAndRejectsStaleOrDuplicateTargets()
    {
        var claims = new BuildTargetClaims();
        var generations = new long[4];
        for (var id = 1; id <= 4; id++)
            generations[id - 1] = claims.Assign(101, id, BuildOwnerKind.Player, 2).Generation;
        TestAssert.IsTrue(claims.ValidateLaunch(101, new[] { -1, 0, 0, 0 },
            new[] { generations[0], 0L, 0L, 0L }, BuildOwnerKind.Player, 2, true));
        TestAssert.IsTrue(claims.ValidateLaunch(101, new[] { -1, -2, -3, -4 },
            generations, BuildOwnerKind.Player, 2, true));
        TestAssert.IsFalse(claims.ValidateLaunch(101, new[] { -1, -1, 0, 0 },
            new[] { generations[0], generations[0], 0L, 0L }, BuildOwnerKind.Player, 2, true));
        TestAssert.IsFalse(claims.ValidateLaunch(101, new[] { -1, 0, 0, 0 },
            new[] { generations[0] + 1, 0L, 0L, 0L }, BuildOwnerKind.Player, 2, true));
        TestAssert.IsFalse(claims.ValidateLaunch(101, new[] { -1, 0, 0, 0 },
            new[] { generations[0], 0L, 0L, 0L }, BuildOwnerKind.Player, 3, true));
        claims.MarkLaunched(101, 1, generations[0], BuildOwnerKind.Player, 2);
        TestAssert.IsFalse(claims.ValidateLaunch(101, new[] { -1, 0, 0, 0 },
            new[] { generations[0], 0L, 0L, 0L }, BuildOwnerKind.Player, 2, true));
        TestAssert.IsTrue(claims.ValidateLaunch(101, new[] { -1, 0, 0, 0 },
            new[] { generations[0], 0L, 0L, 0L }, BuildOwnerKind.Player, 2, false));
    }
}
