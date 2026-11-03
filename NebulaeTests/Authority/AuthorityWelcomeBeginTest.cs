
using NebulaeModel.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// every client that joined a real room ended with an active identity but a
/// null <c>WorldReplica</c> — no subscriptions, no baselines, pristine pools. The welcome
/// processor adopts the epoch on the shared identity before the runtime builds, and the
/// duplicate-begin guard mistook "adopted" for "built" and returned without building anything.
/// </summary>
[TestClass]
public class AuthorityWelcomeBeginTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA220062200000001, 0xA220062200000002);
    private static readonly AuthorityEpoch NextEpoch = new(0xA220062200000003, 0xA220062200000004);

    private static AuthoritySession NewClientSessionAdopting(AuthorityEpoch epoch)
    {
        // The welcome-processor order: negotiate, adopt the epoch on the shared identity,
        // then build the runtime for it.
        var identity = new AuthoritySessionState();
        identity.ConfirmHandshake();
        TestAssert.IsTrue(identity.TryAdoptWorldEpoch(epoch));
        return new AuthoritySession(identity);
    }

    [TestMethod]
    public void AdoptThenBeginBuildsTheClientReplica()
    {
        using var session = NewClientSessionAdopting(Epoch);
        session.BeginAuthorityWorld(Epoch, isHost: false);
        TestAssert.IsNotNull(session.WorldReplica,
            "An adopted-but-unbuilt world must still build its replica.");
        TestAssert.IsTrue(session.Identity.IsActive);
    }

    [TestMethod]
    public void ASecondBeginForTheSameEpochIsANoOp()
    {
        using var session = NewClientSessionAdopting(Epoch);
        session.BeginAuthorityWorld(Epoch, isHost: false);
        var replica = session.WorldReplica;
        TestAssert.IsNotNull(replica);
        session.BeginAuthorityWorld(Epoch, isHost: false);
        TestAssert.IsTrue(ReferenceEquals(replica, session.WorldReplica),
            "A duplicate begin must not rebuild the client world.");
    }

    [TestMethod]
    public void ANewEpochStillReplacesTheClientWorld()
    {
        using var session = NewClientSessionAdopting(Epoch);
        session.BeginAuthorityWorld(Epoch, isHost: false);
        var replica = session.WorldReplica;
        session.BeginAuthorityWorld(NextEpoch, isHost: false);
        TestAssert.IsFalse(ReferenceEquals(replica, session.WorldReplica));
        TestAssert.AreEqual(NextEpoch, session.WorldReplica.Epoch);
    }
}
