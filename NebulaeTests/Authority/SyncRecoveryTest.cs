using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class SyncRecoveryTest
{
    private static readonly AuthorityEpoch Epoch = new(11, 22);
    private static readonly ScopeKey Scope = new(PoolKind.GroundEnemy, 102);

    private sealed class World : IHostWorldView
    {
        public readonly ObjectKey Key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 102, 1, 1);
        public byte[] Value = [1];
        public bool Exists = true;
        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
        {
            if (Exists && scope.Equals(Scope)) members.Add(Key);
            return true;
        }
        public bool TryReadState(ObjectKey key, out byte[] state) { state = Value; return true; }
    }

    private sealed class Sink : IReplicationSink, ISnapshotAckSink, IScopeControlSink
    {
        public readonly List<AuthorityEnvelopePacket> Packets = [];
        public readonly List<AuthoritySnapshotAckPacket> Acks = [];
        public readonly List<(ScopeControlOp Op, ScopeKey Scope)> Requests = [];
        public void Send(ushort player, AuthorityEnvelopePacket packet) => Packets.Add(packet);
        public void Send(AuthoritySnapshotAckPacket packet) => Acks.Add(packet);
        public void Send(ScopeControlOp op, ScopeKey scope, ScopeRecoveryReason reason, bool digestOnly,
            long subscriptionEpoch, long lastAppliedSequence) => Requests.Add((op, scope));
    }

    private sealed class Rig : IDisposable
    {
        public readonly World World = new();
        public readonly Sink Sink = new();
        public readonly HostWorldReplicator Host;
        public readonly AuthoritySession Client = new(new AuthoritySessionState());
        public double Now;
        public ClientWorldReplica Replica => Client.WorldReplica;
        public Rig(int capacity = 4096)
        {
            Client.Identity.ConfirmHandshake();
            Client.BeginAuthorityWorld(Epoch, isHost: false);
            Client.RecoveryTime = () => Now;
            Client.ScopeControlSink = Sink;
            Client.SnapshotAckSink = Sink;
            Replica.Subscribe(Scope);
            Host = new HostWorldReplicator(Epoch, World, Sink, logCapacity: capacity);
        }
        public void Deliver(Func<AuthorityEnvelopePacket, bool>? keep = null)
        {
            foreach (var packet in Sink.Packets)
                if (keep == null || keep(packet))
                    TestAssert.IsTrue(Client.TryEnqueueReplicaMessage(packet,
                        new ApplyScope(Scope, hostTick: packet.HostTick)));
            Sink.Packets.Clear();
            Client.OnFrameBoundary(1000);
        }
        public void Ack()
        {
            foreach (var ack in Sink.Acks)
                Host.OnSnapshotAck(1, ack.BaselineId, ack.LastAppliedSequence, ack.Accepted);
            Sink.Acks.Clear();
        }
        public void Recover()
        {
            foreach (var request in Sink.Requests)
                if (request.Op == ScopeControlOp.Subscribe) Host.Subscribe(1, request.Scope);
                else if (request.Op == ScopeControlOp.Resync && !Host.RequestResync(1, request.Scope))
                    Host.Subscribe(1, request.Scope); // The session permits this for the player's current planet.
            Sink.Requests.Clear();
            Deliver(); Ack(); Deliver();
            TestAssert.IsTrue(Host.HasLiveSubscription(1, Scope));
            TestAssert.IsTrue(Replica.Versions.TryGetScope(Scope, out var state));
            TestAssert.AreEqual(SubscriptionPhase.Live, state.Phase);
        }
        public void GoLive() { Host.Subscribe(1, Scope); Deliver(); Ack(); Deliver(); }
        public void Dispose() => Client.Dispose();
    }

    [TestMethod]
    public void MissingCommitRecoversByElapsedTimeWithoutAdvancingTheClientTick()
    {
        using var rig = new Rig();
        rig.Host.Subscribe(1, Scope);
        rig.Deliver(packet => packet.Family != AuthorityFamily.SnapshotCommit);
        TestAssert.IsTrue(rig.Replica.Snapshots.IsStaging(Scope));
        rig.Now = ClientWorldReplica.RecoveryTimeoutSeconds - 1;
        rig.Client.OnFrameBoundary(1000);
        TestAssert.IsEmpty(rig.Sink.Requests);
        rig.Now++;
        rig.Client.OnFrameBoundary(1000);
        TestAssert.HasCount(1, rig.Sink.Requests);
        TestAssert.IsFalse(rig.Replica.Snapshots.IsStaging(Scope));
        rig.Recover();
        TestAssert.IsTrue(rig.Replica.TryFindKey(PoolKind.GroundEnemy, 102, 1, out _));
    }

    [TestMethod]
    public void MissingInitialSubscriptionCanRecoverWithAnEligibleNewHostCursor()
    {
        using var rig = new Rig();
        rig.Client.OnFrameBoundary(1000);
        rig.Now = ClientWorldReplica.RecoveryTimeoutSeconds;
        rig.Client.OnFrameBoundary(1000);
        TestAssert.AreEqual(ScopeControlOp.Resync, rig.Sink.Requests.Single().Op);
        rig.Recover();
    }

    [TestMethod]
    public void LosingTheEntireInitialSnapshotDoesNotLeaveTheHostWaitingForItsAck()
    {
        using var rig = new Rig();
        rig.Host.Subscribe(1, Scope);
        rig.Sink.Packets.Clear();
        rig.Client.OnFrameBoundary(1000);
        rig.Now = ClientWorldReplica.RecoveryTimeoutSeconds;
        rig.Client.OnFrameBoundary(1000);
        rig.Recover();
    }

    [TestMethod]
    public void MissingResumeMarkerRecoversFromCatchingUp()
    {
        using var rig = new Rig();
        rig.Host.Subscribe(1, Scope); rig.Deliver(); rig.Ack();
        rig.Sink.Packets.Clear();
        rig.Now = ClientWorldReplica.RecoveryTimeoutSeconds;
        rig.Client.OnFrameBoundary(1000);
        TestAssert.HasCount(1, rig.Sink.Requests);
        rig.Recover();
    }

    [TestMethod]
    public void MissingTailDetectedByDigestRecoversEvenWithoutAnotherStreamPacket()
    {
        using var rig = new Rig(); rig.GoLive();
        rig.World.Exists = false; rig.Host.CaptureFrame(1);
        rig.Sink.Packets.Clear(); // Lose the final despawn.
        rig.Host.PublishDigests(1, 1); rig.Deliver();
        rig.Now = ClientWorldReplica.RecoveryTimeoutSeconds;
        rig.Client.OnFrameBoundary(1000);
        rig.Recover();
        TestAssert.IsFalse(rig.Replica.TryFindKey(PoolKind.GroundEnemy, 102, 1, out _));
    }

    [TestMethod]
    public void ChunkProgressExtendsTimeoutButDuplicateChunksDoNot()
    {
        var sink = new Sink();
        var replica = new ClientWorldReplica(Epoch) { ScopeControlSink = sink };
        replica.Subscribe(Scope);
        TestAssert.IsTrue(replica.BeginSnapshot(Scope, 1, 1, 2, AuthorityLimits.ChunkMaxBytes * 2L, 1));
        replica.ServiceRecovery(0);
        var chunk = new byte[AuthorityLimits.ChunkMaxBytes];
        TestAssert.IsTrue(replica.ReceiveSnapshotChunk(Scope, 1, 1, 0, chunk));
        replica.ServiceRecovery(14);
        TestAssert.IsTrue(replica.ReceiveSnapshotChunk(Scope, 1, 1, 0, chunk));
        replica.ServiceRecovery(28);
        TestAssert.IsEmpty(sink.Requests);
        replica.ServiceRecovery(29);
        TestAssert.HasCount(1, sink.Requests);
    }

    [TestMethod]
    public void WaitingForALocalFactoryDoesNotDiscardACompleteSnapshot()
    {
        using var rig = new Rig();
        var observer = new NotReadyObserver();
        rig.Replica.MirrorObserver = observer;
        rig.Host.Subscribe(1, Scope); rig.Deliver();
        rig.Now = 1000; rig.Client.OnFrameBoundary(1000);
        TestAssert.IsEmpty(rig.Sink.Requests);
        TestAssert.IsEmpty(rig.Sink.Acks);
        observer.Ready = true;
        rig.Client.OnFrameBoundary(1000); rig.Ack(); rig.Deliver();
        TestAssert.IsTrue(rig.Host.HasLiveSubscription(1, Scope));
    }

    private sealed class NotReadyObserver : IReplicaMirrorObserver, IReplicaBaselineReadiness
    {
        public bool Ready;
        public bool IsReadyForBaseline(ScopeKey scope) => Ready;
        public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state) { }
        public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members) { }
        public void OnMemberRemoved(ScopeKey scope, in ObjectKey key) { }
    }

    [TestMethod]
    public void StartingAMissingRecoveryCursorStillChecksTheRegisteredPlanet()
    {
        using var host = new AuthoritySession(new AuthoritySessionState());
        host.BeginAuthorityWorld(Epoch, isHost: true);
        var sink = new Sink();
        host.RegisterHostReplication(new World(), sink);
        host.SubscriptionPlanetProvider = player => player == 1 ? 102 : 0;
        host.TryEnqueueScopeControl(2, ScopeControlOp.Resync, Scope, ScopeRecoveryReason.BaselineRefused, false, 0, 0);
        host.OnFrameBoundary(0);
        TestAssert.IsEmpty(sink.Packets);
        TestAssert.AreEqual(1L, host.ScopeControlRefused);
        host.TryEnqueueScopeControl(1, ScopeControlOp.Resync, Scope, ScopeRecoveryReason.BaselineRefused, false, 0, 0);
        host.OnFrameBoundary(0);
        TestAssert.IsTrue(sink.Packets.Any(p => p.Family == AuthorityFamily.SnapshotBegin));
    }

    [TestMethod]
    public void LostRecoveryRequestIsRetriedButNotEveryFrame()
    {
        using var rig = new Rig();
        rig.GoLive();
        rig.Replica.RequestResync(Scope, ScopeRecoveryReason.StreamGap);
        rig.Sink.Requests.Clear(); // Lose the request.
        rig.Client.OnFrameBoundary(1000);
        for (var i = 1; i < ClientWorldReplica.RecoveryTimeoutSeconds; i++)
        { rig.Now = i; rig.Client.OnFrameBoundary(1000); }
        TestAssert.IsEmpty(rig.Sink.Requests);
        rig.Now = ClientWorldReplica.RecoveryTimeoutSeconds;
        rig.Client.OnFrameBoundary(1000);
        TestAssert.HasCount(1, rig.Sink.Requests);
        rig.Recover();
    }

    [TestMethod]
    public void EvictedTailReplacesTheBaselineAndRemovesTheGhostBeforeGoingLive()
    {
        using var rig = new Rig(capacity: 2);
        rig.Host.Subscribe(1, Scope);
        rig.Deliver(); // Delay ACK while the world advances.
        for (var tick = 1; tick <= 8; tick++)
        { rig.World.Value = [(byte)(tick + 1)]; rig.Host.CaptureFrame(tick); }
        rig.World.Exists = false;
        rig.Host.CaptureFrame(9);
        rig.Ack();
        TestAssert.IsFalse(rig.Host.HasLiveSubscription(1, Scope));
        TestAssert.IsTrue(rig.Sink.Packets.Any(p => p.Family == AuthorityFamily.SnapshotBegin));
        TestAssert.IsFalse(rig.Sink.Packets.Any(p => p.Family == AuthorityFamily.WorldState),
            "An evicted tail cannot be followed by a completion marker.");
        rig.Deliver();
        TestAssert.IsFalse(rig.Replica.TryFindKey(PoolKind.GroundEnemy, 102, 1, out _));
        rig.Ack(); rig.Deliver();
        TestAssert.IsTrue(rig.Host.HasLiveSubscription(1, Scope));
        TestAssert.IsTrue(rig.Replica.Versions.TryGetScope(Scope, out var state));
        TestAssert.AreEqual(SubscriptionPhase.Live, state.Phase);
    }

    [TestMethod]
    public void ALiveSubscriberAlsoRecoversWhenOneFrameExceedsTheRetainedHistory()
    {
        using var rig = new Rig(capacity: 1);
        rig.GoLive();
        // A new member produces a spawn and a state, overflowing a one-event history.
        rig.World.Exists = false; rig.Host.CaptureFrame(1); rig.Deliver();
        rig.World.Exists = true; rig.World.Value = [2]; rig.Host.CaptureFrame(2);
        TestAssert.IsTrue(rig.Sink.Packets.Any(p => p.Family == AuthorityFamily.SnapshotBegin));
        rig.Deliver(); rig.Ack(); rig.Deliver();
        TestAssert.IsTrue(rig.Host.HasLiveSubscription(1, Scope));
        TestAssert.IsTrue(rig.Replica.TryFindKey(PoolKind.GroundEnemy, 102, 1, out _));
    }

    [TestMethod]
    public void NewerSnapshotReplacesIncompleteStagingAndLateMessagesCannotDestroyIt()
    {
        using var rig = new Rig();
        rig.Host.Subscribe(1, Scope);
        var old = rig.Sink.Packets.ToArray();
        rig.Deliver(p => p.Family == AuthorityFamily.SnapshotBegin);
        rig.Host.RequestResync(1, Scope);
        var fresh = rig.Sink.Packets.ToArray();
        rig.Deliver(p => p.Family == AuthorityFamily.SnapshotBegin);
        var begin = (AuthoritySnapshotBeginPacket)fresh[0];
        foreach (var packet in old)
            rig.Client.TryEnqueueReplicaMessage(packet, new ApplyScope(Scope));
        rig.Client.OnFrameBoundary(1000);
        TestAssert.IsTrue(rig.Replica.Snapshots.IsStaging(Scope, begin.SubscriptionEpoch, begin.BaselineId));
        rig.Sink.Packets.AddRange(fresh.Skip(1));
        rig.Deliver(); rig.Ack(); rig.Deliver();
        TestAssert.IsTrue(rig.Host.HasLiveSubscription(1, Scope));
    }

    [TestMethod]
    public void InboxOverflowRecoversEvenWhenTheLostDeathIsTheLastEvent()
    {
        using var rig = new Rig();
        rig.GoLive();
        var scope = new ApplyScope(Scope);
        var future = new AuthorityWorldStatePacket { HostTick = 10000 };
        for (var i = 0; i < AuthorityLimits.CommandQueueMax; i++)
            TestAssert.IsTrue(rig.Client.TryEnqueueReplicaMessage(future, scope));
        rig.World.Exists = false;
        rig.Host.CaptureFrame(1);
        TestAssert.IsFalse(rig.Client.TryEnqueueReplicaMessage(rig.Sink.Packets.Single(), scope));
        rig.Sink.Packets.Clear();
        rig.Client.OnFrameBoundary(1000);
        TestAssert.AreEqual(0, rig.Client.InboundCount);
        TestAssert.HasCount(1, rig.Sink.Requests);
        rig.Recover();
        TestAssert.IsFalse(rig.Replica.TryFindKey(PoolKind.GroundEnemy, 102, 1, out _));
    }

    [TestMethod]
    public void AFutureHeadCannotHoldRecoveryMessagesForever()
    {
        using var rig = new Rig();
        rig.GoLive();
        rig.Client.TryEnqueueReplicaMessage(new AuthorityWorldStatePacket { HostTick = 10000 }, new ApplyScope(Scope));
        rig.Client.OnFrameBoundary(1000);
        rig.Now = ClientWorldReplica.RecoveryTimeoutSeconds;
        rig.Client.OnFrameBoundary(1000);
        TestAssert.AreEqual(0, rig.Client.InboundCount);
        TestAssert.HasCount(1, rig.Sink.Requests);
        rig.Recover();
    }

    [TestMethod]
    public void FuturePacketsFromARetiredSubscriptionDoNotBlockTheReplacement()
    {
        using var rig = new Rig(); rig.GoLive();
        TestAssert.IsTrue(rig.Replica.Versions.TryGetScope(Scope, out var state));
        rig.Client.TryEnqueueReplicaMessage(new AuthorityWorldStatePacket
        { HostTick = 10000, SubscriptionEpoch = state.SubscriptionEpoch }, new ApplyScope(Scope));
        rig.Replica.RequestResync(Scope, ScopeRecoveryReason.StreamGap);
        rig.Recover(); // Recovery must succeed now, without waiting for tick 10000 or timeout.
        TestAssert.AreEqual(0, rig.Client.InboundCount);
    }

    [TestMethod]
    public void UnsubscribingCancelsTimeoutAndRejectsLateBaselines()
    {
        using var rig = new Rig();
        rig.Host.Subscribe(1, Scope);
        rig.Replica.Unsubscribe(Scope);
        rig.Deliver(); rig.Now = 100; rig.Client.OnFrameBoundary(1000);
        TestAssert.IsEmpty(rig.Sink.Requests);
        TestAssert.IsEmpty(rig.Sink.Acks);
        TestAssert.IsFalse(rig.Replica.IsSubscribed(Scope));
    }

    [TestMethod]
    public void LateBaselineCannotTurnADigestOnlyObserverBackIntoAFullReplica()
    {
        using var rig = new Rig();
        rig.Host.Subscribe(1, Scope);
        rig.Replica.Subscribe(Scope, digestOnly: true);
        rig.Deliver(); rig.Now = 100; rig.Client.OnFrameBoundary(1000);
        TestAssert.IsEmpty(rig.Sink.Acks);
        TestAssert.IsEmpty(rig.Sink.Requests);
        TestAssert.IsEmpty(rig.Replica.Mirrors);
    }

    [TestMethod]
    public void AnOversizedMessageIsRejectedBeforeCloningAndRequestsRecovery()
    {
        using var rig = new Rig();
        rig.GoLive();
        TestAssert.IsFalse(rig.Client.TryEnqueueReplicaMessage(new TooLargePacket(), new ApplyScope(Scope)));
        rig.Client.OnFrameBoundary(1000);
        rig.Recover();
    }

    private sealed class TooLargePacket : AuthorityEnvelopePacket
    {
        public override AuthorityFamily Family => AuthorityFamily.WorldState;
        public override int DeclaredPayloadLength => (int)AuthorityLimits.ConnectionPendingMaxBytes;
        protected override void CloneMutableBuffers() => throw new InvalidOperationException("Refuse before allocating a copy.");
    }
}
