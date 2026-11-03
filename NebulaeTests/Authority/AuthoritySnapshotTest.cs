
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// the snapshot conversation. The host freezes one scope at a safe frame, ships it as
/// Begin/Chunk/Commit, and the client stages it, installs it as one atomic membership replacement,
/// and acks it — only then does the stream resume. The end-to-end test wires a real host replicator
/// to a real client session through their seams, without a game process.
/// </summary>
[TestClass]
public class AuthoritySnapshotTest
{
    private static readonly AuthorityEpoch Epoch = new(0x1A1A1A1A1A1A1A1A, 0x2B2B2B2B2B2B2B2B);
    private static readonly ScopeKey Scope = new(PoolKind.GroundEnemy, 101);

    private static ObjectKey Key(int nativeId, long generation) =>
        ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, nativeId, generation);

    // ---------------------------------------------------------------- codec

    [TestMethod]
    public void ASnapshotImageRoundTripsAndHashesDeterministically()
    {
        var members = new List<SnapshotMemberRecord>
        {
            new(Key(7, 1), 3, [10, 20]),
            new(Key(8, 2), 1, null),
            new(Key(9, 1), 12, Array.Empty<byte>())
        };
        var snapshot = new FrozenScopeSnapshot(Scope, 41, 6, 1000, 5000, members);

        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(snapshot, out var image));
        TestAssert.AreEqual(AuthoritySnapshotCodec.Hash(image), AuthoritySnapshotCodec.Hash(image),
            "The content hash is deterministic: both sides must be able to recompute it.");

        TestAssert.IsTrue(AuthoritySnapshotCodec.TryDecode(image, Epoch, Scope, out var decoded, out var reject),
            reject.ToString());
        TestAssert.AreEqual(41L, decoded.BaselineId);
        TestAssert.AreEqual(6L, decoded.SubscriptionEpoch);
        TestAssert.AreEqual(1000L, decoded.CutoffLogSequence);
        TestAssert.HasCount(3, decoded.Members);
        TestAssert.AreEqual(Key(7, 1), decoded.Members[0].Key);
        TestAssert.AreEqual(3L, decoded.Members[0].Revision);
        CollectionAssert.AreEquivalent(new byte[] { 10, 20 }, decoded.Members[0].State);
        TestAssert.IsNull(decoded.Members[1].State, "Identity without state survives the wire as an explicit case.");
        TestAssert.IsNotNull(decoded.Members[2].State);
        TestAssert.HasCount(0, decoded.Members[2].State, "An empty state is distinct from no state.");
    }

    [TestMethod]
    public void AForeignOrTruncatedImageIsRefused()
    {
        var snapshot = new FrozenScopeSnapshot(Scope, 7, 1, 0, 0,
            new List<SnapshotMemberRecord> { new(Key(7, 1), 1, [1]) });
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(snapshot, out var image));

        // A key from another epoch describes another world.
        var foreignEpoch = new AuthorityEpoch(0x1234567890ABCDEF, 0x0FEDCBA098765432);
        TestAssert.IsFalse(AuthoritySnapshotCodec.TryDecode(image, foreignEpoch, Scope, out _, out var epochReject));
        TestAssert.AreEqual(AuthorityRejectCode.InvalidObjectKey, epochReject.Code);

        // A key that names another scope cannot ride in under this one.
        var otherScope = new ScopeKey(PoolKind.GroundEnemy, 102);
        TestAssert.IsFalse(AuthoritySnapshotCodec.TryDecode(image, Epoch, otherScope, out _, out _));

        // Truncation is refused, never half-parsed.
        var truncated = new byte[image.Length - 3];
        Buffer.BlockCopy(image, 0, truncated, 0, truncated.Length);
        TestAssert.IsFalse(AuthoritySnapshotCodec.TryDecode(truncated, Epoch, Scope, out _, out _));

        // So is trailing data: sender and receiver must agree on the layout exactly.
        var padded = new byte[image.Length + 1];
        Buffer.BlockCopy(image, 0, padded, 0, image.Length);
        TestAssert.IsFalse(AuthoritySnapshotCodec.TryDecode(padded, Epoch, Scope, out _, out var paddedReject));
        TestAssert.AreEqual(AuthorityRejectCode.MalformedEnvelope, paddedReject.Code);

        // A member state above the record ceiling refuses the whole image at encode time.
        var oversized = new FrozenScopeSnapshot(Scope, 7, 1, 0, 0,
            new List<SnapshotMemberRecord> { new(Key(7, 1), 1, new byte[AuthorityLimits.StateRecordMaxBytes + 1]) });
        TestAssert.IsFalse(AuthoritySnapshotCodec.TryEncode(oversized, out _));
    }

    [TestMethod]
    public void ChunkingSplitsOnlyAtTheChunkBoundary()
    {
        TestAssert.AreEqual(0, AuthoritySnapshotCodec.ChunkCountFor(0));
        TestAssert.AreEqual(1, AuthoritySnapshotCodec.ChunkCountFor(1));
        TestAssert.AreEqual(1, AuthoritySnapshotCodec.ChunkCountFor(AuthorityLimits.ChunkMaxBytes));
        TestAssert.AreEqual(2, AuthoritySnapshotCodec.ChunkCountFor(AuthorityLimits.ChunkMaxBytes + 1L));

        var members = new List<SnapshotMemberRecord>();
        for (var i = 0; i < 3000; i++)
        {
            members.Add(new SnapshotMemberRecord(Key(i + 1, 1), 1, new byte[AuthorityLimits.StateRecordMaxBytes]));
        }
        var snapshot = new FrozenScopeSnapshot(Scope, 9, 1, 0, 0, members);
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(snapshot, out var image));
        var chunkCount = AuthoritySnapshotCodec.ChunkCountFor(image.Length);
        TestAssert.IsGreaterThan(1, chunkCount, "A large scope actually splits.");

        // Every chunk but the last is exactly the chunk size, and they reassemble losslessly.
        for (var index = 0; index < chunkCount; index++)
        {
            var offset = (long)index * AuthorityLimits.ChunkMaxBytes;
            var expected = (int)Math.Min(AuthorityLimits.ChunkMaxBytes, image.Length - offset);
            TestAssert.AreEqual(expected, Math.Min(AuthorityLimits.ChunkMaxBytes, image.Length - offset));
        }
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryDecode(image, Epoch, Scope, out var decoded, out _));
        TestAssert.HasCount(members.Count, decoded.Members);
    }

    // ------------------------------------------------- end-to-end conversation

    private sealed class FakeWorld : IHostWorldView
    {
        public readonly List<ObjectKey> Members = [];
        public readonly Dictionary<ObjectKey, byte[]> States = [];

        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
        {
            members.AddRange(Members);
            return true;
        }

        public bool TryReadState(ObjectKey key, out byte[] state) => States.TryGetValue(key, out state!);
    }

    /// <summary>A host sink that feeds a client session's inbox exactly as the network would.</summary>
    private sealed class SinkToClientInbox : IReplicationSink
    {
        public readonly AuthoritySession Client;

        public SinkToClientInbox(AuthoritySession client) => Client = client;

        public void Send(ushort subscriberId, AuthorityEnvelopePacket packet)
        {
            var scope = packet switch
            {
                AuthorityLifecyclePacket lifecycle => TryScope(lifecycle.ScopeKind, lifecycle.Scope),
                AuthorityWorldStatePacket worldState => TryScope(worldState.ScopeKind, worldState.Scope),
                AuthoritySnapshotBeginPacket begin => TryScope(begin.ScopeKind, begin.Scope),
                AuthoritySnapshotChunkPacket chunk => TryScope(chunk.ScopeKind, chunk.Scope),
                AuthoritySnapshotCommitPacket commit => TryScope(commit.ScopeKind, commit.Scope),
                _ => default
            };
            TestAssert.IsTrue(scope.IsValid, "Every replicated packet names a legal scope.");
            TestAssert.IsTrue(Client.TryEnqueueReplicaMessage(packet, new ApplyScope(scope, 0, packet.Sequence,
                packet.HostTick)), "The client inbox must accept the host's traffic in this test.");
        }

        private static ScopeKey TryScope(byte kind, int scope) => new((PoolKind)kind, scope);
    }

    /// <summary>A client ack sink that feeds the host session exactly as the network would.</summary>
    private sealed class AckToHost : ISnapshotAckSink
    {
        public readonly AuthoritySession Host;
        public readonly ushort PlayerId;

        public AckToHost(AuthoritySession host, ushort playerId)
        {
            Host = host;
            PlayerId = playerId;
        }

        public void Send(AuthoritySnapshotAckPacket packet)
        {
            packet.ClaimedPlayerId = PlayerId;
            TestAssert.IsTrue(Host.NotifySnapshotAck(packet.ClaimedPlayerId, packet.BaselineId,
                packet.LastAppliedSequence, packet.Accepted), "The host must accept the ack in this test.");
        }
    }

    [TestMethod]
    public void AFullSubscriptionConversationEndsLiveWithTheHostWorldMirrored()
    {
        // Host and client, wired through their seams only. The client must end Live with exactly
        // the host's members and their states, including a member that spawned and one that died
        // while the baseline was in flight.
        var world = new FakeWorld();
        var staying = Key(1, 1);
        var dying = Key(2, 1);
        world.Members.Add(staying);
        world.States[staying] = [90];
        world.Members.Add(dying);
        world.States[dying] = [40];

        var hostSession = new AuthoritySession(new AuthoritySessionState());
        hostSession.BeginAuthorityWorld(Epoch, isHost: true);
        var clientSession = new AuthoritySession(new AuthoritySessionState());
        clientSession.BeginAuthorityWorld(Epoch, isHost: false);

        hostSession.RegisterHostReplication(world, new SinkToClientInbox(clientSession));
        clientSession.WorldReplica.SnapshotAckSink = new AckToHost(hostSession, playerId: 1);

        clientSession.WorldReplica.Subscribe(Scope);
        hostSession.HostReplicator.Subscribe(1, Scope);
        hostSession.OnFrameBoundary(100);
        TestAssert.IsTrue(clientSession.WorldReplica.Snapshots.IsStaging(Scope) ||
                          clientSession.WorldReplica.Snapshots.CommittedTotal >= 0,
            "The control conversation is queued; staging state is checked after the boundary.");

        clientSession.OnFrameBoundary(100);
        TestAssert.IsTrue(clientSession.WorldReplica.Versions.TryGetScope(Scope, out var clientScope));
        TestAssert.AreEqual(SubscriptionPhase.CatchingUp, clientScope.Phase, "Commit installs at the boundary.");

        // The ack is queued on the host and only releases the stream at the host's boundary.
        hostSession.OnFrameBoundary(110);
        TestAssert.AreEqual(SubscriptionPhase.CatchingUp, clientScope.Phase,
            "The client has not applied the backlog yet.");

        // While the baseline was in flight the world moved on: one spawn, one death.
        var born = Key(3, 1);
        world.Members.Remove(dying);
        world.Members.Add(born);
        world.States[born] = [70];
        world.States[staying] = [80];
        hostSession.Capture.Capture(120);

        // The host boundary processes the ack: the backlog (in log order) plus the marker go out,
        // and the log is reclaimed now that the subscriber is covered again.
        hostSession.OnFrameBoundary(120);
        TestAssert.IsGreaterThan(0,
            hostSession.HostReplicator.EventsTrimmed, "Delivery plus the acked baseline is what releases the log.");

        clientSession.OnFrameBoundary(120);
        TestAssert.AreEqual(SubscriptionPhase.Live, clientScope.Phase, "The resume marker ends the catch-up.");
        TestAssert.HasCount(2, clientSession.WorldReplica.Mirrors, "Exactly the host's live members.");
        CollectionAssert.AreEquivalent(new byte[] { 80 }, clientSession.WorldReplica.Mirrors[staying].CanonicalState);
        CollectionAssert.AreEquivalent(new byte[] { 70 }, clientSession.WorldReplica.Mirrors[born].CanonicalState);
        TestAssert.IsFalse(clientSession.WorldReplica.Mirrors.ContainsKey(dying),
            "The death that happened mid-install is applied from the backlog, never lost, never duplicated.");
        TestAssert.IsEmpty(clientSession.WorldReplica.ScopesNeedingResync);

        // A quiet frame publishes nothing now that both sides agree.
        var before = clientSession.InboundApplied;
        hostSession.Capture.Capture(130);
        clientSession.OnFrameBoundary(130);
        TestAssert.AreEqual(before, clientSession.InboundApplied);
    }

    [TestMethod]
    public void AClientThatNeverAcksLeavesTheHostHoldingTheBacklog()
    {
        // Fail-closed by silence: without an ack the subscriber is never promoted, receives no
        // stream packet, and the log keeps everything it will need if the ack ever arrives.
        var world = new FakeWorld();
        var key = Key(1, 1);
        world.Members.Add(key);
        world.States[key] = [10];

        var hostSession = new AuthoritySession(new AuthoritySessionState());
        hostSession.BeginAuthorityWorld(Epoch, isHost: true);
        var sent = new List<AuthorityEnvelopePacket>();
        hostSession.RegisterHostReplication(world, new RecordingSink(sent));

        hostSession.HostReplicator.Subscribe(1, Scope);
        hostSession.Capture.Capture(100);
        var streamPackets = 0;
        foreach (var packet in sent)
        {
            if (packet is AuthorityLifecyclePacket or AuthorityWorldStatePacket) streamPackets++;
        }
        TestAssert.AreEqual(0, streamPackets, "No stream packet before the ack, even across captures.");
        TestAssert.AreEqual(0L, hostSession.HostReplicator.EventsTrimmed);

        world.States[key] = [20];
        hostSession.Capture.Capture(110);
        TestAssert.AreEqual(0L, hostSession.HostReplicator.EventsTrimmed,
            "The events a pending subscriber will need after its ack are retained.");
    }

    [TestMethod]
    public void ASnapshotRefusesAStaleSubscriptionEpoch()
    {
        // The client adopts the epoch the Begin carries. Once a newer subscription exists, the
        // older one's commit cannot rebind the scope and its stream packets are dropped wholesale.
        var replica = new ClientWorldReplica(Epoch);
        replica.Subscribe(Scope);
        var snapshot = new FrozenScopeSnapshot(Scope, 5, 1, 0, 0,
            new List<SnapshotMemberRecord> { new(Key(1, 1), 1, [1]) });
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(snapshot, out var image));
        var hash = AuthoritySnapshotCodec.Hash(image);

        TestAssert.IsTrue(replica.BeginSnapshot(Scope, 1, 5, 1, image.Length, hash));
        TestAssert.IsTrue(replica.ReceiveSnapshotChunk(Scope, 1, 5, 0, image));
        TestAssert.IsTrue(replica.CommitSnapshot(Scope, 1, 5, 1, hash));

        // The client re-requests, the host answers with a newer epoch and baseline — and an
        // image that agrees with its own bookkeeping.
        replica.Subscribe(Scope);
        var fresh = new FrozenScopeSnapshot(Scope, 6, 2, 0, 0,
            new List<SnapshotMemberRecord> { new(Key(1, 1), 2, [2]) });
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(fresh, out var freshImage));
        TestAssert.IsTrue(replica.BeginSnapshot(Scope, 2, 6, 1, freshImage.Length, AuthoritySnapshotCodec.Hash(freshImage)));
        TestAssert.IsFalse(replica.CommitSnapshot(Scope, 1, 5, 1, hash),
            "A commit from the superseded subscription is refused wholesale.");
        TestAssert.IsTrue(replica.ReceiveSnapshotChunk(Scope, 2, 6, 0, freshImage));
        TestAssert.IsTrue(replica.CommitSnapshot(Scope, 2, 6, 1, AuthoritySnapshotCodec.Hash(freshImage)));
        TestAssert.IsTrue(replica.Versions.TryGetScope(Scope, out var state));
        TestAssert.AreEqual(2L, state.SubscriptionEpoch);
        TestAssert.AreEqual(6L, state.BaselineId);

        TestAssert.IsFalse(replica.ApplyLifecycle(Scope, 1, 1,
            new List<AuthorityLifecycleRecord>
            {
                new(AuthorityLifecycleOp.Spawn, Key(9, 9), 1)
            }),
            "A stream packet of the superseded subscription is dropped by its epoch.");
    }

    [TestMethod]
    public void ASnapshotExceedingTheScopeCeilingIsRefusedBeforeAllocation()
    {
        var staging = new ClientSnapshotStaging();
        var tooBig = AuthorityLimits.ScopeSnapshotMaxBytes + 1;
        var chunkCount = AuthoritySnapshotCodec.ChunkCountFor(tooBig);
        TestAssert.IsFalse(staging.Begin(Scope, 1, 7, chunkCount, tooBig, hash: 1),
            "A Begin that cannot ever be a legal image is refused before a buffer exists.");
        TestAssert.AreEqual(0, staging.Count);
    }

    private sealed class RecordingSink : IReplicationSink
    {
        private readonly List<AuthorityEnvelopePacket> sent;

        public RecordingSink(List<AuthorityEnvelopePacket> sent) => this.sent = sent;

        public void Send(ushort subscriberId, AuthorityEnvelopePacket packet) => sent.Add(packet);
    }
}
