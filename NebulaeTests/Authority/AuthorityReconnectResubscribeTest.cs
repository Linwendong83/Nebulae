
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// a reconnecting client reuses its session player id but is a brand-new
/// process with an empty replica. The host must drop that seat's replication cursors on disconnect,
/// or the re-subscribe hits the "healthy active cursor" path, returns no baseline, and the client
/// sits in Snapshotting forever.
/// </summary>
[TestClass]
public class AuthorityReconnectResubscribeTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA220062200000010, 0xA220062200000011);
    private static readonly ScopeKey Scope = new(PoolKind.Entity, 102);

    private sealed class FakeWorld : IHostWorldView
    {
        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
        {
            members.Add(ObjectKey.Create(Epoch, PoolKind.Entity, 102, 1, 1));
            return true;
        }

        public bool TryReadState(ObjectKey key, out byte[] state)
        {
            state = [7];
            return true;
        }
    }

    private sealed class RecordingSink : IReplicationSink
    {
        public readonly List<AuthorityEnvelopePacket> Sent = [];

        public void Send(ushort subscriberId, AuthorityEnvelopePacket packet) => Sent.Add(packet);

        public int Begins() => Sent.FindAll(p => p is AuthoritySnapshotBeginPacket).Count;
    }

    private static AuthoritySession NewHost(FakeWorld world, RecordingSink sink)
    {
        var identity = new AuthoritySessionState();
        identity.ConfirmHandshake();
        var session = new AuthoritySession(identity);
        session.BeginAuthorityWorld(Epoch, isHost: true);
        TestAssert.IsTrue(session.RegisterHostReplication(world, sink));
        session.SubscriptionPlanetProvider = id => id == 2 ? 102 : (int?)null;
        return session;
    }

    [TestMethod]
    public void AReconnectResubscribeGetsAFreshBaseline()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        using var session = NewHost(world, sink);
        session.AssignConnectionEpoch(2);

        // First connection: subscribe, install the baseline, ack.
        session.TryEnqueueScopeControl(2, ScopeControlOp.Subscribe, Scope, ScopeRecoveryReason.None,
            digestOnly: false, subscriptionEpoch: 0, lastAppliedSequence: 0);
        session.OnFrameBoundary(100);
        var begin = FindBegin(sink);
        TestAssert.IsNotNull(begin);
        session.HostReplicator.OnSnapshotAck(2, begin.BaselineId, lastAppliedSequence: 1, accepted: true);

        // The client disconnects and reconnects as a new process. The old cursor must be gone.
        session.ForgetPlayerConnection(2);
        session.AssignConnectionEpoch(2);

        var beginsBefore = sink.Begins();
        session.TryEnqueueScopeControl(2, ScopeControlOp.Subscribe, Scope, ScopeRecoveryReason.None,
            digestOnly: false, subscriptionEpoch: 0, lastAppliedSequence: 0);
        session.OnFrameBoundary(200);

        TestAssert.AreEqual(beginsBefore + 1, sink.Begins(),
            "A reconnecting client must receive a fresh baseline, not resume the dead stream.");
    }

    [TestMethod]
    public void ADuplicateSubscribeWhileHealthyDoesNotRebaseline()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        using var session = NewHost(world, sink);
        session.AssignConnectionEpoch(2);

        session.TryEnqueueScopeControl(2, ScopeControlOp.Subscribe, Scope, ScopeRecoveryReason.None,
            digestOnly: false, subscriptionEpoch: 0, lastAppliedSequence: 0);
        session.OnFrameBoundary(100);
        var begin = FindBegin(sink);
        session.HostReplicator.OnSnapshotAck(2, begin.BaselineId, lastAppliedSequence: 1, accepted: true);

        var beginsBefore = sink.Begins();
        session.TryEnqueueScopeControl(2, ScopeControlOp.Subscribe, Scope, ScopeRecoveryReason.None,
            digestOnly: false, subscriptionEpoch: 0, lastAppliedSequence: 0);
        session.OnFrameBoundary(200);
        TestAssert.AreEqual(beginsBefore, sink.Begins(),
            "A repeat subscribe from the same healthy connection must not re-baseline.");
    }

    private static AuthoritySnapshotBeginPacket FindBegin(RecordingSink sink) =>
        (AuthoritySnapshotBeginPacket)sink.Sent.Find(p => p is AuthoritySnapshotBeginPacket);
}
