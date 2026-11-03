using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class SpaceEnemyInterestTest
{
    private static readonly AuthorityEpoch Epoch = new(361, 927);
    private static readonly ScopeKey Scope = new(PoolKind.SpaceEnemy, 0);
    private static readonly AuthoritySessionContext Context = new(true, Epoch, default, 0, false);

    private sealed class World : IHostWorldView, IHostDeathView
    {
        public readonly Dictionary<ObjectKey, byte[]> States = new();
        public readonly Dictionary<ObjectKey, ReplicationInterest> Interests = new();
        public readonly Dictionary<ObjectKey, int> Reads = new();
        public readonly HashSet<ObjectKey> Dead = new();
        public ObjectKey Add(int id, int star, SpaceEnemyKind kind = SpaceEnemyKind.SpaceUnit, long generation = 1)
        {
            var key = ObjectKey.Create(Epoch, PoolKind.SpaceEnemy, 0, id, generation);
            States[key] = [(byte)id]; Interests[key] = ReplicationInterest.SpaceEnemy(kind, star);
            Reads[key] = 0;
            return key;
        }
        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
        { members.AddRange(States.Keys); return true; }
        public bool TryReadState(ObjectKey key, out byte[] state)
        { Reads[key]++; return States.TryGetValue(key, out state!); }
        public bool IsDead(in ObjectKey key) => Dead.Contains(key);
    }

    private sealed class Rig : IReplicationSink
    {
        public readonly World World = new();
        public readonly Dictionary<ushort, int> Stars = new();
        public readonly Dictionary<ushort, ClientWorldReplica> Clients = new();
        public readonly List<(ushort Player, AuthorityEnvelopePacket Packet)> Sent = new();
        private readonly HashSet<(ushort Player, long Epoch)> superseded = new();
        public readonly HostWorldReplicator Host;
        public Rig()
        {
            Host = new HostWorldReplicator(Epoch, World, this, deathView: World)
            {
                MemberInterestFor = key => World.Interests[key],
                SubscriberStarProvider = id => Stars[id]
            };
        }
        public void Send(ushort subscriberId, AuthorityEnvelopePacket packet) => Sent.Add((subscriberId, packet));
        public AuthoritySnapshotBeginPacket Subscribe(ushort id, int star, bool ack = true, bool deliver = true)
        {
            Stars[id] = star;
            if (!Clients.TryGetValue(id, out var replica)) Clients[id] = replica = new ClientWorldReplica(Epoch);
            replica.BeginSpaceEnemySubscription();
            replica.Subscribe(Scope);
            Host.Subscribe(id, Scope, requestId: replica.SpaceEnemyRequestId);
            var begin = (AuthoritySnapshotBeginPacket)Sent.Last(p => p.Player == id && p.Packet is AuthoritySnapshotBeginPacket).Packet;
            if (!deliver) return begin;
            Pump();
            if (ack) { Host.OnSnapshotAck(id, begin.BaselineId, 0, true); Pump(); }
            return begin;
        }
        public void Restart(ushort id)
        { Clients[id].Unsubscribe(Scope); Host.Unsubscribe(id, Scope); }
        public void Members(ushort id, params ObjectKey[] keys) =>
            CollectionAssert.AreEquivalent(keys, Clients[id].Mirrors.Keys.ToArray());
        public void Pump()
        {
            foreach (var (id, packet) in Sent)
            {
                var replica = Clients[id];
                switch (packet)
                {
                    case AuthoritySnapshotBeginPacket begin:
                        // A newer subscription epoch is authoritative even when the host's recovery
                        // still carries the request id minted before the client's interest change.
                        var knownEpoch = replica.Versions.TryGetScope(Scope, out var before) ? before.SubscriptionEpoch : -1;
                        // A newer epoch overrides a stale request id only after this scope adopted a
                        // baseline: with no baseline installed there is no recovery to resume, so a
                        // delayed begin from a superseded subscription stays refused.
                        var newerEpoch = knownEpoch >= 0 && begin.SubscriptionEpoch > knownEpoch;
                        var acceptable = begin.RequestId == replica.SpaceEnemyRequestId || newerEpoch && knownEpoch > 0;
                        TestAssert.AreEqual(acceptable, replica.BeginSnapshot(Scope, begin.SubscriptionEpoch, begin.BaselineId,
                            begin.ChunkCount, begin.TotalBytes, begin.SnapshotHash, begin.RequestId));
                        if (!acceptable) superseded.Add((id, begin.SubscriptionEpoch));
                        break;
                    case AuthoritySnapshotChunkPacket chunk:
                        TestAssert.AreEqual(!superseded.Contains((id, chunk.SubscriptionEpoch)), replica.ReceiveSnapshotChunk(Scope,
                            chunk.SubscriptionEpoch, chunk.BaselineId, chunk.ChunkIndex, chunk.Data));
                        break;
                    case AuthoritySnapshotCommitPacket commit:
                        TestAssert.AreEqual(!superseded.Contains((id, commit.SubscriptionEpoch)), replica.CommitSnapshot(Scope,
                            commit.SubscriptionEpoch, commit.BaselineId, commit.ChunkCount, commit.SnapshotHash));
                        break;
                    case AuthorityWorldStatePacket state:
                        TestAssert.IsTrue(AuthorityWorldStateCodec.TryDecode(state.Data, 0, state.Data.Length,
                            state.RecordCount, Context, out var records, out _));
                        TestAssert.IsTrue(replica.ApplyWorldState(Scope, state.SubscriptionEpoch, state.Sequence,
                            state.DeclaredBaselineId, records));
                        break;
                    case AuthorityLifecyclePacket lifecycle:
                        TestAssert.IsTrue(AuthorityLifecycleCodec.TryDecode(lifecycle.Data, 0, lifecycle.Data.Length,
                            lifecycle.RecordCount, Context, out var changes, out _));
                        TestAssert.IsTrue(replica.ApplyLifecycle(Scope, lifecycle.SubscriptionEpoch, lifecycle.Sequence, changes));
                        break;
                    case AuthorityScopeDigestPacket digest:
                        TestAssert.IsTrue(replica.ReceiveScopeDigest(Scope, digest.SubscriptionEpoch, digest.HostTick,
                            digest.DeclaredStreamSequence, digest.BaselineId, digest.MemberCount, digest.Digest));
                        break;
                }
            }
            Sent.Clear();
        }
    }

    [TestMethod]
    public void DistantBuildingsAndRelaysAreExcludedWhileTindersRemainGlobal()
    {
        var rig = new Rig(); var local = rig.World.Add(1, 1);
        var remote = new List<ObjectKey>();
        foreach (var kind in new[] { SpaceEnemyKind.SpaceCore, SpaceEnemyKind.SpaceNode,
                     SpaceEnemyKind.SpaceConnector, SpaceEnemyKind.SpaceReplicator, SpaceEnemyKind.SpaceGamma,
                     SpaceEnemyKind.SpaceTurret, SpaceEnemyKind.SpaceRelay })
            remote.Add(rig.World.Add(remote.Count + 2, 2, kind));
        var tinder = rig.World.Add(20, 2, SpaceEnemyKind.SpaceTinder);
        rig.Subscribe(1, 1); rig.Members(1, local, tinder);
        foreach (var key in remote) TestAssert.AreEqual(0, rig.World.Reads[key]);
        rig.Restart(1); rig.Subscribe(1, 0); rig.Members(1, tinder);
        rig.Restart(1); rig.Subscribe(1, 2); rig.Members(1, remote.Append(tinder).ToArray());
        rig.Host.PublishDigests(1, 1); rig.Pump();
        TestAssert.AreEqual(1L, rig.Clients[1].DigestsMatchedTotal);
    }

    [TestMethod]
    public void AFilteredScopeResyncUsesANewRequestAndReturnsToLiveWithoutAcceptingThePreviousBegin()
    {
        var rig = new Rig(); var a = rig.World.Add(1, 1); rig.World.Add(2, 2);
        var previous = rig.Subscribe(1, 1);
        rig.Host.PublishDigests(1, 1); rig.Pump();
        var replica = rig.Clients[1];
        TestAssert.AreEqual(previous.RequestId, replica.SpaceEnemyRequestId, "Digest checks do not start subscriptions.");
        TestAssert.IsTrue(replica.RequestResync(Scope, ScopeRecoveryReason.BaselineRefused));
        TestAssert.IsGreaterThan(previous.RequestId, replica.SpaceEnemyRequestId);
        TestAssert.IsFalse(replica.BeginSnapshot(Scope, previous.SubscriptionEpoch, previous.BaselineId,
            previous.ChunkCount, previous.TotalBytes, previous.SnapshotHash, previous.RequestId));
        rig.World.States[a] = [77];
        TestAssert.IsTrue(rig.Host.RequestResync(1, Scope, replica.SpaceEnemyRequestId));
        var current = (AuthoritySnapshotBeginPacket)rig.Sent.First(p => p.Packet is AuthoritySnapshotBeginPacket).Packet;
        rig.Pump(); rig.Host.OnSnapshotAck(1, current.BaselineId, 0, true); rig.Pump();
        rig.Members(1, a);
        CollectionAssert.AreEqual(new byte[] { 77 }, replica.Mirrors[a].CanonicalState);
        replica.Versions.TryGetScope(Scope, out var version);
        TestAssert.AreEqual(SubscriptionPhase.Live, version.Phase);
    }

    [TestMethod]
    public void DelayedSnapshotBeginsCannotReplaceTheLatestRequestAfterAnAToBToATrip()
    {
        var rig = new Rig(); var a = rig.World.Add(1, 1); rig.World.Add(2, 2);
        var tinder = rig.World.Add(3, 2, SpaceEnemyKind.SpaceTinder);
        var first = rig.Subscribe(1, 1, ack: false, deliver: false);
        rig.Restart(1); rig.Subscribe(1, 2, ack: false, deliver: false);
        rig.World.States[a] = [99];
        rig.Restart(1); var latest = rig.Subscribe(1, 1);
        TestAssert.IsGreaterThan(first.RequestId, latest.RequestId);
        rig.Members(1, a, tinder);
        CollectionAssert.AreEqual(new byte[] { 99 }, rig.Clients[1].Mirrors[a].CanonicalState);
        rig.Clients[1].Versions.TryGetScope(Scope, out var version);
        TestAssert.AreEqual(SubscriptionPhase.Live, version.Phase);
        TestAssert.AreEqual(1L, rig.Clients[1].SnapshotsInstalledTotal);
        TestAssert.AreEqual(0L, rig.Clients[1].ResyncsRequestedTotal);
    }

    [TestMethod]
    public void HostRecoveryWithAStaleRequestIdStillInstallsItsNewerBaseline()
    {
        var rig = new Rig(); var a = rig.World.Add(1, 1);
        var first = rig.Subscribe(1, 1);
        // The client mints a new interest token before the host has started a baseline for it.
        rig.Clients[1].BeginSpaceEnemySubscription();
        // The host recovers from a gap using the cursor's previous token: the baseline carries the
        // stale id but a newer subscription epoch, so it must not be refused into a no-staging loop.
        TestAssert.IsTrue(rig.Host.RequestResync(1, Scope, first.RequestId));
        rig.World.States[a] = [88];
        rig.Host.CaptureFrame(1);
        var begin = (AuthoritySnapshotBeginPacket)rig.Sent.First(p => p.Packet is AuthoritySnapshotBeginPacket).Packet;
        TestAssert.AreEqual(first.RequestId, begin.RequestId);
        rig.Pump();
        rig.Host.OnSnapshotAck(1, begin.BaselineId, 0, true);
        rig.Pump();
        CollectionAssert.AreEqual(new byte[] { 88 }, rig.Clients[1].Mirrors[a].CanonicalState);
        rig.Clients[1].Versions.TryGetScope(Scope, out var version);
        TestAssert.AreEqual(SubscriptionPhase.Live, version.Phase);
        TestAssert.AreEqual(0L, rig.Clients[1].ResyncsRequestedTotal);
    }

    [TestMethod]
    public void TwoOccupiedStarsReceiveDifferentBaselinesAndStreamsWithMatchingDigests()
    {
        var rig = new Rig();
        var a = rig.World.Add(1, 1); var b = rig.World.Add(2, 2); var distant = rig.World.Add(3, 3);
        var relay = rig.World.Add(4, 2, SpaceEnemyKind.SpaceRelay);
        var tinder = rig.World.Add(5, 3, SpaceEnemyKind.SpaceTinder);
        rig.Subscribe(1, 1); rig.Members(1, a, tinder);
        TestAssert.AreEqual(0, rig.World.Reads[b]); TestAssert.AreEqual(0, rig.World.Reads[distant]);
        rig.Subscribe(2, 2); rig.Members(2, b, relay, tinder);
        foreach (var key in rig.World.States.Keys.ToArray()) rig.World.States[key] = [71, (byte)key.NativeId];
        rig.Host.CaptureFrame(1); rig.Pump();
        rig.Host.PublishDigests(1, 1); rig.Pump();
        rig.Members(1, a, tinder); rig.Members(2, b, relay, tinder);
        foreach (var replica in rig.Clients.Values)
        {
            TestAssert.AreEqual(1L, replica.DigestsMatchedTotal);
            TestAssert.AreEqual(0L, replica.DigestsMismatchedTotal);
            TestAssert.AreEqual(0L, replica.ResyncsRequestedTotal);
        }
        TestAssert.AreEqual(0, rig.World.Reads[distant]);
        CollectionAssert.AreEqual(new byte[] { 71, 1 }, rig.Clients[1].Mirrors[a].CanonicalState);
        CollectionAssert.AreEqual(new byte[] { 71, 2 }, rig.Clients[2].Mirrors[b].CanonicalState);
    }

    [TestMethod]
    public void ViewingAnotherStarAddsItsDetailsAndClosingItDoesNotTombstoneThem()
    {
        var rig = new Rig(); var local = rig.World.Add(1, 1);
        var relay = rig.World.Add(2, 2, SpaceEnemyKind.SpaceRelay);
        var tinder = rig.World.Add(3, 3, SpaceEnemyKind.SpaceTinder);
        var views = new Dictionary<ushort, int>();
        rig.Host.SubscriberViewedStarProvider = id => views.TryGetValue(id, out var star) ? star : 0;
        rig.Subscribe(1, 1); rig.Members(1, local, tinder);
        void View(int star, long tick)
        {
            views[1] = star; rig.Host.CaptureFrame(tick);
            var begin = (AuthoritySnapshotBeginPacket)rig.Sent.Single(p => p.Packet is AuthoritySnapshotBeginPacket).Packet;
            rig.Pump(); rig.Host.OnSnapshotAck(1, begin.BaselineId, 0, true); rig.Pump();
            rig.Host.PublishDigests(tick, 1); rig.Pump();
        }
        View(2, 1); rig.Members(1, local, relay, tinder);
        View(0, 2); rig.Members(1, local, tinder);
        View(2, 3); rig.Members(1, local, relay, tinder);
        TestAssert.AreEqual(0L, rig.Clients[1].TombstoneRefusedTotal);
        TestAssert.AreEqual(0L, rig.Clients[1].DigestsMismatchedTotal);
    }

    [TestMethod]
    public void WarningAndNavigationExceptionsEnterAndLeaveWithoutChangingNativeGeneration()
    {
        var rig = new Rig(); var local = rig.World.Add(1, 1);
        var relay = rig.World.Add(2, 2, SpaceEnemyKind.SpaceRelay);
        rig.Subscribe(1, 1);
        rig.World.Interests[relay] = default;
        rig.Host.RefreshMemberInterest(relay); rig.Host.CaptureFrame(1);
        var begin = (AuthoritySnapshotBeginPacket)rig.Sent.Single(p => p.Packet is AuthoritySnapshotBeginPacket).Packet;
        rig.Pump(); rig.Host.OnSnapshotAck(1, begin.BaselineId, 0, true); rig.Pump(); rig.Members(1, local, relay);
        rig.World.Interests[relay] = ReplicationInterest.InStar(2);
        rig.Host.RefreshMemberInterest(relay); rig.Host.CaptureFrame(2);
        begin = (AuthoritySnapshotBeginPacket)rig.Sent.Single(p => p.Packet is AuthoritySnapshotBeginPacket).Packet;
        rig.Pump(); rig.Host.OnSnapshotAck(1, begin.BaselineId, 0, true); rig.Pump(); rig.Members(1, local);
        TestAssert.AreEqual(0L, rig.Clients[1].TombstoneRefusedTotal);
    }

    [TestMethod]
    public void FilteredChangesConsumeNoStreamSequenceAndUnusedStatesAreNeverRead()
    {
        var rig = new Rig(); var local = rig.World.Add(1, 1); var remote = rig.World.Add(2, 2);
        rig.Subscribe(1, 1);
        for (var tick = 1; tick <= 8; tick++)
        {
            rig.World.States[remote] = [(byte)tick]; rig.Host.CaptureFrame(tick);
            TestAssert.HasCount(0, rig.Sent);
        }
        TestAssert.AreEqual(0, rig.World.Reads[remote]);
        rig.World.States[local] = [99]; rig.Host.CaptureFrame(9);
        TestAssert.AreEqual(2L, rig.Sent.Single().Packet.Sequence, "Only the initial resume marker preceded this state.");
        rig.Pump();
        rig.World.States.Remove(remote); rig.Host.CaptureFrame(10);
        TestAssert.HasCount(0, rig.Sent);
        rig.Host.PublishDigests(10, 1); rig.Pump();
        TestAssert.AreEqual(1L, rig.Clients[1].DigestsMatchedTotal);
    }

    [TestMethod]
    public void DeathRoutingSurvivesSlotReuseByAnEnemyInAnotherStar()
    {
        var rig = new Rig(); var old = rig.World.Add(2, 2);
        rig.Subscribe(1, 1); rig.Subscribe(2, 2);
        rig.World.States.Remove(old); rig.World.Dead.Add(old);
        var replacement = rig.World.Add(2, 1, generation: 2);
        rig.Host.CaptureFrame(1);
        var death = rig.Sent.Where(p => p.Player == 2).Select(p => p.Packet).OfType<AuthorityLifecyclePacket>().Single();
        TestAssert.IsTrue(AuthorityLifecycleCodec.TryDecode(death.Data, 0, death.Data.Length, death.RecordCount,
            Context, out var records, out _));
        TestAssert.AreEqual(AuthorityLifecycleOp.Death, records.Single().Op);
        TestAssert.AreEqual(old, records.Single().Key);
        rig.Pump(); rig.Members(1, replacement); rig.Members(2);
        rig.Host.PublishDigests(1, 1); rig.Pump();
        TestAssert.AreEqual(1L, rig.Clients[1].DigestsMatchedTotal);
        TestAssert.AreEqual(1L, rig.Clients[2].DigestsMatchedTotal);
    }

    [TestMethod]
    public void TravelAndSupersededBaselinesKeepOnlyGlobalActivityUntilTheNewStarIsReady()
    {
        var rig = new Rig(); var a = rig.World.Add(1, 1); var b = rig.World.Add(2, 2);
        var tinder = rig.World.Add(3, 2, SpaceEnemyKind.SpaceTinder);
        var first = rig.Subscribe(1, 1, ack: false);
        rig.Restart(1); rig.Subscribe(1, 0); rig.Members(1, tinder);
        rig.Restart(1); var second = rig.Subscribe(1, 2, ack: false);
        rig.World.States[a] = [99];
        rig.Restart(1); var latest = rig.Subscribe(1, 1);
        rig.Host.OnSnapshotAck(1, first.BaselineId, 0, true);
        rig.Host.OnSnapshotAck(1, second.BaselineId, 0, true);
        TestAssert.HasCount(0, rig.Sent);
        TestAssert.IsGreaterThan(second.SubscriptionEpoch, latest.SubscriptionEpoch);
        rig.Members(1, a, tinder);
        CollectionAssert.AreEqual(new byte[] { 99 }, rig.Clients[1].Mirrors[a].CanonicalState);
        TestAssert.IsFalse(rig.Clients[1].BeginSnapshot(Scope, first.SubscriptionEpoch, first.BaselineId,
            first.ChunkCount, first.TotalBytes, first.SnapshotHash));
        rig.Host.PublishDigests(1, 1); rig.Pump();
        TestAssert.AreEqual(1L, rig.Clients[1].DigestsMatchedTotal);
        rig.Host.RemoveSubscriber(1); rig.Clients.Remove(1);
        rig.Subscribe(1, 2); rig.Members(1, b, tinder);
    }
}
