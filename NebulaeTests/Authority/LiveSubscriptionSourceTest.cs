using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class LiveSubscriptionSourceTest
{
    private sealed class EmptyWorld : IHostWorldView
    {
        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members) => true;
        public bool TryReadState(ObjectKey key, out byte[]? state) { state = null; return false; }
    }

    private sealed class Sink : IReplicationSink
    {
        public void Send(ushort subscriberId, AuthorityEnvelopePacket packet) { }
    }

    [TestMethod]
    public void AStarSubscriptionWaitsForTheMatchingAcceptedLocation()
    {
        using var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(new AuthorityEpoch(5, 7), isHost: true);
        session.RegisterHostReplication(new EmptyWorld(), new Sink());
        session.SubscriptionPlanetProvider = _ => 0;
        var acceptedStar = 1;
        session.HostReplicator.SubscriberStarProvider = _ => acceptedStar;
        session.HostReplicator.MemberInterestFor = _ => default;
        var scope = new ScopeKey(PoolKind.SpaceEnemy, 0);
        void Subscribe() => session.TryEnqueueScopeControl(2, ScopeControlOp.Subscribe, scope,
            ScopeRecoveryReason.None, false, 0, 0, observedStarId: 2, requestId: 1);
        Subscribe(); session.OnFrameBoundary(1);
        TestAssert.AreEqual(0L, session.HostReplicator.SnapshotsSent);
        TestAssert.AreEqual(1L, session.ScopeControlRefused);
        acceptedStar = 2;
        Subscribe(); session.OnFrameBoundary(2);
        TestAssert.AreEqual(1L, session.HostReplicator.SnapshotsSent);
        TestAssert.AreEqual(1L, session.ScopeControlHandled);
    }

    [TestMethod]
    public void AcceptedMovementAndDisconnectImmediatelyChangeSubscriptionEligibility()
    {
        using var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(new AuthorityEpoch(5, 7), isHost: true);
        TestAssert.IsTrue(session.RegisterHostReplication(new EmptyWorld(), new Sink()));
        int? location = 101;
        session.SubscriptionPlanetProvider = id => location;
        void Subscribe(PoolKind kind, int scope) => TestAssert.IsTrue(session.TryEnqueueScopeControl(2,
            ScopeControlOp.Subscribe, new ScopeKey(kind, scope), ScopeRecoveryReason.None, false, 0, 0));
        Subscribe(PoolKind.GroundEnemy, 101);
        session.OnFrameBoundary(1);
        TestAssert.AreEqual(1L, session.ScopeControlHandled);
        location = 102;
        Subscribe(PoolKind.GroundEnemy, 101);
        Subscribe(PoolKind.GroundEnemy, 102);
        session.OnFrameBoundary(2);
        TestAssert.AreEqual(2L, session.ScopeControlHandled);
        TestAssert.AreEqual(1L, session.ScopeControlRefused);
        location = 0; // Accepted space travel clears the previous planet while staying online.
        Subscribe(PoolKind.GroundEnemy, 102);
        Subscribe(PoolKind.SpaceEnemy, 0);
        session.OnFrameBoundary(3);
        TestAssert.AreEqual(3L, session.ScopeControlHandled);
        TestAssert.AreEqual(2L, session.ScopeControlRefused);
        location = null;
        Subscribe(PoolKind.SpaceEnemy, 0);
        session.OnFrameBoundary(4);
        TestAssert.AreEqual(3L, session.ScopeControlRefused);
    }
}
