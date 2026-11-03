
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// subscription control, canonical digests and partial recovery. A broken scope is suspended
/// (Resyncing), a resync request re-baselines it with a new subscription epoch, and a digest is
/// only ever compared at the exact stream position it declares. A digest-only observation records
/// the summary and receives no stream.
/// </summary>
[TestClass]
public class ScopeResyncTest
{
    private static readonly AuthorityEpoch Epoch = new(0x1414141414141414, 0x1414141414141414);
    private static readonly ScopeKey Scope = new(PoolKind.GroundEnemy, 101);
    private static readonly ScopeKey OtherScope = new(PoolKind.GroundEnemy, 102);

    private static ObjectKey Key(int nativeId, long generation) =>
        ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, nativeId, generation);

    private sealed class FakeWorld : IHostWorldView
    {
        public readonly Dictionary<ObjectKey, byte[]> States = [];
        public readonly List<ObjectKey> Members = [];

        public void Add(int nativeId, long generation, byte[] state)
        {
            var key = Key(nativeId, generation);
            Members.Add(key);
            if (state != null) States[key] = state;
        }

        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
        {
            if (!scope.Equals(Scope)) return true;
            members.AddRange(Members);
            return true;
        }

        public bool TryReadState(ObjectKey key, out byte[] state) => States.TryGetValue(key, out state!);
    }

    private sealed class RecordingSink : IReplicationSink
    {
        public readonly List<AuthorityEnvelopePacket> Sent = [];

        public void Send(ushort subscriberId, AuthorityEnvelopePacket packet) => Sent.Add(packet);

        public List<T> Of<T>() where T : AuthorityEnvelopePacket
        {
            var found = new List<T>();
            foreach (var packet in Sent)
            {
                if (packet is T typed) found.Add(typed);
            }
            return found;
        }
    }

    private sealed class RecordingScopeControlSink : IScopeControlSink
    {
        public readonly List<(ScopeControlOp Op, ScopeKey Scope, ScopeRecoveryReason Reason)> Sent = [];

        public void Send(ScopeControlOp op, ScopeKey scope, ScopeRecoveryReason reason, bool digestOnly,
            long subscriptionEpoch, long lastAppliedSequence) => Sent.Add((op, scope, reason));
    }

    private static readonly AuthoritySessionContext SessionContext =
        new(true, Epoch, default, 0, isHost: false);

    /// <summary>Applies one host packet to the replica the way the frame-boundary applier does.</summary>
    private static void ApplyToReplica(ClientWorldReplica replica, AuthorityEnvelopePacket packet)
    {
        switch (packet)
        {
            case AuthoritySnapshotBeginPacket begin:
                TestAssert.IsTrue(begin.TryGetScopeKey(out var beginScope));
                TestAssert.IsTrue(replica.BeginSnapshot(beginScope, begin.SubscriptionEpoch, begin.BaselineId,
                    begin.ChunkCount, begin.TotalBytes, begin.SnapshotHash));
                break;
            case AuthoritySnapshotChunkPacket chunk:
                TestAssert.IsTrue(chunk.TryGetScopeKey(out var chunkScope));
                TestAssert.IsTrue(replica.ReceiveSnapshotChunk(chunkScope, chunk.SubscriptionEpoch,
                    chunk.BaselineId, chunk.ChunkIndex, chunk.Data));
                break;
            case AuthoritySnapshotCommitPacket commit:
                TestAssert.IsTrue(commit.TryGetScopeKey(out var commitScope));
                TestAssert.IsTrue(replica.CommitSnapshot(commitScope, commit.SubscriptionEpoch,
                    commit.BaselineId, commit.ChunkCount, commit.SnapshotHash));
                break;
            case AuthorityLifecyclePacket lifecycle:
                TestAssert.IsTrue(lifecycle.TryGetScopeKey(out var lifecycleScope));
                TestAssert.IsTrue(AuthorityLifecycleCodec.TryDecode(lifecycle.Data, 0, lifecycle.Data.Length,
                    lifecycle.RecordCount, SessionContext, out var lifecycleRecords, out _));
                TestAssert.IsTrue(replica.ApplyLifecycle(lifecycleScope, lifecycle.SubscriptionEpoch,
                    lifecycle.Sequence, lifecycleRecords));
                break;
            case AuthorityWorldStatePacket worldState:
                TestAssert.IsTrue(worldState.TryGetScopeKey(out var stateScope));
                TestAssert.IsTrue(AuthorityWorldStateCodec.TryDecode(worldState.Data, 0, worldState.Data.Length,
                    worldState.RecordCount, SessionContext, out var stateRecords, out _));
                TestAssert.IsTrue(replica.ApplyWorldState(stateScope, worldState.SubscriptionEpoch,
                    worldState.Sequence, worldState.DeclaredBaselineId, stateRecords));
                break;
            case AuthorityScopeDigestPacket digest:
                TestAssert.IsTrue(digest.TryGetScopeKey(out var digestScope));
                TestAssert.IsTrue(replica.ReceiveScopeDigest(digestScope, digest.SubscriptionEpoch,
                    digest.HostTick, digest.DeclaredStreamSequence, digest.BaselineId, digest.MemberCount,
                    digest.Digest));
                break;
        }
    }

    private static void DeliverToReplica(RecordingSink sink, ClientWorldReplica replica)
    {
        foreach (var packet in sink.Sent) ApplyToReplica(replica, packet);
        sink.Sent.Clear();
    }

    /// <summary>Runs one full subscription conversation and leaves the scope Live.</summary>
    private static void SubscribeAndGoLive(RecordingSink sink, ClientWorldReplica replica,
        ushort subscriberId, HostWorldReplicator host)
    {
        replica.Subscribe(Scope);
        host.Subscribe(subscriberId, Scope);
        DeliverToReplica(sink, replica);
        var baselineId = replica.Versions.TryGetScope(Scope, out var state) ? state.BaselineId : 0;
        // The client acks by calling back the host, exactly as the ack processor would.
        host.OnSnapshotAck(subscriberId, baselineId, lastAppliedSequence: 0, accepted: true);
        DeliverToReplica(sink, replica); // backlog + resume marker move the scope to Live
    }

    [TestMethod]
    public void ADigestMatchesWhenBothSidesHoldTheSameScope()
    {
        var world = new FakeWorld();
        world.Add(1, 1, [80]);
        world.Add(2, 1, [60]);
        var sink = new RecordingSink();
        var replica = new ClientWorldReplica(Epoch);
        var host = new HostWorldReplicator(Epoch, world, sink);

        SubscribeAndGoLive(sink, replica, 1, host);
        TestAssert.AreEqual(SubscriptionPhase.Live, replica.Versions.TryGetScope(Scope, out var state)
            ? state.Phase : SubscriptionPhase.Unsubscribed);

        host.CaptureFrame(1000); // a quiet frame: no member changed
        host.PublishDigests(1000, intervalTicks: 300);
        DeliverToReplica(sink, replica);

        TestAssert.AreEqual(1L, replica.DigestsMatchedTotal);
        TestAssert.AreEqual(0L, replica.DigestsMismatchedTotal);
        TestAssert.IsFalse(replica.ScopesNeedingResync.Contains(Scope));
    }

    private sealed class RuntimeVerifier : IReplicaRuntimeVerifier
    {
        public bool Broken;
        public int Reads;
        public RuntimeObjectIntegrity VerifyObject(ObjectKey key, byte[] state)
        { Reads++; return Broken ? RuntimeObjectIntegrity.Drift : RuntimeObjectIntegrity.Matches; }
    }

    [TestMethod]
    public void RuntimeAuditsAreBoundedAndRequestRecoveryEvenWhenTheReceivedDigestMatches()
    {
        var world = new FakeWorld();
        for (var id = 1; id <= 100; id++) world.Add(id, 1, [80]);
        var sink = new RecordingSink();
        var replica = new ClientWorldReplica(Epoch);
        var host = new HostWorldReplicator(Epoch, world, sink);
        var control = new RecordingScopeControlSink(); replica.ScopeControlSink = control;
        SubscribeAndGoLive(sink, replica, 1, host);
        host.PublishDigests(1000, 300); DeliverToReplica(sink, replica);
        TestAssert.AreEqual(1L, replica.DigestsMatchedTotal);
        var verifier = new RuntimeVerifier();
        TestAssert.AreEqual(32, replica.AuditRuntimeObjects(1000, verifier));
        TestAssert.AreEqual(32, verifier.Reads);
        verifier.Broken = true;
        replica.AuditRuntimeObjects(1001, verifier);
        TestAssert.AreEqual(SubscriptionPhase.Resyncing, replica.Versions.TryGetScope(Scope, out var version) ? version.Phase : default);
        TestAssert.AreEqual(1, control.Sent.Count(x => x.Op == ScopeControlOp.Resync && x.Reason == ScopeRecoveryReason.RuntimeDrift));
        var reads = verifier.Reads;
        replica.AuditRuntimeObjects(1002, verifier);
        TestAssert.AreEqual(reads, verifier.Reads, "Suspended scopes are not repeatedly verified or repaired.");
    }

    [TestMethod]
    public void TheAuditRotationDropsObjectsThatLeftSoDriftIsStillFoundAtFullRate()
    {
        // The audit key list is rebuilt only every 300 ticks, so an object that despawned in between
        // stays in the rotation. Looking it up can only fail, and doing that on every pass would
        // audit fewer live objects per round for the rest of the interval: a sector that lost most of
        // its enemies would take proportionally longer to notice drift in the survivors.
        var world = new FakeWorld();
        for (var id = 1; id <= 100; id++) world.Add(id, 1, [80]);
        var sink = new RecordingSink();
        var replica = new ClientWorldReplica(Epoch);
        var host = new HostWorldReplicator(Epoch, world, sink);
        SubscribeAndGoLive(sink, replica, 1, host);

        var verifier = new RuntimeVerifier();
        TestAssert.AreEqual(32, replica.AuditRuntimeObjects(1000, verifier), "The first round rebuilds the rotation.");

        // Two thirds of the sector dies. The mirrors shrink only once the despawns are applied, and
        // the rotation still holds the dead keys because the rebuild interval has not elapsed.
        for (var id = 1; id <= 68; id++) world.Members.Remove(Key(id, 1));
        host.CaptureFrame(1001);
        DeliverToReplica(sink, replica);
        TestAssert.HasCount(32, replica.Mirrors, "The despawns shrank the mirror set.");

        // Walk the rotation far enough to cover the dead keys: once they are dropped, every round
        // settles on the 32 survivors instead of decaying with the corpses.
        var audited = 0;
        for (var tick = 1002; tick < 1014; tick++) audited = replica.AuditRuntimeObjects(tick, verifier);
        TestAssert.AreEqual(32, audited, "A dead key must not keep spending the round's budget.");
    }

    [TestMethod]
    public void ADigestMismatchSuspendsTheScopeAndARequestRebaselinesIt()
    {
        var world = new FakeWorld();
        world.Add(1, 1, [80]);
        var sink = new RecordingSink();
        var replica = new ClientWorldReplica(Epoch);
        var host = new HostWorldReplicator(Epoch, world, sink);
        var control = new RecordingScopeControlSink();
        replica.ScopeControlSink = control;

        SubscribeAndGoLive(sink, replica, 1, host);
        var epochBefore = replica.Versions.TryGetScope(Scope, out var before) ? before.SubscriptionEpoch : 0;

        // The host publishes a state fact; the copy the replica receives is corrupted (the same
        // stream position, different bytes — a stand-in for any writer the replica missed).
        var key = Key(1, 1);
        world.States[key] = [70];
        host.CaptureFrame(1000);
        var genuineStates = sink.Of<AuthorityWorldStatePacket>();
        var statePacket = genuineStates[genuineStates.Count - 1];
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryEncode(
            new List<AuthorityWorldStateRecord> { new(key, 2, new byte[] { 66 }) }, out var corruptedBlob));
        statePacket.Data = corruptedBlob;
        DeliverToReplica(sink, replica);
        host.PublishDigests(1000, intervalTicks: 300);
        DeliverToReplica(sink, replica);

        TestAssert.AreEqual(1L, replica.DigestsMismatchedTotal);
        TestAssert.AreEqual(SubscriptionPhase.Resyncing, replica.Versions.TryGetScope(Scope, out var state)
            ? state.Phase : SubscriptionPhase.Unsubscribed);
        TestAssert.Contains(Scope, replica.ScopesNeedingResync);
        TestAssert.Contains((ScopeControlOp.Resync, Scope, ScopeRecoveryReason.DigestMismatch), control.Sent);

        // The host answers the resync with a fresh baseline; the replica installs it and lives on.
        TestAssert.IsTrue(host.RequestResync(1, Scope));
        DeliverToReplica(sink, replica);
        var newBaseline = replica.Versions.TryGetScope(Scope, out var rebased) ? rebased.BaselineId : 0;
        host.OnSnapshotAck(1, newBaseline, 0, accepted: true);
        DeliverToReplica(sink, replica);
        TestAssert.AreEqual(SubscriptionPhase.Live, replica.Versions.TryGetScope(Scope, out var recovered)
            ? recovered.Phase : SubscriptionPhase.Unsubscribed);
        TestAssert.IsFalse(replica.ScopesNeedingResync.Contains(Scope));

        // The new subscription epoch is a different one: the old stream's tail is refusable by it.
        var epochAfter = replica.Versions.TryGetScope(Scope, out var after) ? after.SubscriptionEpoch : 0;
        TestAssert.AreNotEqual(epochBefore, epochAfter);
    }

    [TestMethod]
    public void AStreamGapSuspendsItsScopeAndNothingElse()
    {
        var world = new FakeWorld();
        world.Add(1, 1, [80]);
        var sink = new RecordingSink();
        var replica = new ClientWorldReplica(Epoch);
        var host = new HostWorldReplicator(Epoch, world, sink);
        var control = new RecordingScopeControlSink();
        replica.ScopeControlSink = control;

        SubscribeAndGoLive(sink, replica, 1, host);
        replica.Subscribe(OtherScope);
        host.Subscribe(1, OtherScope);
        DeliverToReplica(sink, replica);
        host.OnSnapshotAck(1, replica.Versions.TryGetScope(OtherScope, out var otherState) ? otherState.BaselineId : 0,
            0, accepted: true);
        DeliverToReplica(sink, replica);

        // A packet for sequence 3 arrives while the replica applied only sequence 1 (the resume
        // marker): the gap at 2 is real, the scope suspends, and the resync request names it.
        var key = Key(1, 1);
        TestAssert.IsFalse(replica.ApplyWorldState(Scope, 1, 3, 1,
            new List<AuthorityWorldStateRecord> { new(key, 9, [50]) }));
        TestAssert.AreEqual(SubscriptionPhase.Resyncing, replica.Versions.TryGetScope(Scope, out var state)
            ? state.Phase : SubscriptionPhase.Unsubscribed);
        TestAssert.Contains((ScopeControlOp.Resync, Scope, ScopeRecoveryReason.StreamGap), control.Sent);

        // The next packet of the suspended scope applies nothing while it waits for its baseline.
        TestAssert.IsFalse(replica.ApplyWorldState(Scope, 1, 4, 1,
            new List<AuthorityWorldStateRecord> { new(key, 10, [40]) }));
        TestAssert.AreEqual(1L, replica.StreamSuspendedTotal);

        // The other scope is untouched — that is the "correct recovery scope" split.
        TestAssert.IsFalse(replica.ScopesNeedingResync.Contains(OtherScope));
        TestAssert.AreEqual(SubscriptionPhase.Live, replica.Versions.TryGetScope(OtherScope, out var other)
            ? other.Phase : SubscriptionPhase.Unsubscribed);
    }

    [TestMethod]
    public void ADigestAheadOfTheStreamIsDeferredUntilThePositionIsApplied()
    {
        var world = new FakeWorld();
        world.Add(1, 1, [80]);
        var sink = new RecordingSink();
        var replica = new ClientWorldReplica(Epoch);
        var host = new HostWorldReplicator(Epoch, world, sink);
        SubscribeAndGoLive(sink, replica, 1, host);

        // The host digests a state change; the digest packet reaches the client before the state
        // packet it describes. It must wait for the position, not fail against the older one.
        world.States[Key(1, 1)] = [70];
        host.CaptureFrame(1001);
        host.PublishDigests(1001, intervalTicks: 1);
        var digests = sink.Of<AuthorityScopeDigestPacket>();
        var statePacket = sink.Of<AuthorityWorldStatePacket>()[sink.Of<AuthorityWorldStatePacket>().Count - 1];
        var digest = digests[digests.Count - 1];
        sink.Sent.Clear();

        TestAssert.IsTrue(replica.ReceiveScopeDigest(Scope, digest.SubscriptionEpoch, digest.HostTick,
            digest.DeclaredStreamSequence, digest.BaselineId, digest.MemberCount, digest.Digest));
        TestAssert.AreEqual(0L, replica.DigestsMatchedTotal);
        TestAssert.AreEqual(0L, replica.DigestsMismatchedTotal);

        ApplyToReplica(replica, statePacket); // the stream reaches the digest's position
        TestAssert.AreEqual(1L, replica.DigestsMatchedTotal);
        TestAssert.AreEqual(0L, replica.DigestsMismatchedTotal);
        TestAssert.IsFalse(replica.ScopesNeedingResync.Contains(Scope));
    }

    [TestMethod]
    public void ADigestBehindTheStreamIsSupersededNotAMismatch()
    {
        var world = new FakeWorld();
        world.Add(1, 1, [80]);
        var sink = new RecordingSink();
        var replica = new ClientWorldReplica(Epoch);
        var host = new HostWorldReplicator(Epoch, world, sink);
        SubscribeAndGoLive(sink, replica, 1, host);

        // Applied sequence 1 (the resume marker) already; a digest of sequence 1 delivered after
        // sequence 3 is old news.
        var key = Key(1, 1);
        TestAssert.IsTrue(replica.ApplyWorldState(Scope, 1, 2, 1,
            new List<AuthorityWorldStateRecord> { new(key, 5, [79]) }));
        TestAssert.IsTrue(replica.ApplyWorldState(Scope, 1, 3, 1,
            new List<AuthorityWorldStateRecord> { new(key, 6, [78]) }));
        TestAssert.IsFalse(replica.ReceiveScopeDigest(Scope, 1, 0, declaredStreamSequence: 1, baselineId: 1,
            memberCount: 1, digest: 0xDEAD),
            "A superseded digest is dropped, not applied and not a resync trigger.");
        TestAssert.AreEqual(1L, replica.DigestsSupersededTotal);
        TestAssert.AreEqual(0L, replica.DigestsMismatchedTotal);
        TestAssert.IsFalse(replica.ScopesNeedingResync.Contains(Scope));
    }

    [TestMethod]
    public void ADigestOnlyObservationRecordsSummariesAndReceivesNoStream()
    {
        var world = new FakeWorld();
        world.Add(1, 1, [80]);
        world.Add(2, 1, [60]);
        var sink = new RecordingSink();
        var replica = new ClientWorldReplica(Epoch);
        var host = new HostWorldReplicator(Epoch, world, sink);

        replica.Subscribe(Scope, digestOnly: true);
        host.Subscribe(1, Scope, digestOnly: true);
        DeliverToReplica(sink, replica);
        TestAssert.IsEmpty(sink.Of<AuthoritySnapshotBeginPacket>(),
            "An observation holds no baseline and must not receive one.");

        host.CaptureFrame(1000);
        DeliverToReplica(sink, replica);
        TestAssert.IsEmpty(sink.Of<AuthorityLifecyclePacket>(), "An observer is not on the stream.");
        TestAssert.IsEmpty(sink.Of<AuthorityWorldStatePacket>(), "An observer is not on the stream.");

        host.PublishDigests(1000, intervalTicks: 300);
        DeliverToReplica(sink, replica);
        TestAssert.AreEqual(1L, replica.DigestSummariesRecordedTotal);
        TestAssert.IsTrue(replica.DigestSummaries.ContainsKey(Scope));
        TestAssert.AreEqual(2, replica.DigestSummaries[Scope].MemberCount);
        TestAssert.AreEqual(0L, replica.DigestsMatchedTotal,
            "An observation has no mirrors; its digest is recorded, never compared.");

        // A stream packet for a baseline-less observation is a protocol violation, not a fact.
        var key = Key(1, 1);
        TestAssert.IsFalse(replica.ApplyWorldState(Scope, 0, 1, 0,
            new List<AuthorityWorldStateRecord> { new(key, 2, [50]) }));
    }

    [TestMethod]
    public void AnIdleObserverDoesNotPinTheLogReclamationOfStreamSubscribers()
    {
        var world = new FakeWorld();
        world.Add(1, 1, [80]);
        var sink = new RecordingSink();
        var replica = new ClientWorldReplica(Epoch);
        var host = new HostWorldReplicator(Epoch, world, sink);

        // One idle digest-only observer and one live stream subscriber share the scope.
        host.Subscribe(1, Scope, digestOnly: true);
        SubscribeAndGoLive(sink, replica, 2, host);

        // A state event is appended, delivered to the stream subscriber, and then reclaimed: the
        // observer holds no log position, so it must not keep the event alive.
        world.States[Key(1, 1)] = [70];
        host.CaptureFrame(1001);
        TestAssert.AreEqual(1L, host.EventsTrimmed);
    }

    [TestMethod]
    public void AResyncedSubscriptionRestartsItsStreamAtTheNewBaseline()
    {
        var world = new FakeWorld();
        world.Add(1, 1, [80]);
        var sink = new RecordingSink();
        var replica = new ClientWorldReplica(Epoch);
        var host = new HostWorldReplicator(Epoch, world, sink);
        SubscribeAndGoLive(sink, replica, 1, host);
        var epochBefore = replica.Versions.TryGetScope(Scope, out var s0) ? s0.SubscriptionEpoch : 0;

        // The client reports the break; the host re-baselines with a fresh subscription epoch and
        // the replica's stream position restarts at the new baseline.
        TestAssert.IsTrue(replica.RequestResync(Scope, ScopeRecoveryReason.SequenceEvicted));
        TestAssert.AreEqual(1L, replica.ResyncsRequestedTotal);
        TestAssert.IsTrue(host.RequestResync(1, Scope));
        DeliverToReplica(sink, replica);
        var newBaseline = replica.Versions.TryGetScope(Scope, out var rebased) ? rebased.BaselineId : 0;
        host.OnSnapshotAck(1, newBaseline, 0, accepted: true);
        DeliverToReplica(sink, replica);

        TestAssert.AreEqual(SubscriptionPhase.Live, replica.Versions.TryGetScope(Scope, out var s1)
            ? s1.Phase : SubscriptionPhase.Unsubscribed);
        TestAssert.AreNotEqual(epochBefore, s1.SubscriptionEpoch);

        // The resumed stream counts from one again; the resume marker took sequence 1, so the
        // next absolute state applies at sequence 2.
        var key = Key(1, 1);
        TestAssert.IsTrue(replica.ApplyWorldState(Scope, s1.SubscriptionEpoch, 2, s1.BaselineId,
            new List<AuthorityWorldStateRecord> { new(key, 50, [70]) }));
        TestAssert.AreEqual(50L, replica.Mirrors[key].Revision);
    }

    [TestMethod]
    public void ARepeatedBreakSendsOneRequestInsteadOfSpammingTheHost()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var replica = new ClientWorldReplica(Epoch);
        var host = new HostWorldReplicator(Epoch, world, sink);
        var control = new RecordingScopeControlSink();
        replica.ScopeControlSink = control;
        SubscribeAndGoLive(sink, replica, 1, host);

        var key = Key(1, 1);
        TestAssert.IsFalse(replica.ApplyWorldState(Scope, 1, 5, 1,
            new List<AuthorityWorldStateRecord> { new(key, 1, [1]) }));
        TestAssert.IsFalse(replica.ApplyWorldState(Scope, 1, 9, 1,
            new List<AuthorityWorldStateRecord> { new(key, 1, [1]) }));
        TestAssert.AreEqual(1L, replica.ResyncsRequestedTotal,
            "The pending request covers the scope until a baseline installs.");
        TestAssert.HasCount(1, control.Sent.FindAll(request => request.Reason == ScopeRecoveryReason.StreamGap));
    }

    // --- Session wiring: the request queues for the frame boundary; the policy is enforced. ---

    private static AuthoritySession NewHostSession()
    {
        var identity = new AuthoritySessionState();
        identity.ConfirmHandshake();
        var session = new AuthoritySession(identity);
        session.BeginAuthorityWorld(Epoch, isHost: true);
        return session;
    }

    [TestMethod]
    public void AScopeControlRequestIsDrainedAtTheFrameBoundaryUnderThePolicy()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var session = NewHostSession();
        TestAssert.IsTrue(session.RegisterHostReplication(world, sink));
        session.SubscriptionPlanetProvider = id => id == 1 ? 101 : (int?)null;

        // A player may subscribe the planet the host accepted it on.
        TestAssert.IsTrue(session.TryEnqueueScopeControl(1, ScopeControlOp.Subscribe, Scope,
            ScopeRecoveryReason.None, digestOnly: false, subscriptionEpoch: 0, lastAppliedSequence: 0));
        // ... and not another planet: the request is refused by accepted player location.
        TestAssert.IsTrue(session.TryEnqueueScopeControl(1, ScopeControlOp.Subscribe, OtherScope,
            ScopeRecoveryReason.None, digestOnly: false, subscriptionEpoch: 0, lastAppliedSequence: 0));

        session.OnFrameBoundary(100);

        TestAssert.AreEqual(1, session.ScopeControlHandled);
        TestAssert.AreEqual(1, session.ScopeControlRefused);
        TestAssert.HasCount(1, sink.Of<AuthoritySnapshotBeginPacket>(),
            "The allowed subscription received its baseline conversation.");
        TestAssert.AreEqual(1, session.HostReplicator.ScopeCount,
            "The refused request never created a replication state.");

        // Unsubscribe stops the stream; the host keeps simulating the scope regardless.
        TestAssert.IsTrue(session.TryEnqueueScopeControl(1, ScopeControlOp.Unsubscribe, Scope,
            ScopeRecoveryReason.None, digestOnly: false, subscriptionEpoch: 0, lastAppliedSequence: 0));
        session.OnFrameBoundary(101);
        TestAssert.AreEqual(2, session.ScopeControlHandled);
        session.OnFrameComplete(101);
    }

    [TestMethod]
    public void AScopeControlRequestWithoutAHostWorldIsDroppedNotForgotten()
    {
        var session = NewHostSession();
        // No replication registered: the replicator is null and the request cannot run.

        TestAssert.IsTrue(session.TryEnqueueScopeControl(1, ScopeControlOp.Subscribe, Scope,
            ScopeRecoveryReason.None, digestOnly: false, subscriptionEpoch: 0, lastAppliedSequence: 0));
        session.OnFrameBoundary(100);
        TestAssert.AreEqual(1, session.ScopeControlDropped);
    }

    [TestMethod]
    public void ADigestTravelsThroughTheClientInboxAndAnObservationRecordsIt()
    {
        var identity = new AuthoritySessionState();
        identity.ConfirmHandshake();
        var session = new AuthoritySession(identity);
        session.BeginAuthorityWorld(Epoch, isHost: false);
        var control = new RecordingScopeControlSink();
        session.ScopeControlSink = control;

        // The session-level subscribe moves the replica phase and tells the host in one call.
        TestAssert.IsTrue(session.TrySubscribeScope(Scope, digestOnly: true));
        TestAssert.Contains((ScopeControlOp.Subscribe, Scope, ScopeRecoveryReason.None), control.Sent);
        TestAssert.AreEqual(SubscriptionPhase.Live, session.WorldReplica.Versions.TryGetScope(Scope, out var state)
            ? state.Phase : SubscriptionPhase.Unsubscribed);

        // The digest is queued like every stream-adjacent message and applied at the frame boundary.
        var digest = AuthorityScopeDigestPacket.Create(
            new AuthorityEnvelopeHeader(AuthorityFamily.ScopeDigest, Epoch,
                connection: default, sequence: 0, hostTick: 100, claimedPlayerId: 0, payloadLength: 0),
            Scope, subscriptionEpoch: 0, baselineId: 0, declaredStreamSequence: 0, memberCount: 3, digest: 42);
        TestAssert.IsTrue(session.TryEnqueueReplicaMessage(digest,
            new ApplyScope(Scope, transactionId: 0, streamSequence: 0, hostTick: 100)));
        session.OnFrameBoundary(101);
        TestAssert.AreEqual(1L, session.WorldReplica.DigestSummariesRecordedTotal);
        TestAssert.AreEqual(3, session.WorldReplica.DigestSummaries[Scope].MemberCount);

        // Unsubscribe leaves and tells the host in the same call.
        TestAssert.IsTrue(session.TryUnsubscribeScope(Scope));
        TestAssert.Contains((ScopeControlOp.Unsubscribe, Scope, ScopeRecoveryReason.None), control.Sent);
        TestAssert.AreEqual(SubscriptionPhase.Unsubscribed, session.WorldReplica.Versions.TryGetScope(Scope, out var left)
            ? left.Phase : SubscriptionPhase.Live);
    }
    [TestMethod]
    public void DigestOnlySummariesAreValidWithoutAStreamEpoch()
    {
        var summary = new AuthorityScopeDigestPacket { MemberCount = 4 };
        TestAssert.IsTrue(summary.HasValidBookkeeping());
        summary.SubscriptionEpoch = 1;
        TestAssert.IsFalse(summary.HasValidBookkeeping());
        summary.BaselineId = 2;
        TestAssert.IsTrue(summary.HasValidBookkeeping());
        summary.DeclaredStreamSequence = -1;
        TestAssert.IsFalse(summary.HasValidBookkeeping());
        summary.DeclaredStreamSequence = 1;
        summary.MemberCount = -1;
        TestAssert.IsFalse(summary.HasValidBookkeeping());
    }
}
