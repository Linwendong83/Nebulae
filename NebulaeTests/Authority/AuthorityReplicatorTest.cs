
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// the host's standard replicator publishes membership and absolute state as deterministic,
/// revisioned events scanned from the canonical world, and a subscription starts with a frozen
/// baseline — Begin/Chunk/Commit — whose ack releases the backlog and the live stream.
/// </summary>
[TestClass]
public class HostReplicatorTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0606060606060606, 0x0606060606060606);
    private static readonly ScopeKey Scope = new(PoolKind.GroundEnemy, 101);

    private sealed class FakeWorld : IHostWorldView, IHostDeathView
    {
        public readonly Dictionary<ScopeKey, List<ObjectKey>> Members = [];
        public readonly Dictionary<ObjectKey, byte[]> States = [];
        public readonly HashSet<ObjectKey> Deaths = [];
        public bool MembersReadable = true;
        public int MemberReads;

        public void Add(int nativeId, long generation, byte[]? state)
        {
            var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, nativeId, generation);
            if (!Members.TryGetValue(Scope, out var list))
            {
                list = [];
                Members[Scope] = list;
            }
            list.Add(key);
            if (state != null) States[key] = state;
        }

        public void Remove(int nativeId, long generation) =>
            Members[Scope].Remove(ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, nativeId, generation));

        /// <summary>Records the death the host's ledger would hold for this exact key.</summary>
        public void Kill(int nativeId, long generation) =>
            Deaths.Add(ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, nativeId, generation));

        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
        {
            MemberReads++;
            if (!MembersReadable) return false;
            if (Members.TryGetValue(scope, out var list)) members.AddRange(list);
            return true;
        }

        public bool TryReadState(ObjectKey key, out byte[] state) => States.TryGetValue(key, out state!);

        public bool IsDead(in ObjectKey key) => Deaths.Contains(key);
    }

    private sealed class RecordingSink : IReplicationSink
    {
        public readonly List<AuthorityEnvelopePacket> Sent = [];
        private readonly List<ushort> subscribers = [];

        public void Send(ushort subscriberId, AuthorityEnvelopePacket packet)
        {
            Sent.Add(packet);
            subscribers.Add(subscriberId);
        }

        public List<AuthorityEnvelopePacket> For(ushort subscriberId)
        {
            var packets = new List<AuthorityEnvelopePacket>();
            for (var i = 0; i < Sent.Count; i++)
            {
                if (subscribers[i] == subscriberId) packets.Add(Sent[i]);
            }
            return packets;
        }

        /// <summary>Forgets the recorded traffic, including who it was for.</summary>
        public void Clear()
        {
            Sent.Clear();
            subscribers.Clear();
        }
    }

    private static HostWorldReplicator Replicator(FakeWorld world, RecordingSink sink, int logCapacity = 4096) =>
        new(Epoch, world, sink, logCapacity, deathView: world);

    [TestMethod]
    public void PacedCombatStatesKeepSpawnDespawnAndRecycledSlotsImmediate()
    {
        var world = new FakeWorld(); world.Add(7, 1, [10]);
        var sink = new RecordingSink();
        var host = Replicator(world, sink); host.StateIntervalTicks = 3;
        SubscribeAndAck(host, sink, 1, Scope);
        host.CaptureFrame(3);
        var before = host.EventsAppended;
        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        world.States[key] = [9];
        host.CaptureFrame(4); host.CaptureFrame(5);
        TestAssert.AreEqual(before, host.EventsAppended);
        host.CaptureFrame(6);
        TestAssert.AreEqual(before + 1, host.EventsAppended, "Latest state arrives within three ticks.");
        world.Remove(7, 1); world.Add(7, 2, [20]);
        host.CaptureFrame(7);
        TestAssert.AreEqual(before + 4, host.EventsAppended, "Spawn, initial state and old-generation despawn must not wait for tick 9.");
        world.Remove(7, 2); host.CaptureFrame(8);
        TestAssert.AreEqual(before + 5, host.EventsAppended, "A death cannot wait for a state sample.");
        world.Add(9, 1, [30]); host.CaptureFrame(9);
        host.StateIntervalFor = candidate => candidate.NativeId == 9 ? 1 : 3;
        before = host.EventsAppended;
        world.States[ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 9, 1)] = [29];
        host.CaptureFrame(10);
        TestAssert.AreEqual(before + 1, host.EventsAppended, "Fast-moving space actors can retain per-frame samples.");
        host.StateIntervalFor = _ => 60;
        world.States[ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 9, 1)] = [28];
        host.CaptureFrame(11);
        TestAssert.AreEqual(before + 1, host.EventsAppended, "Distant state can wait without removing its identity.");
        host.CaptureFrame(69);
        TestAssert.AreEqual(before + 2, host.EventsAppended, "Distant objects still converge.");
        world.Remove(9, 1); host.CaptureFrame(70);
        TestAssert.AreEqual(before + 3, host.EventsAppended, "Distant deaths are immediate too.");
    }

    [TestMethod]
    public void AnEventDrivenMemberCanStillBeSlowedByItsOwnInterval()
    {
        // Per-key sampling intervals also apply to event-driven members.
        var world = new FakeWorld(); world.Add(7, 1, [10]);
        var sink = new RecordingSink();
        var host = Replicator(world, sink);
        host.EventDrivenStateFor = _ => true;
        host.StateIntervalFor = _ => 60;
        SubscribeAndAck(host, sink, 1, Scope);
        host.CaptureFrame(60);
        var before = host.EventsAppended;
        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        world.States[key] = [11];

        // The phase is 7 % 60, so this member is read on the ticks where tick % 60 == 7.
        host.CaptureFrame(61);
        TestAssert.AreEqual(before, host.EventsAppended, "A throttled member waits for its own phase.");
        host.CaptureFrame(66);
        TestAssert.AreEqual(before, host.EventsAppended, "Still waiting: the interval is 60, not 1.");
        host.CaptureFrame(127);
        TestAssert.AreEqual(before + 1, host.EventsAppended, "The throttled state still converges.");

        // The throttle is an override, not the cold tier: an engaged subscriber keeps the scope hot.
        TestAssert.AreEqual(0L, host.ColdScopeFrames);

        // Identity and death are never paced, whatever the interval says. A spawn and its first
        // state are two events, and both are immediate for a member with no published state yet.
        world.Add(8, 1, [40]);
        host.CaptureFrame(128);
        TestAssert.AreEqual(before + 3, host.EventsAppended, "A spawn and its first state never wait for an override.");
        world.Kill(8, 1); world.Remove(8, 1);
        host.CaptureFrame(129);
        TestAssert.AreEqual(before + 4, host.EventsAppended, "A death never waits for an override.");

    }

    [TestMethod]
    public void AnEventDrivenMemberKeepsEveryTickUnlessAnOverrideSlowsIt()
    {
        var world = new FakeWorld(); world.Add(7, 1, [10]);
        var sink = new RecordingSink();
        var host = Replicator(world, sink);
        host.EventDrivenStateFor = _ => true;
        SubscribeAndAck(host, sink, 1, Scope);
        host.CaptureFrame(3);
        var before = host.EventsAppended;
        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);

        world.States[key] = [11];
        host.CaptureFrame(4);
        TestAssert.AreEqual(before + 1, host.EventsAppended, "No override leaves the event-driven cadence alone.");

        // An override may only slow a member down, so answering the cadence it already has is a no-op
        // rather than a second, slower path.
        host.StateIntervalFor = _ => 1;
        world.States[key] = [12];
        host.CaptureFrame(5);
        TestAssert.AreEqual(before + 2, host.EventsAppended, "An override cannot speed an event-driven member up.");

        host.StateIntervalFor = _ => 60;
        world.States[key] = [13];
        host.CaptureFrame(6);
        TestAssert.AreEqual(before + 2, host.EventsAppended, "The override applies from the next phase, not retroactively.");
        host.CaptureFrame(67);
        TestAssert.AreEqual(before + 3, host.EventsAppended, "The override still converges at its own phase.");
    }

    [TestMethod]
    public void QuietEventDrivenScopesStillRespectTheDistantStateOverrideWithoutDelayingLifecycle()
    {
        var world = new FakeWorld(); world.Add(7, 1, [10]);
        var sink = new RecordingSink(); var host = Replicator(world, sink);
        host.EventDrivenStateFor = _ => true;
        host.EngagementProvider = _ => false;
        host.StateIntervalFor = _ => 60;
        SubscribeAndAck(host, sink, 1, Scope);
        var before = host.EventsAppended;
        world.States[ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1)] = [11];
        host.CaptureFrame(6); TestAssert.AreEqual(before, host.EventsAppended);
        host.CaptureFrame(7); TestAssert.AreEqual(before + 1, host.EventsAppended);
        world.Add(8, 1, [40]); host.CaptureFrame(8);
        TestAssert.AreEqual(before + 3, host.EventsAppended, "A first state is immediate even in a distant quiet scope.");
        world.Kill(8, 1); world.Remove(8, 1); host.CaptureFrame(9);
        TestAssert.AreEqual(before + 4, host.EventsAppended, "Death delivery is not throttled.");
    }

    // the engagement-driven cold cadence

    [TestMethod]
    public void AQuietScopeIsSampledAtTheColdCadence()
    {
        var world = new FakeWorld(); world.Add(7, 1, [10]);
        var sink = new RecordingSink();
        var host = Replicator(world, sink);
        host.StateIntervalTicks = 3;
        host.ColdStateIntervalTicks = 15;
        // Nobody is fighting, so the scope is cold.
        host.EngagementProvider = _ => false;
        SubscribeAndAck(host, sink, 1, Scope);
        host.CaptureFrame(15);
        var before = host.EventsAppended;
        world.States[ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1)] = [11];
        // The cold interval is spread by native id (7 % 15), so this member is sampled on the ticks
        // where tick % 15 == 7 — the trade that keeps a slow cadence from becoming one large batch.
        host.CaptureFrame(16); host.CaptureFrame(21);
        TestAssert.AreEqual(before, host.EventsAppended, "A cold scope does not publish on the hot cadence.");
        host.CaptureFrame(22);
        TestAssert.AreEqual(before + 1, host.EventsAppended, "The cold scope still converges, at its own phase.");
        TestAssert.AreEqual(4L, host.ColdScopeFrames, "The tier is reported so a run can show what it covered.");
    }

    [TestMethod]
    public void AnEngagedSubscriberKeepsTheHotCadence()
    {
        var world = new FakeWorld(); world.Add(7, 1, [10]);
        var sink = new RecordingSink();
        var host = Replicator(world, sink);
        host.StateIntervalTicks = 3;
        host.ColdStateIntervalTicks = 15;
        // One subscriber of two is fighting: the scope is hot for both, because the tier is
        // scope-level and filtering per subscriber would break the digest's premise.
        host.EngagementProvider = id => id == 2;
        SubscribeAndAck(host, sink, 1, Scope);
        SubscribeAndAck(host, sink, 2, Scope);
        host.CaptureFrame(3);
        var before = host.EventsAppended;
        world.States[ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1)] = [12];
        host.CaptureFrame(4); host.CaptureFrame(5);
        TestAssert.AreEqual(before, host.EventsAppended);
        host.CaptureFrame(6);
        TestAssert.AreEqual(before + 1, host.EventsAppended, "One fighting subscriber holds the whole scope hot.");
        TestAssert.AreEqual(0L, host.ColdScopeFrames);
    }

    [TestMethod]
    public void ColdSamplingStillPublishesMembershipAndDeathImmediately()
    {
        var world = new FakeWorld(); world.Add(7, 1, [10]);
        var sink = new RecordingSink();
        var host = Replicator(world, sink);
        host.ColdStateIntervalTicks = 60;
        host.EngagementProvider = _ => false;
        SubscribeAndAck(host, sink, 1, Scope);
        host.CaptureFrame(1);
        var before = host.EventsAppended;
        // A spawn and its first state never wait for the cold interval: identity precedes HP, and a
        // pooled slot's new generation is a different object, not a slower one.
        world.Add(7, 2, [20]);
        host.CaptureFrame(2);
        TestAssert.AreEqual(before + 2, host.EventsAppended, "Spawn and initial state are immediate while cold.");
        world.Kill(7, 2);
        world.Remove(7, 2);
        host.CaptureFrame(3);
        TestAssert.AreEqual(before + 3, host.EventsAppended, "A death never waits for a cold sample.");
    }

    [TestMethod]
    public void WithoutAnEngagementProviderEveryScopeStaysHot()
    {
        // The tier refines the pacing, it does not replace it: a replicator nobody told about
        // engagement behaves exactly as before.
        var world = new FakeWorld(); world.Add(7, 1, [10]);
        var sink = new RecordingSink();
        var host = Replicator(world, sink);
        host.StateIntervalTicks = 3;
        host.ColdStateIntervalTicks = 60;
        SubscribeAndAck(host, sink, 1, Scope);
        host.CaptureFrame(3);
        var before = host.EventsAppended;
        world.States[ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1)] = [13];
        host.CaptureFrame(6);
        TestAssert.AreEqual(before + 1, host.EventsAppended);
        TestAssert.AreEqual(0L, host.ColdScopeFrames);
    }

    private static AuthorityLifecyclePacket Lifecycle(AuthorityEnvelopePacket packet)
    {
        var lifecycle = packet as AuthorityLifecyclePacket;
        TestAssert.IsNotNull(lifecycle);
        return lifecycle;
    }
    private static AuthorityWorldStatePacket State(AuthorityEnvelopePacket packet)
    {
        var state = packet as AuthorityWorldStatePacket;
        TestAssert.IsNotNull(state);
        return state;
    }

    /// <summary>
    /// Subscribes the subscriber and answers the baseline conversation: reads the Begin's
    /// bookkeeping and acks the baseline as installed. Returns the baseline id.
    /// </summary>
    private static long SubscribeAndAck(HostWorldReplicator replicator, RecordingSink sink, ushort subscriber,
        ScopeKey scope, bool accepted = true)
    {
        var before = sink.For(subscriber).Count;
        replicator.Subscribe(subscriber, scope);
        var control = sink.For(subscriber);
        var begin = control[before] as AuthoritySnapshotBeginPacket;
        TestAssert.IsNotNull(begin, "A subscription starts with a snapshot Begin.");
        TestAssert.HasCount(before + begin.ChunkCount + 2, control, "Begin, every chunk, then Commit.");
        TestAssert.AreEqual(begin.BaselineId,
            (control[control.Count - 2] as AuthoritySnapshotChunkPacket)?.BaselineId);
        TestAssert.AreEqual(begin.BaselineId,
            (control[control.Count - 1] as AuthoritySnapshotCommitPacket)?.BaselineId);
        replicator.OnSnapshotAck(subscriber, begin.BaselineId, lastAppliedSequence: 0, accepted);
        return begin.BaselineId;
    }

    private static byte[] ReassembleSnapshot(RecordingSink sink, ushort subscriber)
    {
        // The latest Begin wins; only its own chunks belong to its image.
        AuthoritySnapshotBeginPacket? begin = null;
        foreach (var packet in sink.For(subscriber))
        {
            if (packet is AuthoritySnapshotBeginPacket b) begin = b;
        }
        TestAssert.IsNotNull(begin);
        var chunks = new List<byte[]>();
        foreach (var packet in sink.For(subscriber))
        {
            if (packet is AuthoritySnapshotChunkPacket c && c.BaselineId == begin.BaselineId)
            {
                chunks.Add(c.Data);
            }
        }
        var image = new byte[begin.TotalBytes];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, image, offset, chunk.Length);
            offset += chunk.Length;
        }
        return image;
    }

    [TestMethod]
    public void AJoiningSubscriberReceivesABaselineThenAStreamMarker()
    {
        var world = new FakeWorld();
        world.Add(7, 1, [10, 0, 90]);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);
        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);

        replicator.Subscribe(1, Scope);

        // The subscription conversation, not a join burst: Begin/Chunk/Commit only, no stream
        // packet, so nothing can be applied ahead of the identity baseline.
        var sent = sink.For(1);
        TestAssert.HasCount(3, sent);
        var begin = (AuthoritySnapshotBeginPacket)sent[0];
        TestAssert.AreEqual(1L, begin.SubscriptionEpoch);
        TestAssert.AreEqual(1L, begin.ChunkCount, "A small scope fits one chunk.");
        TestAssert.AreEqual(AuthoritySnapshotCodec.Hash(ReassembleSnapshot(sink, 1)), begin.SnapshotHash);

        // The frozen baseline is a full statement: identity and the member's state.
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryDecode(ReassembleSnapshot(sink, 1), Epoch, Scope,
            out var snapshot, out var reject), reject.ToString());
        TestAssert.HasCount(1, snapshot.Members);
        TestAssert.AreEqual(key, snapshot.Members[0].Key);
        CollectionAssert.AreEquivalent(new byte[] { 10, 0, 90 }, snapshot.Members[0].State);
        TestAssert.AreEqual(1L, snapshot.Members[0].Revision);

        // The ack releases the stream: a quiet backlog is just the resume marker.
        TestAssert.IsFalse(replicator.HasLiveSubscription(1, Scope));
        replicator.OnSnapshotAck(1, begin.BaselineId, 0, accepted: true);
        TestAssert.IsTrue(replicator.HasLiveSubscription(1, Scope));
        var after = sink.For(1).GetRange(3, sink.For(1).Count - 3);
        TestAssert.HasCount(1, after);
        var marker = State(after[0]);
        TestAssert.IsNotNull(marker);
        TestAssert.AreEqual(0, marker.RecordCount, "The marker is an empty batch on the stream.");
        TestAssert.AreEqual(1L, marker.Sequence, "The stream counts from one after the baseline.");
        TestAssert.AreEqual(begin.BaselineId, marker.DeclaredBaselineId);
        TestAssert.AreEqual(begin.SubscriptionEpoch, marker.SubscriptionEpoch);
        replicator.Unsubscribe(1, Scope);
        TestAssert.IsFalse(replicator.HasLiveSubscription(1, Scope));
    }

    [TestMethod]
    public void AStateChangeIsPublishedAsAbsoluteStateAndQuietFramesSendNothing()
    {
        var world = new FakeWorld();
        world.Add(7, 1, [100]);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);
        SubscribeAndAck(replicator, sink, 1, Scope);
        sink.Clear();

        world.States[ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1)] = [70];
        replicator.CaptureFrame(1000);
        TestAssert.HasCount(1, sink.Sent, "One changed field, one state packet.");
        var record = DecodeSingleState(State(sink.Sent[0]));
        CollectionAssert.AreEquivalent(new byte[] { 70 }, record.State);
        TestAssert.AreEqual(2L, record.Revision, "The baseline shipped the state at revision 1; the change is 2.");

        // The scan compares against the last published bytes — and the baseline is the last
        // published record — so a frame where nothing changed publishes nothing at all.
        sink.Clear();
        replicator.CaptureFrame(1010);
        TestAssert.IsEmpty(sink.Sent);
    }

    [TestMethod]
    public void ADeathIsPublishedOnceWithAFinalRevision()
    {
        var world = new FakeWorld();
        world.Add(7, 1, null);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);
        SubscribeAndAck(replicator, sink, 1, Scope);
        sink.Clear();

        world.Remove(7, 1);
        replicator.CaptureFrame(2000);
        var despawn = Lifecycle(sink.Sent[0]);
        TestAssert.IsNotNull(despawn);
        TestAssert.AreEqual(1, despawn.RecordCount);
        var record = DecodeSingleLifecycle(despawn);
        TestAssert.AreEqual(AuthorityLifecycleOp.Despawn, record.Op);
        TestAssert.AreEqual(2L, record.Revision, "The death carries the final revision of the generation.");

        sink.Clear();
        replicator.CaptureFrame(2010);
        TestAssert.IsEmpty(sink.Sent, "A death is never republished; the tombstone is the client's fact now.");
    }

    [TestMethod]
    public void ARememberedDeathIsPublishedAsADeathAndASilentRemovalIsNot()
    {
        // Both members leave the pool in the same frame, but only one of them died: vanilla's Kill
        // path and its silent removal path both empty the pool, and only the first may be presented
        // on a client — the other is a unit that went home.
        var world = new FakeWorld();
        world.Add(7, 1, null);
        world.Add(8, 1, null);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);
        SubscribeAndAck(replicator, sink, 1, Scope);
        sink.Clear();

        world.Kill(7, 1);
        world.Remove(7, 1);
        world.Remove(8, 1);
        replicator.CaptureFrame(5000);

        var packet = Lifecycle(sink.Sent[0]);
        TestAssert.IsNotNull(packet);
        TestAssert.AreEqual(2, packet.RecordCount);
        var context = new AuthoritySessionContext(true, Epoch, default, 0, true);
        TestAssert.IsTrue(AuthorityLifecycleCodec.TryDecode(packet.Data, 0, packet.Data.Length,
            packet.RecordCount, context, out var records, out var reject), reject.ToString());
        var byNative = new Dictionary<int, AuthorityLifecycleRecord>();
        foreach (var record in records) byNative[record.Key.NativeId] = record;
        TestAssert.AreEqual(AuthorityLifecycleOp.Death, byNative[7].Op);
        TestAssert.AreEqual(AuthorityLifecycleOp.Despawn, byNative[8].Op);
    }

    [TestMethod]
    public void ARecycledSlotIsANewGenerationNeverAResurrection()
    {
        var world = new FakeWorld();
        world.Add(7, 1, null);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);
        SubscribeAndAck(replicator, sink, 1, Scope);
        sink.Clear();

        world.Remove(7, 1);
        world.Add(7, 2, [5]);
        replicator.CaptureFrame(3000);

        // The frame publishes spawn, then the new object's state, then the old death — in that
        // order, as three packets on one stream.
        TestAssert.HasCount(3, sink.Sent);
        var spawn = DecodeLifecycleRecord(Lifecycle(sink.Sent[0]), 0);
        TestAssert.AreEqual(AuthorityLifecycleOp.Spawn, spawn.Op);
        TestAssert.AreEqual(2L, spawn.Key.Generation);
        CollectionAssert.AreEquivalent(new byte[] { 5 }, DecodeSingleState(State(sink.Sent[1])).State);
        var despawn = DecodeLifecycleRecord(Lifecycle(sink.Sent[2]), 0);
        TestAssert.AreEqual(AuthorityLifecycleOp.Despawn, despawn.Op);
        TestAssert.AreEqual(1L, despawn.Key.Generation);
        TestAssert.AreNotEqual(spawn.Key, despawn.Key,
            "The recycled slot is a different key; the old generation's history is not merged into it.");
    }

    [TestMethod]
    public void AFrameWhereTheScopeCannotBeReadPublishesNoDeaths()
    {
        // TryReadMembers false means "cannot read now". Treating that as an empty scope would
        // publish a wave of fake deaths, so the frame must skip the scope instead.
        var world = new FakeWorld();
        world.Add(7, 1, null);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);
        SubscribeAndAck(replicator, sink, 1, Scope);
        sink.Clear();

        world.MembersReadable = false;
        replicator.CaptureFrame(4000);
        TestAssert.IsEmpty(sink.Sent);
        TestAssert.AreEqual(1L, replicator.ScopesSkipped);
        TestAssert.AreEqual(1, replicator.Revisions.Count, "No death was minted for the unreadable scope.");

        world.MembersReadable = true;
        replicator.CaptureFrame(4010);
        TestAssert.IsEmpty(sink.Sent, "Recovering the view must not find anything to publish either.");
    }

    [TestMethod]
    public void AResubscribingSubscriberStartsANewBaselineAndANewEpoch()
    {
        var world = new FakeWorld();
        world.Add(7, 1, [1]);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);
        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        var firstBaseline = SubscribeAndAck(replicator, sink, 1, Scope);
        var firstEpoch = ((AuthoritySnapshotBeginPacket)sink.For(1)[0]).SubscriptionEpoch;

        replicator.Unsubscribe(1, Scope);
        world.States[key] = [42];
        replicator.CaptureFrame(6000);
        TestAssert.HasCount(4, sink.For(1),
            "An unsubscribed scope delivers nothing beyond its acked conversation (control plus marker).");

        // A re-subscription is a fresh subscription: a new baseline with a new epoch, not a
        // continuation the old stream's tail could masquerade inside.
        replicator.Subscribe(1, Scope);
        var control = sink.For(1).GetRange(4, sink.For(1).Count - 4);
        var begin = (AuthoritySnapshotBeginPacket)control[0];
        TestAssert.AreEqual(firstBaseline + 1, begin.BaselineId);
        TestAssert.AreEqual(firstEpoch + 1, begin.SubscriptionEpoch);
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryDecode(ReassembleSnapshot(sink, 1), Epoch, Scope,
            out var snapshot, out _));
        CollectionAssert.AreEquivalent(new byte[] { 42 }, snapshot.Members[0].State,
            "The subscriber receives a full statement of the world, not a stale patchwork.");

        replicator.OnSnapshotAck(1, begin.BaselineId, 0, accepted: true);
        var marker = (AuthorityWorldStatePacket)sink.For(1)[sink.For(1).Count - 1];
        TestAssert.AreEqual(0, marker.RecordCount);
        TestAssert.AreEqual(1L, marker.Sequence, "The new stream counts from one under the new epoch.");
    }

    [TestMethod]
    public void ASecondSubscriberGetsItsOwnBaselineAndItsOwnStream()
    {
        var world = new FakeWorld();
        world.Add(7, 1, [1]);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);
        var firstBaseline = SubscribeAndAck(replicator, sink, 1, Scope);
        var firstCount = sink.For(1).Count;

        // The second subscriber's conversation does not disturb the first one's stream.
        var secondBaseline = SubscribeAndAck(replicator, sink, 2, Scope);
        TestAssert.AreEqual(firstBaseline + 1, secondBaseline);
        TestAssert.HasCount(firstCount, sink.For(1),
            "The first subscriber's stream is untouched by the second one's conversation.");

        var second = sink.For(2);
        var begin = (AuthoritySnapshotBeginPacket)second[0];
        TestAssert.AreEqual(2L, begin.SubscriptionEpoch,
            "Subscription epochs are minted once per replicator and never reused.");
        TestAssert.AreEqual(secondBaseline, begin.BaselineId);
        var marker = (AuthorityWorldStatePacket)second[second.Count - 1];
        TestAssert.AreEqual(1L, marker.Sequence,
            "Each subscriber's stream starts and continues independently: the sequence is per subscriber and per scope.");
    }

    [TestMethod]
    public void ABacklogDeliversOnlyAfterTheAckAndNeverToAPendingSubscriber()
    {
        var world = new FakeWorld();
        world.Add(7, 1, [1]);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);
        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        var baseline = SubscribeAndAck(replicator, sink, 1, Scope);
        sink.Clear();

        // While the second subscriber installs its baseline, the scope keeps living; the first
        // subscriber sees the events, the second receives nothing ahead of its baseline.
        replicator.Subscribe(2, Scope);
        var pendingCount = sink.For(2).Count;
        world.States[key] = [77];
        replicator.CaptureFrame(6500);
        TestAssert.HasCount(pendingCount, sink.For(2), "A subscriber still installing receives no stream packet.");
        TestAssert.HasCount(1, sink.For(1));

        // The ack releases the backlog in log order, then the marker closes CatchingUp.
        replicator.OnSnapshotAck(2, baseline + 1, 0, accepted: true);
        var delivered = sink.For(2).GetRange(pendingCount, sink.For(2).Count - pendingCount);
        TestAssert.HasCount(2, delivered);
        var state = State(delivered[0]);
        CollectionAssert.AreEquivalent(new byte[] { 77 }, DecodeSingleState(state).State);
        TestAssert.AreEqual(1L, state.Sequence);
        TestAssert.AreEqual(baseline + 1, state.DeclaredBaselineId,
            "The backlog is an increment of the installed baseline.");
        TestAssert.AreEqual(2L, ((AuthorityWorldStatePacket)delivered[1]).Sequence, "The marker follows the backlog.");
    }

    [TestMethod]
    public void ARejectedAckStopsTheSubscriptionInsteadOfRetrying()
    {
        var world = new FakeWorld();
        world.Add(7, 1, [1]);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);
        replicator.Subscribe(1, Scope);
        var begin = (AuthoritySnapshotBeginPacket)sink.For(1)[0];

        replicator.OnSnapshotAck(1, begin.BaselineId, 0, accepted: false);
        TestAssert.IsTrue(replicator.NeedsResync(1, Scope),
            "A refused baseline leaves the scope flagged; the recovery is a new subscription.");
        TestAssert.AreEqual(1L, replicator.AcksRejected);

        world.States[ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1)] = [9];
        replicator.CaptureFrame(7500);
        TestAssert.HasCount(3, sink.For(1),
            "A flagged subscriber receives nothing further — not a partial world, not a retry.");
    }

    [TestMethod]
    public void AnAckForNoPendingBaselineIsCounted()
    {
        var world = new FakeWorld();
        world.Add(7, 1, [1]);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);

        replicator.OnSnapshotAck(1, baselineId: 12345, 0, accepted: true);
        TestAssert.AreEqual(1L, replicator.AcksUnknown);
        TestAssert.IsEmpty(sink.Sent, "An unmatched ack releases no stream.");
    }

    [TestMethod]
    public void LogReclamationIsBoundToBaselineCoverage()
    {
        var world = new FakeWorld();
        world.Add(7, 1, [1]);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);
        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        var baseline = SubscribeAndAck(replicator, sink, 1, Scope);

        // Subscriber 2 is still installing: the log must retain everything after its cutoff,
        // because that is exactly the backlog its ack will ask for.
        replicator.Subscribe(2, Scope);
        for (var frame = 0; frame < 5; frame++)
        {
            world.States[key] = [(byte)(frame + 2)];
            replicator.CaptureFrame(8000 + frame);
        }
        TestAssert.AreEqual(0L, replicator.EventsTrimmed,
            "Events a pending subscriber still needs are never reclaimed early.");

        // Once both subscribers are covered — delivered, or covered by an acked baseline — the log
        // reclaims deterministically.
        replicator.OnSnapshotAck(2, baseline + 1, 0, accepted: true);
        world.States[key] = [99];
        replicator.CaptureFrame(9000);
        TestAssert.IsGreaterThan(0,
            replicator.EventsTrimmed, "The cutoff the acks declare is what releases the log, not a timer and not only capacity.");
    }

    [TestMethod]
    public void ADisconnectedSubscriberIsRemovedFromEveryScope()
    {
        var world = new FakeWorld();
        world.Add(7, 1, [1]);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);
        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        SubscribeAndAck(replicator, sink, 1, Scope);
        replicator.RemoveSubscriber(1);

        world.States[key] = [9];
        replicator.CaptureFrame(7000);
        TestAssert.HasCount(4, sink.For(1),
            "The acked conversation (Begin, Chunk, Commit, marker) is all a removed subscriber ever gets.");
    }

    [TestMethod]
    public void AnOversizedCanonicalStateRefusesTheFrameInsteadOfDiverging()
    {
        // The record ceiling is a protocol bound. A field that cannot fit is a discovery the frame
        // must refuse loudly, not truncate silently.
        var world = new FakeWorld();
        world.Add(7, 1, new byte[AuthorityLimits.StateRecordMaxBytes]);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);
        SubscribeAndAck(replicator, sink, 1, Scope);
        sink.Clear();

        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        world.States[key] = new byte[AuthorityLimits.StateRecordMaxBytes + 1];
        replicator.CaptureFrame(8000);
        TestAssert.AreEqual(1L, replicator.OversizedStatesRefused);
        TestAssert.IsEmpty(sink.Sent, "A scope with an unpublishable field publishes nothing that frame.");

        world.States[key] = [1];
        replicator.CaptureFrame(8010);
        TestAssert.HasCount(1, sink.Sent, "Recovering publishes the change on the next frame.");
    }

    [TestMethod]
    public void ASubscriptionWhoseStateCannotShipIsRefusedNotHalfSent()
    {
        // A baseline is a whole statement or none of it. A member whose state cannot even be
        // encoded stops the subscription before any packet leaves.
        var world = new FakeWorld();
        world.Add(7, 1, new byte[AuthorityLimits.StateRecordMaxBytes + 1]);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);

        replicator.Subscribe(1, Scope);
        TestAssert.IsEmpty(sink.For(1), "No Begin, no chunk, no Commit: the conversation never started.");
        TestAssert.AreEqual(1L, replicator.SnapshotRefusedTotal);
    }

    private static AuthorityLifecycleRecord DecodeSingleLifecycle(AuthorityLifecyclePacket packet)
    {
        var context = new AuthoritySessionContext(true, Epoch,
            default, 0, true);
        TestAssert.IsTrue(AuthorityLifecycleCodec.TryDecode(packet.Data, 0, packet.Data.Length,
            packet.RecordCount, context, out var records, out var reject), reject.ToString());
        TestAssert.HasCount(1, records);
        return records[0];
    }

    private static AuthorityLifecycleRecord DecodeLifecycleRecord(AuthorityLifecyclePacket packet, int index)
    {
        var context = new AuthoritySessionContext(true, Epoch,
            default, 0, true);
        TestAssert.IsTrue(AuthorityLifecycleCodec.TryDecode(packet.Data, 0, packet.Data.Length,
            packet.RecordCount, context, out var records, out var reject), reject.ToString());
        return records[index];
    }

    private static AuthorityWorldStateRecord DecodeSingleState(AuthorityWorldStatePacket packet)
    {
        var context = new AuthoritySessionContext(true, Epoch,
            default, 0, true);
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryDecode(packet.Data, 0, packet.Data.Length,
            packet.RecordCount, context, out var records, out var reject), reject.ToString());
        TestAssert.HasCount(1, records);
        return records[0];
    }
    [TestMethod]
    public void InactiveScopesStopScanningAndResubscribeFromTheCurrentWorld()
    {
        var world = new FakeWorld();
        world.Add(1, 1, [1]);
        var sink = new RecordingSink();
        var replicator = Replicator(world, sink);
        replicator.Subscribe(2, Scope);
        replicator.CaptureFrame(1);
        replicator.Unsubscribe(2, Scope);
        var reads = world.MemberReads;
        for (var i = 2; i < 100; i++) replicator.CaptureFrame(i);
        TestAssert.AreEqual(reads, world.MemberReads);
        // A pending baseline was abandoned without an ACK. Resubscribe must replace it.
        world.Members[Scope].Clear();
        replicator.Subscribe(2, Scope);
        TestAssert.IsGreaterThan(reads, world.MemberReads);
        TestAssert.AreEqual(2, sink.Sent.OfType<AuthoritySnapshotBeginPacket>().Count());
    }
    [TestMethod]
    public void LargeMembershipDiffPublishesOnlyTheRemovedIdentities()
    {
        var world = new FakeWorld();
        for (var id = 1; id <= 12000; id++)
            world.Add(id, 1, [1]);
        var replicator = Replicator(world, new RecordingSink(), logCapacity: 50000);
        replicator.Subscribe(2, Scope);
        replicator.CaptureFrame(1);
        var before = replicator.EventsAppended;
        world.Members[Scope].RemoveRange(0, 6000);
        replicator.CaptureFrame(2);
        TestAssert.AreEqual(6000L, replicator.EventsAppended - before);
        replicator.CaptureFrame(3);
        TestAssert.AreEqual(6000L, replicator.EventsAppended - before);
    }
    [TestMethod]
    public void AnUnreadableSubscriptionDoesNotPublishAnInventedEmptyDigest()
    {
        var world = new FakeWorld { MembersReadable = false };
        var sink = new RecordingSink();
        var host = Replicator(world, sink);
        host.Subscribe(2, Scope);
        host.CaptureFrame(1);
        host.PublishDigests(1, 1);
        TestAssert.IsEmpty(sink.Sent);
    }
}

/// <summary>
/// a host captures nothing until a world view is registered, a
/// registered replicator runs at the capture seam, and snapshot acknowledgements reach the
/// replicator only at the frame boundary.
/// </summary>
[TestClass]
public class HostReplicationSessionTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0A0A0A0A0A0A0A0A, 0x0B0B0B0B0B0B0B0B);

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

    private sealed class RecordingSink : IReplicationSink
    {
        public readonly List<AuthorityEnvelopePacket> Sent = [];

        public void Send(ushort subscriberId, AuthorityEnvelopePacket packet) => Sent.Add(packet);

        public void Clear() => Sent.Clear();
    }

    [TestMethod]
    public void AHostWithoutAWorldViewCapturesNothing()
    {
        // No canonical source, no invented facts: the capture seam stays empty and the mode stays
        // unable to publish, which is the fail-closed state the design requires.
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);

        TestAssert.IsNull(session.HostReplicator);
        TestAssert.IsNull(session.Capture);
        TestAssert.IsNull(session.WorldReplica);
    }

    [TestMethod]
    public void ARegisteredWorldViewDrivesTheCaptureSeam()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        var world = new FakeWorld();
        var sink = new RecordingSink();

        TestAssert.IsTrue(session.RegisterHostReplication(world, sink));
        TestAssert.IsFalse(session.RegisterHostReplication(world, sink),
            "A second registration would strand two divergent replication states behind one seam.");
        TestAssert.IsNotNull(session.HostReplicator);
        TestAssert.IsNotNull(session.Capture);

        var scope = new ScopeKey(PoolKind.GroundEnemy, 101);
        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        world.Members.Add(key);
        world.States[key] = [1];
        session.HostReplicator.Subscribe(1, scope);
        TestAssert.HasCount(3, sink.Sent, "The snapshot conversation goes through the same sink as the frames.");

        // The ack is queued from the socket thread; before a frame boundary it releases nothing.
        TestAssert.IsTrue(session.NotifySnapshotAck(1, BeginBaselineId(sink), 0, accepted: true));
        TestAssert.HasCount(3, sink.Sent);
        session.OnFrameBoundary(9000);
        TestAssert.HasCount(4, sink.Sent, "The drained ack releases the resume marker.");
        sink.Clear();

        world.States[key] = [2];
        session.Capture.Capture(9010);
        TestAssert.HasCount(1, sink.Sent, "The frame patch's capture point drives the canonical scan.");

        session.Reset();
        TestAssert.IsNull(session.HostReplicator, "A new world must not inherit the old replication state.");
        TestAssert.IsNull(session.Capture);
    }

    [TestMethod]
    public void AFrameBoundaryAppliesQueuedAcksToTheReplicator()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        var world = new FakeWorld();
        var sink = new RecordingSink();
        session.RegisterHostReplication(world, sink);

        var scope = new ScopeKey(PoolKind.GroundEnemy, 101);
        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        world.Members.Add(key);
        world.States[key] = [1];
        session.HostReplicator.Subscribe(1, scope);
        var baseline = BeginBaselineId(sink);

        // The ack arrives on the socket thread; before the boundary it must not release anything.
        TestAssert.IsTrue(session.NotifySnapshotAck(1, baseline, 0, accepted: true));
        TestAssert.HasCount(3, sink.Sent, "The ack is queued, not applied: the stream is still held.");

        session.OnFrameBoundary(100);
        TestAssert.HasCount(4, sink.Sent, "At the boundary the ack releases the resume marker onto the stream.");
        var marker = (AuthorityWorldStatePacket)sink.Sent[3];
        TestAssert.AreEqual(0, marker.RecordCount);
        TestAssert.AreEqual(baseline, marker.DeclaredBaselineId);
    }

    [TestMethod]
    public void RegistrationIsRefusedOutsideAHostAuthorityWorld()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        TestAssert.IsFalse(session.RegisterHostReplication(new FakeWorld(), new RecordingSink()),
            "An uninitialized session must not create a replicator.");

        var client = new AuthoritySession(new AuthoritySessionState());
        client.BeginAuthorityWorld(Epoch, isHost: false);
        TestAssert.IsFalse(client.RegisterHostReplication(new FakeWorld(), new RecordingSink()),
            "A client is not a world authority and must not be able to become one by registration.");
    }

    [TestMethod]
    public void TheRegisteredDeathLedgerReachesTheScanThatPublishesRemovals()
    {
        // The ledger rides the same registration as the world view: the session commits the frame's
        // deaths into it before the scan, which is what lets a published removal say whether it was
        // a death at all.
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        var world = new FakeWorld();
        var ledger = new HostDeathLedger(Epoch);
        var sink = new RecordingSink();
        var scope = new ScopeKey(PoolKind.GroundEnemy, 101);
        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);

        TestAssert.IsTrue(session.RegisterHostReplication(world, sink, ledger));
        world.Members.Add(key);
        session.HostReplicator.Subscribe(1, scope);
        var baseline = BeginBaselineId(sink);
        TestAssert.IsTrue(session.NotifySnapshotAck(1, baseline, 0, accepted: true));
        session.OnFrameBoundary(100);
        sink.Clear();

        // What HostDeathCapture.Drain does at the boundary, one frame before the scan that sees the
        // empty slot: the death transaction for this exact key exists by then.
        TestAssert.IsTrue(ledger.OpenDeath(key, 110).IsFirst);
        world.Members.Remove(key);
        session.OnFrameComplete(110);

        AuthorityLifecyclePacket? packet = null;
        foreach (var sent in sink.Sent) if (sent is AuthorityLifecyclePacket lifecycle) packet = lifecycle;
        TestAssert.IsNotNull(packet, "The removal is published as a lifecycle record.");
        var context = new AuthoritySessionContext(true, Epoch, default, 0, true);
        TestAssert.IsTrue(AuthorityLifecycleCodec.TryDecode(packet.Data, 0, packet.Data.Length,
            packet.RecordCount, context, out var records, out var reject), reject.ToString());
        TestAssert.HasCount(1, records);
        TestAssert.AreEqual(AuthorityLifecycleOp.Death, records[0].Op,
            "The registered ledger is what turns the removal into a published death.");
    }

    private static long BeginBaselineId(RecordingSink sink)
    {
        foreach (var packet in sink.Sent)
        {
            if (packet is AuthoritySnapshotBeginPacket begin) return begin.BaselineId;
        }
        throw new InvalidOperationException("No snapshot Begin was sent.");
    }
}
