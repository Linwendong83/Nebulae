using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>Verifies that replication delivers every frame's output in log order within that frame, including
/// the complete baseline conversation.</summary>
[TestClass]
public class AuthorityUnbudgetedDeliveryTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0B0B0B0B0B0B0B0B, 0x0B0B0B0B0B0B0B0B);
    private static readonly ScopeKey Scope = new(PoolKind.GroundEnemy, 101);
    private static readonly ScopeKey OtherScope = new(PoolKind.GroundEnemy, 102);

    private sealed class FakeWorld : IHostWorldView
    {
        public readonly Dictionary<ScopeKey, List<ObjectKey>> Members = [];
        public readonly Dictionary<ObjectKey, byte[]> States = [];
        public bool MembersReadable = true;

        public ObjectKey Add(ScopeKey scope, int nativeId, byte[] state, long generation = 1)
        {
            var key = ObjectKey.Create(Epoch, scope.Kind, scope.Scope, nativeId, generation);
            if (!Members.TryGetValue(scope, out var list))
            {
                list = [];
                Members[scope] = list;
            }
            if (!list.Contains(key)) list.Add(key);
            if (state != null) States[key] = state;
            return key;
        }

        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
        {
            if (!MembersReadable) return false;
            if (Members.TryGetValue(scope, out var list)) members.AddRange(list);
            return true;
        }

        public bool TryReadState(ObjectKey key, out byte[] state) => States.TryGetValue(key, out state!);
    }

    private sealed class RecordingSink : IReplicationSink
    {
        public readonly List<(ushort Subscriber, AuthorityEnvelopePacket Packet)> Sent = [];

        public void Send(ushort subscriberId, AuthorityEnvelopePacket packet) =>
            Sent.Add((subscriberId, packet));

        public List<AuthorityEnvelopePacket> OfFamily(AuthorityFamily family)
        {
            var packets = new List<AuthorityEnvelopePacket>();
            foreach (var entry in Sent)
            {
                if (entry.Packet.Family == family) packets.Add(entry.Packet);
            }
            return packets;
        }

        public void Clear() => Sent.Clear();
    }

    /// <summary>Subscribes and completes the baseline so later frames deliver increments only.</summary>
    private static void Establish(HostWorldReplicator replicator, RecordingSink sink, ScopeKey scope,
        ushort subscriber = 1)
    {
        var before = sink.Sent.Count;
        replicator.Subscribe(subscriber, scope);
        var begin = (AuthoritySnapshotBeginPacket)Find(sink, before, AuthorityFamily.SnapshotBegin);
        var commit = (AuthoritySnapshotCommitPacket)Find(sink, before, AuthorityFamily.SnapshotCommit);
        replicator.OnSnapshotAck(subscriber, begin.BaselineId, commit.ChunkCount, accepted: true);
    }

    private static AuthorityEnvelopePacket Find(RecordingSink sink, int from, AuthorityFamily family)
    {
        for (var i = from; i < sink.Sent.Count; i++)
        {
            if (sink.Sent[i].Packet.Family == family) return sink.Sent[i].Packet;
        }
        throw new System.InvalidOperationException("No " + family + " packet was sent.");
    }

    private static AuthoritySessionContext Context() =>
        new(true, Epoch, connection: default,
            connectedPlayerId: 1, isHost: true);

    private static List<AuthorityWorldStateRecord> Decode(AuthorityWorldStatePacket packet)
    {
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryDecode(packet.Data, 0, packet.Data.Length,
                packet.RecordCount, Context(), out var records, out var reject),
            "The delivered batch must decode: " + reject);
        return records;
    }

    private static List<AuthorityWorldStateRecord> DecodeAll(RecordingSink sink)
    {
        var records = new List<AuthorityWorldStateRecord>();
        foreach (AuthorityWorldStatePacket packet in sink.OfFamily(AuthorityFamily.WorldState))
        {
            records.AddRange(Decode(packet));
        }
        return records;
    }

    [TestMethod]
    public void EveryPendingRevisionOfAKeyShipsWhenTheBacklogIsReleased()
    {
        // A member that keeps changing while its baseline is in flight accumulates one state event per
        // change in the log. With nothing to postpone or merge, the ack releases every revision, in
        // order, and the newest is the one the replica converges on.
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var replicator = new HostWorldReplicator(Epoch, world, sink);
        var key = world.Add(Scope, 1, [1, 1]);
        replicator.Subscribe(1, Scope);
        world.Add(Scope, 1, [2, 2]);
        replicator.CaptureFrame(10);
        world.Add(Scope, 1, [3, 3]);
        replicator.CaptureFrame(11);

        var begin = (AuthoritySnapshotBeginPacket)Find(sink, 0, AuthorityFamily.SnapshotBegin);
        var commit = (AuthoritySnapshotCommitPacket)Find(sink, 0, AuthorityFamily.SnapshotCommit);
        sink.Clear();
        replicator.OnSnapshotAck(1, begin.BaselineId, commit.ChunkCount, accepted: true);

        var delivered = DecodeAll(sink);
        TestAssert.HasCount(2, delivered, "Every recorded revision is delivered, none is merged away.");
        TestAssert.AreEqual(key, delivered[0].Key);
        TestAssert.AreEqual(key, delivered[1].Key);
        TestAssert.IsGreaterThan(delivered[0].Revision, delivered[1].Revision, "Revisions ascend.");
        CollectionAssert.AreEqual(new byte[] { 3, 3 }, delivered[1].State, "The newest state survives.");
    }

    [TestMethod]
    public void ALargeBurstOfChangesShipsInASingleBatchInTheFrameItHappened()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var replicator = new HostWorldReplicator(Epoch, world, sink);
        world.Add(Scope, 1, [1]);
        Establish(replicator, sink, Scope);

        for (var i = 0; i < 500; i++) world.Add(Scope, 1000 + i, new byte[256]);
        sink.Clear();
        replicator.CaptureFrame(10);

        var batches = sink.OfFamily(AuthorityFamily.WorldState);
        TestAssert.HasCount(1, batches, "Nothing waits for a later frame.");
        TestAssert.AreEqual(500, ((AuthorityWorldStatePacket)batches[0]).RecordCount,
            "The whole burst travels as one statement of what changed.");

        // The members are new, so their spawns travel in the same frame as their first states:
        // neither half of the burst is held behind the other.
        var lifecycle = sink.OfFamily(AuthorityFamily.Lifecycle);
        TestAssert.HasCount(1, lifecycle, "The spawns are one batch, in the same frame.");
        TestAssert.AreEqual(500, ((AuthorityLifecyclePacket)lifecycle[0]).RecordCount);

        // The next frame is quiet: what was delivered is not delivered twice.
        sink.Clear();
        replicator.CaptureFrame(11);
        TestAssert.HasCount(0, sink.OfFamily(AuthorityFamily.WorldState));
    }

    [TestMethod]
    public void IdentityAndItsFirstStateTravelInTheFrameThatProducedThem()
    {
        // Spawns and the states behind them are one frame's work, so a subscriber is never left
        // holding identities without their hitpoints, and never left waiting for either half.
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var replicator = new HostWorldReplicator(Epoch, world, sink);
        world.Add(Scope, 1, [7, 7]);
        Establish(replicator, sink, Scope);
        sink.Clear();

        world.Add(Scope, 2, [1, 1]);
        world.Add(Scope, 3, [2, 2]);
        replicator.CaptureFrame(10);

        var lifecycle = sink.OfFamily(AuthorityFamily.Lifecycle);
        TestAssert.HasCount(1, lifecycle, "Both spawns travel in one batch.");
        TestAssert.AreEqual(2, ((AuthorityLifecyclePacket)lifecycle[0]).RecordCount);

        var states = DecodeAll(sink);
        TestAssert.HasCount(2, states, "Both first states travel in the same frame as their spawns.");
        CollectionAssert.AreEqual(new byte[] { 1, 1 }, states[0].State);
        CollectionAssert.AreEqual(new byte[] { 2, 2 }, states[1].State);
    }

    [TestMethod]
    public void ASubscriberThatKeepsUpGetsOneBatchPerChange()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var replicator = new HostWorldReplicator(Epoch, world, sink);
        world.Add(Scope, 1, [1]);
        Establish(replicator, sink, Scope);

        for (var frame = 0; frame < 5; frame++)
        {
            world.Add(Scope, 1, new byte[] { (byte)(10 + frame) });
            sink.Clear();
            replicator.CaptureFrame(10 + frame);
            TestAssert.HasCount(1, sink.OfFamily(AuthorityFamily.WorldState),
                "One changed member, one batch, sent in the frame it changed.");
            var records = DecodeAll(sink);
            TestAssert.HasCount(1, records);
            CollectionAssert.AreEqual(new byte[] { (byte)(10 + frame) }, records[0].State);
        }
    }

    [TestMethod]
    public void AMultiChunkBaselineShipsBeginEveryChunkAndCommitInOneFrame()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var replicator = new HostWorldReplicator(Epoch, world, sink);

        // A second scope is already live, so the claim can be checked against real traffic: the
        // baseline must not displace the other scope's delivery either.
        world.Add(OtherScope, 1, [1]);
        Establish(replicator, sink, OtherScope);

        // 300 members at 256 bytes each is well past the 64 KiB chunk size.
        for (var i = 0; i < 300; i++) world.Add(Scope, 100 + i, new byte[256]);
        sink.Clear();
        replicator.Subscribe(1, Scope);

        var begin = (AuthoritySnapshotBeginPacket)Find(sink, 0, AuthorityFamily.SnapshotBegin);
        TestAssert.IsGreaterThanOrEqualTo(2, begin.ChunkCount, "The test needs a baseline larger than one chunk.");
        TestAssert.HasCount(1, sink.OfFamily(AuthorityFamily.SnapshotBegin));
        TestAssert.HasCount(begin.ChunkCount, sink.OfFamily(AuthorityFamily.SnapshotChunk),
            "Every chunk ships in the subscribing frame.");
        TestAssert.HasCount(1, sink.OfFamily(AuthorityFamily.SnapshotCommit),
            "The conversation completes in the same frame it began.");

        var commit = (AuthoritySnapshotCommitPacket)Find(sink, 0, AuthorityFamily.SnapshotCommit);
        TestAssert.AreEqual(begin.ChunkCount, commit.ChunkCount);
        TestAssert.AreEqual(begin.BaselineId, commit.BaselineId);

        // The other scope keeps flowing while the baseline is delivered, in the same frame.
        world.Add(OtherScope, 2, [2]);
        sink.Clear();
        replicator.CaptureFrame(11);
        TestAssert.HasCount(1, sink.OfFamily(AuthorityFamily.Lifecycle),
            "The other scope's spawn is delivered without waiting behind the baseline.");
    }

    [TestMethod]
    public void AResyncDuringABaselineStillRebaselines()
    {
        // Regression: the client only acks a baseline it installed, so a broken conversation leaves
        // the host's cursor waiting forever. A resync must discard that in-flight baseline instead of
        // being refused as "already subscribing", or the scope would stay in Snapshotting for the
        // rest of the session.
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var replicator = new HostWorldReplicator(Epoch, world, sink);
        for (var i = 0; i < 300; i++) world.Add(Scope, 100 + i, new byte[256]);
        replicator.Subscribe(1, Scope);

        var firstBegin = (AuthoritySnapshotBeginPacket)Find(sink, 0, AuthorityFamily.SnapshotBegin);
        sink.Clear();
        TestAssert.IsTrue(replicator.RequestResync(1, Scope), "A live subscription can be resynced.");

        var secondBegin = (AuthoritySnapshotBeginPacket)Find(sink, 0, AuthorityFamily.SnapshotBegin);
        TestAssert.AreNotEqual(firstBegin.BaselineId, secondBegin.BaselineId,
            "A resync mints a new baseline; replaying the old one would refuse the old stream's tail.");

        // The replacement conversation runs to completion on its own, with no frames pumped.
        TestAssert.HasCount(secondBegin.ChunkCount, sink.OfFamily(AuthorityFamily.SnapshotChunk));
        TestAssert.HasCount(1, sink.OfFamily(AuthorityFamily.SnapshotCommit));
    }

    [TestMethod]
    public void ARemovedSubscriberReceivesNothingFurther()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var replicator = new HostWorldReplicator(Epoch, world, sink);
        for (var i = 0; i < 300; i++) world.Add(Scope, 100 + i, new byte[256]);
        replicator.Subscribe(1, Scope);
        sink.Clear();

        replicator.RemoveSubscriber(1);
        replicator.CaptureFrame(10);

        TestAssert.HasCount(0, sink.OfFamily(AuthorityFamily.SnapshotChunk),
            "A departed subscriber's chunks are dropped, not shipped to nobody.");
        TestAssert.HasCount(0, sink.OfFamily(AuthorityFamily.SnapshotCommit));
        TestAssert.HasCount(0, sink.OfFamily(AuthorityFamily.WorldState));
        TestAssert.HasCount(0, sink.OfFamily(AuthorityFamily.Lifecycle));
    }
}
