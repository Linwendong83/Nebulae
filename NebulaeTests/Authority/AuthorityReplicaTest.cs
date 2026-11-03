
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// the client replica applies host facts in stream order and refuses everything the
/// version state cannot vouch for. A subscription is only complete once a baseline was installed:
/// the mirror is a whole-scope replacement at that point, and the live stream resumes after the
/// host's marker.
/// </summary>
[TestClass]
public class ClientWorldReplicaTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0707070707070707, 0x0707070707070707);
    private static readonly ScopeKey PlanetA = new(PoolKind.GroundEnemy, 101);
    private static readonly ScopeKey PlanetB = new(PoolKind.GroundEnemy, 102);

    private static ObjectKey Key(int nativeId, long generation) =>
        ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, nativeId, generation);

    private static ObjectKey KeyOn(ScopeKey scope, int nativeId, long generation) =>
        ObjectKey.Create(Epoch, scope.Kind, scope.Scope, nativeId, generation);

    private static List<AuthorityLifecycleRecord> Lifecycle(params AuthorityLifecycleRecord[] records) =>
        new List<AuthorityLifecycleRecord>(records);

    private static List<AuthorityWorldStateRecord> State(params AuthorityWorldStateRecord[] records) =>
        new List<AuthorityWorldStateRecord>(records);

    /// <summary>
    /// Drives the subscription conversation the way the host does: Subscribe, then Begin/Chunk/
    /// Commit with the frozen baseline. Returns the subscription epoch the scope now speaks.
    /// </summary>
    private static long InstallBaseline(ClientWorldReplica replica, ScopeKey scope, long subscriptionEpoch,
        long baselineId, params SnapshotMemberRecord[] members)
    {
        replica.Subscribe(scope);
        var snapshot = new FrozenScopeSnapshot(scope, baselineId, subscriptionEpoch, 0, 0,
            new List<SnapshotMemberRecord>(members));
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(snapshot, out var image));
        var hash = AuthoritySnapshotCodec.Hash(image);
        var chunkCount = AuthoritySnapshotCodec.ChunkCountFor(image.Length);
        TestAssert.IsTrue(replica.BeginSnapshot(scope, subscriptionEpoch, baselineId, chunkCount, image.Length, hash));
        for (var index = 0; index < chunkCount; index++)
        {
            var offset = (long)index * AuthorityLimits.ChunkMaxBytes;
            var length = (int)Math.Min(AuthorityLimits.ChunkMaxBytes, image.Length - offset);
            var chunk = new byte[length];
            Buffer.BlockCopy(image, (int)offset, chunk, 0, length);
            TestAssert.IsTrue(replica.ReceiveSnapshotChunk(scope, subscriptionEpoch, baselineId, index, chunk));
        }
        TestAssert.IsTrue(replica.CommitSnapshot(scope, subscriptionEpoch, baselineId, chunkCount, hash));
        return subscriptionEpoch;
    }

    [TestMethod]
    public void IdentityComesBeforeHitpoints()
    {
        var replica = new ClientWorldReplica(Epoch);
        var key = Key(7, 1);
        InstallBaseline(replica, PlanetA, subscriptionEpoch: 1, baselineId: 7,
            new SnapshotMemberRecord(key, 1, null));
        TestAssert.IsTrue(replica.Mirrors.ContainsKey(key), "The baseline establishes identity.");
        TestAssert.IsNull(replica.Mirrors[key].CanonicalState, "A member without published state ships no hitpoints.");

        TestAssert.IsTrue(replica.ApplyWorldState(PlanetA, 1, 1, 7,
            State(new AuthorityWorldStateRecord(key, 2, [80]))));
        CollectionAssert.AreEquivalent(new byte[] { 80 }, replica.Mirrors[key].CanonicalState);
        TestAssert.AreEqual(2L, replica.Mirrors[key].Revision);
    }

    [TestMethod]
    public void StateBeforeSpawnIsRefusedAndFlagsTheScope()
    {
        // "RejectedUnknownObject 不是可忽略的噪声": a state record for an object neither the
        // baseline nor a lifecycle message established means the spawn was lost. The record is
        // refused, the mirror stays empty, and the scope is flagged instead of being fed a guess.
        var replica = new ClientWorldReplica(Epoch);
        InstallBaseline(replica, PlanetA, 1, 7);

        TestAssert.IsFalse(replica.ApplyWorldState(PlanetA, 1, 1, 7,
            State(new AuthorityWorldStateRecord(Key(7, 1), 1, [80]))));
        TestAssert.AreEqual(1L, replica.UnknownObjectTotal);
        TestAssert.Contains(PlanetA, replica.ScopesNeedingResync);
        TestAssert.IsEmpty(replica.Mirrors);
    }

    [TestMethod]
    public void ALateStateCannotReviveADeadGeneration()
    {
        var replica = new ClientWorldReplica(Epoch);
        var key = Key(7, 1);
        InstallBaseline(replica, PlanetA, 1, 7, new SnapshotMemberRecord(key, 1, [50]));
        TestAssert.IsTrue(replica.ApplyLifecycle(PlanetA, 1, 1,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Despawn, key, 3))));
        TestAssert.IsFalse(replica.Mirrors.ContainsKey(key), "The death removes the mirror.");

        // Whatever its revision, a state record for the dead key must not bring it back. The
        // refusal is per-record (the rest of the batch is unaffected), so the counters and the
        // mirror are the observable truth.
        TestAssert.IsTrue(replica.ApplyWorldState(PlanetA, 1, 2, 7,
            State(new AuthorityWorldStateRecord(key, 9, [(byte)255]))),
            "The batch is processed; the refusal lands on the record, not the stream.");
        TestAssert.AreEqual(1L, replica.TombstoneRefusedTotal);
        TestAssert.IsFalse(replica.Mirrors.ContainsKey(key));
    }

    [TestMethod]
    public void APublishedDeathIsAnnouncedBeforeItsRemovalAPlainDespawnIsNot()
    {
        var replica = new ClientWorldReplica(Epoch);
        var dead = Key(7, 1);
        var gone = Key(8, 1);
        var observer = new RecordingMirrorObserver();
        replica.MirrorObserver = observer;
        InstallBaseline(replica, PlanetA, 1, 7,
            new SnapshotMemberRecord(dead, 1, [50]), new SnapshotMemberRecord(gone, 1, [50]));

        // A published death announces itself first — while the local shell is still live, which is
        // the only moment a presentation can read its pose — and the removal follows it.
        TestAssert.IsTrue(replica.ApplyLifecycle(PlanetA, 1, 1,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Death, dead, 3))));
        CollectionAssert.AreEqual(new[] { "died", "removed" }, observer.Calls);
        TestAssert.IsFalse(replica.Mirrors.ContainsKey(dead));

        // A plain despawn is a membership change only: a unit that went home must never be
        // presented as a death.
        observer.Calls.Clear();
        TestAssert.IsTrue(replica.ApplyLifecycle(PlanetA, 1, 2,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Despawn, gone, 4))));
        CollectionAssert.AreEqual(new[] { "removed" }, observer.Calls);

        // Leaving the scope releases the mirror too, and nothing about that is a death either.
        var elsewhere = new ClientWorldReplica(Epoch);
        var otherObserver = new RecordingMirrorObserver();
        elsewhere.MirrorObserver = otherObserver;
        InstallBaseline(elsewhere, PlanetA, 1, 7, new SnapshotMemberRecord(Key(9, 1), 1, null));
        elsewhere.Unsubscribe(PlanetA);
        CollectionAssert.AreEqual(new[] { "removed" }, otherObserver.Calls);
    }

    private sealed class RecordingMirrorObserver : IReplicaMirrorObserver, IReplicaDeathObserver
    {
        public readonly List<string> Calls = [];

        public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state) { }
        public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members) { }
        public void OnMemberRemoved(ScopeKey scope, in ObjectKey key) => Calls.Add("removed");
        public void OnMemberDied(ScopeKey scope, in ObjectKey key) => Calls.Add("died");
    }

    [TestMethod]
    public void ARecycledSlotSpawnsANewObjectRatherThanResurrectingTheOldOne()
    {
        var replica = new ClientWorldReplica(Epoch);
        var old = Key(7, 1);
        var fresh = Key(7, 2);
        InstallBaseline(replica, PlanetA, 1, 7, new SnapshotMemberRecord(old, 1, null));
        replica.ApplyLifecycle(PlanetA, 1, 1,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Despawn, old, 2)));

        TestAssert.IsTrue(replica.ApplyLifecycle(PlanetA, 1, 2,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Spawn, fresh, 3))));
        TestAssert.IsFalse(replica.Mirrors.ContainsKey(old));
        TestAssert.IsTrue(replica.Mirrors.ContainsKey(fresh),
            "The next generation of the slot is its own object with its own history.");
    }

    [TestMethod]
    public void ARepeatedStreamPositionIsIgnoredRatherThanReapplied()
    {
        var replica = new ClientWorldReplica(Epoch);
        var key = Key(7, 1);
        InstallBaseline(replica, PlanetA, 1, 7, new SnapshotMemberRecord(key, 1, [50]));
        TestAssert.IsTrue(replica.ApplyWorldState(PlanetA, 1, 1, 7,
            State(new AuthorityWorldStateRecord(key, 2, [50]))));

        // The same position arrives again carrying different bytes; neither the version state nor
        // the mirror may move.
        TestAssert.IsFalse(replica.ApplyWorldState(PlanetA, 1, 1, 7,
            State(new AuthorityWorldStateRecord(key, 2, [(byte)255]))));
        TestAssert.AreEqual(1L, replica.DuplicateTotal);
        CollectionAssert.AreEquivalent(new byte[] { 50 }, replica.Mirrors[key].CanonicalState);
    }

    [TestMethod]
    public void AStreamGapStopsApplicationAndFlagsTheScope()
    {
        // Applying past a gap would silently hide whatever the missing message carried, so the
        // scope stops receiving trust instead.
        var replica = new ClientWorldReplica(Epoch);
        var key = Key(7, 1);
        InstallBaseline(replica, PlanetA, 1, 7, new SnapshotMemberRecord(key, 1, null));
        replica.ApplyLifecycle(PlanetA, 1, 1,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Spawn, key, 1)));

        TestAssert.IsFalse(replica.ApplyWorldState(PlanetA, 1, 3, 7,
            State(new AuthorityWorldStateRecord(key, 2, [70]))));
        TestAssert.AreEqual(1L, replica.GapTotal);
        TestAssert.Contains(PlanetA, replica.ScopesNeedingResync);
        TestAssert.IsNull(replica.Mirrors[key].CanonicalState, "The record behind the gap was never applied.");
    }

    [TestMethod]
    public void ASwitchingPlanetsKeepsScopesSeparateAndRefusesOldTails()
    {
        // A→B→A: leaving A, fighting over B, and returning to A must not let B's objects into A's
        // membership or A's old tail into the fresh subscription. A re-subscription installs a new
        // baseline and restarts the stream under a new subscription epoch.
        var replica = new ClientWorldReplica(Epoch);
        var keyA = KeyOn(PlanetA, 7, 1);
        var keyB = KeyOn(PlanetB, 8, 1);

        InstallBaseline(replica, PlanetA, 1, 7, new SnapshotMemberRecord(keyA, 1, null));
        TestAssert.IsTrue(replica.ApplyLifecycle(PlanetA, 1, 1,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Spawn, keyA, 2))),
            "The first subscription's stream is live and advancing.");
        replica.Unsubscribe(PlanetA);
        TestAssert.IsFalse(replica.IsSubscribed(PlanetA));

        InstallBaseline(replica, PlanetB, 1, 7, new SnapshotMemberRecord(keyB, 1, null));
        TestAssert.IsFalse(replica.ApplyLifecycle(PlanetB, 1, 1,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Spawn, keyA, 2))),
            "A's object key cannot be spawned into B's membership.");

        InstallBaseline(replica, PlanetA, 2, 8, new SnapshotMemberRecord(keyA, 5, null));
        TestAssert.IsTrue(replica.IsSubscribed(PlanetA));
        TestAssert.IsTrue(replica.Versions.TryGetScope(PlanetA, out var scopeA));
        TestAssert.AreEqual(2L, scopeA.SubscriptionEpoch, "Returning to A speaks with the new subscription's epoch.");
        TestAssert.AreEqual(0L, scopeA.LastAppliedSequence, "The stream restarts with the baseline.");

        // The old subscription's tail is refused by its epoch before it can be mistaken for a
        // fresh position; the new stream counts from one.
        TestAssert.IsFalse(replica.ApplyLifecycle(PlanetA, 1, 2,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Spawn, keyA, 9))));
        TestAssert.AreEqual(1L, replica.SequenceDroppedTotal);
        TestAssert.IsTrue(replica.ApplyLifecycle(PlanetA, 2, 1,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Spawn, keyA, 6))));

        TestAssert.AreEqual(1L, scopeA.MemberCount, "B's object never joined A's membership.");
        TestAssert.IsTrue(replica.Versions.TryGetScope(PlanetB, out var scopeB));
        TestAssert.AreEqual(1L, scopeB.MemberCount);
    }

    [TestMethod]
    public void AnUnsubscribedScopeDropsPacketsWithoutApplyingThem()
    {
        var replica = new ClientWorldReplica(Epoch);
        var key = Key(7, 1);
        InstallBaseline(replica, PlanetA, 1, 7, new SnapshotMemberRecord(key, 1, [10]));
        replica.Unsubscribe(PlanetA);

        TestAssert.IsFalse(replica.ApplyWorldState(PlanetA, 1, 2, 7,
            State(new AuthorityWorldStateRecord(key, 2, [70]))));
        TestAssert.AreEqual(1L, replica.SequenceDroppedTotal);
        CollectionAssert.AreEquivalent(new byte[] { 10 }, replica.Mirrors[key].CanonicalState);
    }

    [TestMethod]
    public void LocalBindingsStaySeparateFromCanonicalState()
    {
        // Refreshing canonical state must not disturb the local component binding.
        var replica = new ClientWorldReplica(Epoch);
        var key = Key(7, 1);
        InstallBaseline(replica, PlanetA, 1, 7, new SnapshotMemberRecord(key, 1, [10]));
        TestAssert.IsTrue(replica.SetLocalComponentBinding(key, 77));

        TestAssert.IsTrue(replica.ApplyWorldState(PlanetA, 1, 1, 7,
            State(new AuthorityWorldStateRecord(key, 2, [60]))));
        TestAssert.AreEqual(77, replica.Mirrors[key].LocalComponentId);
        CollectionAssert.AreEquivalent(new byte[] { 60 }, replica.Mirrors[key].CanonicalState);

        TestAssert.IsFalse(replica.SetLocalComponentBinding(Key(9, 1), 1),
            "A binding needs a mirrored object; inventing one from a bare key is refused.");
    }

    [TestMethod]
    public void ADespawnOfAnUnknownKeyStillRecordsTheTombstone()
    {
        // The spawn may have been lost, but the death is a fact the replica can hold: recording it
        // is what stops a late packet for that key from doing anything at all.
        var replica = new ClientWorldReplica(Epoch);
        InstallBaseline(replica, PlanetA, 1, 7);
        var key = Key(7, 1);

        TestAssert.IsTrue(replica.ApplyLifecycle(PlanetA, 1, 1,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Despawn, key, 5))));
        TestAssert.IsTrue(replica.Versions.TryGetScope(PlanetA, out var scope));
        TestAssert.IsTrue(scope.IsTombstoned(key));

        TestAssert.IsFalse(replica.ApplyLifecycle(PlanetA, 1, 2,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Spawn, key, 1))),
            "Spawning the exact key a tombstone holds is a protocol violation, not a resurrection.");
        TestAssert.AreEqual(1L, replica.TombstoneRefusedTotal);
        TestAssert.IsEmpty(replica.Mirrors);
    }

    [TestMethod]
    public void ADecliningBaselineIsRefusedRatherThanAppliedToADifferentWorld()
    {
        var replica = new ClientWorldReplica(Epoch);
        var key = Key(7, 1);
        InstallBaseline(replica, PlanetA, 1, 7, new SnapshotMemberRecord(key, 1, null));

        // Anything declaring a baseline other than the installed one is describing another world.
        TestAssert.IsFalse(replica.ApplyWorldState(PlanetA, 1, 1, declaredBaselineId: 42,
            State(new AuthorityWorldStateRecord(key, 2, [70]))));
        TestAssert.AreEqual(1L, replica.BaselineRefusedTotal);
        TestAssert.Contains(PlanetA, replica.ScopesNeedingResync);
    }

    [TestMethod]
    public void ALiveStreamBeforeItsBaselineIsAProtocolViolation()
    {
        // The host must not stream live facts before the subscriber acked the baseline. Receiving
        // one anyway is refused and flags the scope instead of applying a half-built world.
        var replica = new ClientWorldReplica(Epoch);
        var key = Key(7, 1);
        replica.Subscribe(PlanetA);
        TestAssert.IsTrue(replica.Versions.TryGetScope(PlanetA, out var state));
        TestAssert.AreEqual(SubscriptionPhase.Snapshotting, state.Phase);

        TestAssert.IsFalse(replica.ApplyLifecycle(PlanetA, 1, 1,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Spawn, key, 1))));
        TestAssert.AreEqual(1L, replica.StreamBeforeBaselineTotal);
        TestAssert.Contains(PlanetA, replica.ScopesNeedingResync);
        TestAssert.IsEmpty(replica.Mirrors);
    }

    [TestMethod]
    public void ASupersededSubscriptionEpochIsRefused()
    {
        var replica = new ClientWorldReplica(Epoch);
        var key = Key(7, 1);
        InstallBaseline(replica, PlanetA, 1, 7, new SnapshotMemberRecord(key, 1, null));

        TestAssert.IsFalse(replica.ApplyLifecycle(PlanetA, subscriptionEpoch: 99, streamSequence: 1,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Spawn, key, 2))));
        TestAssert.AreEqual(1L, replica.SequenceDroppedTotal,
            "The packet belongs to a subscription this scope never adopted; it is dropped whole.");
    }

    [TestMethod]
    public void TheResumeMarkerMovesAScopeFromCatchingUpToLive()
    {
        var replica = new ClientWorldReplica(Epoch);
        var key = Key(7, 1);
        InstallBaseline(replica, PlanetA, 1, 7, new SnapshotMemberRecord(key, 1, null));
        TestAssert.IsTrue(replica.Versions.TryGetScope(PlanetA, out var state));
        TestAssert.AreEqual(SubscriptionPhase.CatchingUp, state.Phase);

        // The marker is an empty state batch: applying it means the backlog before it is complete.
        TestAssert.IsTrue(replica.ApplyWorldState(PlanetA, 1, 1, 7, State()));
        TestAssert.AreEqual(SubscriptionPhase.Live, state.Phase);
    }

    [TestMethod]
    public void ABaselineReplacesTheWholeMembership()
    {
        // The install is a replacement, not a patch: members the baseline does not name leave the
        // mirror, and the surviving member's state is the shipped one.
        var replica = new ClientWorldReplica(Epoch);
        var kept = Key(7, 1);
        var dropped = Key(8, 1);
        InstallBaseline(replica, PlanetA, 1, 7,
            new SnapshotMemberRecord(kept, 2, [40]),
            new SnapshotMemberRecord(dropped, 1, [10]));

        InstallBaseline(replica, PlanetA, 2, 8, new SnapshotMemberRecord(kept, 5, [30]));
        TestAssert.IsTrue(replica.Mirrors.ContainsKey(kept));
        TestAssert.IsFalse(replica.Mirrors.ContainsKey(dropped), "A member the baseline does not name is gone.");
        CollectionAssert.AreEquivalent(new byte[] { 30 }, replica.Mirrors[kept].CanonicalState);
        TestAssert.AreEqual(5L, replica.Mirrors[kept].Revision,
            "The baseline member speaks with the revision its state was shipped at.");

        // Delta-before-spawn is answered by the baseline: the member exists now.
        TestAssert.IsTrue(replica.ApplyWorldState(PlanetA, 2, 1, 8,
            State(new AuthorityWorldStateRecord(kept, 6, [20]))));
    }

    [TestMethod]
    public void ABaselineIsTheOnlyPathThatRevivesATombstone()
    {
        var replica = new ClientWorldReplica(Epoch);
        var key = Key(7, 1);
        InstallBaseline(replica, PlanetA, 1, 7, new SnapshotMemberRecord(key, 1, [10]));
        replica.ApplyLifecycle(PlanetA, 1, 1,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Despawn, key, 2)));
        TestAssert.IsTrue(replica.Versions.TryGetScope(PlanetA, out var scope));
        TestAssert.IsTrue(scope.IsTombstoned(key));

        // A late spawn cannot revive it…
        TestAssert.IsFalse(replica.ApplyLifecycle(PlanetA, 1, 2,
            Lifecycle(new AuthorityLifecycleRecord(AuthorityLifecycleOp.Spawn, key, 3))));
        // …but a fresh baseline that names the key alive is an explicit, whole-scope statement.
        InstallBaseline(replica, PlanetA, 2, 8, new SnapshotMemberRecord(key, 4, [70]));
        TestAssert.IsFalse(scope.IsTombstoned(key));
        CollectionAssert.AreEquivalent(new byte[] { 70 }, replica.Mirrors[key].CanonicalState);
    }

    [TestMethod]
    public void AnAckIsSentWhenASinkIsWiredAndCountedWhenItIsNot()
    {
        var acks = new List<AuthoritySnapshotAckPacket>();
        var replica = new ClientWorldReplica(Epoch) { SnapshotAckSink = new RecordingAckSink(acks) };
        InstallBaseline(replica, PlanetA, 1, 7);
        TestAssert.HasCount(1, acks);
        TestAssert.AreEqual(7L, acks[0].BaselineId);
        TestAssert.IsTrue(acks[0].Accepted);
        TestAssert.AreEqual(0L, replica.UnsentSnapshotAcksTotal);

        var sinkless = new ClientWorldReplica(Epoch);
        InstallBaseline(sinkless, PlanetA, 1, 7);
        TestAssert.AreEqual(1L, sinkless.UnsentSnapshotAcksTotal,
            "A missing ack sink strands the host in Snapshotting; the omission is counted, never silent.");
        TestAssert.AreEqual(SubscriptionPhase.CatchingUp,
            sinkless.Versions.TryGetScope(PlanetA, out var installed) ? installed.Phase : default,
            "The install itself does not depend on the ack reaching anywhere.");
    }

    [TestMethod]
    public void AChunkConversationRefusesBrokenBookkeeping()
    {
        var replica = new ClientWorldReplica(Epoch);
        var key = Key(7, 1);
        replica.Subscribe(PlanetA);
        var snapshot = new FrozenScopeSnapshot(PlanetA, 7, 1, 0, 0,
            new List<SnapshotMemberRecord> { new(key, 1, [10]) });
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(snapshot, out var image));
        var hash = AuthoritySnapshotCodec.Hash(image);
        var chunkCount = AuthoritySnapshotCodec.ChunkCountFor(image.Length);

        // A Begin whose bookkeeping cannot describe a real image is refused before allocation.
        TestAssert.IsFalse(replica.BeginSnapshot(PlanetA, 1, 7, chunkCount: 99, image.Length, hash));
        TestAssert.IsTrue(replica.BeginSnapshot(PlanetA, 1, 7, chunkCount, image.Length, hash));

        // A chunk for another baseline or another subscription does not enter the buffer.
        TestAssert.IsFalse(replica.ReceiveSnapshotChunk(PlanetA, 1, baselineId: 8, 0, image));
        TestAssert.IsFalse(replica.ReceiveSnapshotChunk(PlanetA, subscriptionEpoch: 5, 7, 0, image));

        // An identical repeat is a transport duplicate, not an error; the first call places it.
        TestAssert.IsTrue(replica.ReceiveSnapshotChunk(PlanetA, 1, 7, 0, image));
        TestAssert.IsTrue(replica.ReceiveSnapshotChunk(PlanetA, 1, 7, 0, image));
        TestAssert.AreEqual(1L, replica.Snapshots.DuplicateChunksTotal);

        // A Commit whose bookkeeping disagrees with the staged buffer is refused and discards it.
        TestAssert.IsFalse(replica.CommitSnapshot(PlanetA, 1, 7, chunkCount: chunkCount + 1, hash));
        TestAssert.AreEqual(SubscriptionPhase.Resyncing,
            replica.Versions.TryGetScope(PlanetA, out var phase) ? phase.Phase : default);
        TestAssert.IsEmpty(replica.Mirrors);

        // The retired conversation cannot return. A newer recovery attempt with a mismatching
        // commit hash is also discarded instead of installing a foreign image.
        TestAssert.IsFalse(replica.BeginSnapshot(PlanetA, 1, 7, chunkCount, image.Length, hash));
        snapshot = new FrozenScopeSnapshot(PlanetA, 8, 2, 0, 0,
            new List<SnapshotMemberRecord> { new(key, 1, [10]) });
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(snapshot, out image));
        hash = AuthoritySnapshotCodec.Hash(image);
        TestAssert.IsTrue(replica.BeginSnapshot(PlanetA, 2, 8, chunkCount, image.Length, hash));
        TestAssert.IsTrue(replica.ReceiveSnapshotChunk(PlanetA, 2, 8, 0, image));
        TestAssert.IsFalse(replica.CommitSnapshot(PlanetA, 2, 8, chunkCount, hash: 12345));
        TestAssert.IsFalse(replica.Snapshots.IsStaging(PlanetA));
        TestAssert.Contains(PlanetA, replica.ScopesNeedingResync);
        TestAssert.IsEmpty(replica.Mirrors);
    }

    private sealed class RecordingAckSink : ISnapshotAckSink
    {
        private readonly List<AuthoritySnapshotAckPacket> acks;

        public RecordingAckSink(List<AuthoritySnapshotAckPacket> acks) => this.acks = acks;

        public void Send(AuthoritySnapshotAckPacket packet) => acks.Add(packet);
    }
}

/// <summary>
/// queued replica messages are applied inside the apply window,
/// and only there — including the snapshot conversation that installs a baseline.
/// </summary>
[TestClass]
public class AuthorityReplicaWiringTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0C0C0C0C0C0C0C0C, 0x0D0D0D0D0D0D0D0D);
    private static readonly ScopeKey Scope = new(PoolKind.GroundEnemy, 101);

    private static AuthorityLifecyclePacket LifecyclePacket(ScopeKey scope, long subscriptionEpoch, long sequence,
        params AuthorityLifecycleRecord[] records)
    {
        TestAssert.IsTrue(AuthorityLifecycleCodec.TryEncode(records, out var data));
        var header = new AuthorityEnvelopeHeader(AuthorityFamily.Lifecycle, Epoch,
            connection: default, sequence, hostTick: 100, claimedPlayerId: 0, payloadLength: data.Length);
        var packet = AuthorityLifecyclePacket.Create(header, scope, records.Length, data);
        packet.SubscriptionEpoch = subscriptionEpoch;
        return packet;
    }

    private static AuthoritySession ClientSession()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: false);
        return session;
    }

    [TestMethod]
    public void AClientSessionOwnsAReplicaAndTheApplyWindowAdapter()
    {
        var session = ClientSession();

        TestAssert.IsNotNull(session.WorldReplica);
        TestAssert.IsNotNull(session.ReplicaApplier);
        TestAssert.IsNull(session.HostReplicator, "A client is not a world authority.");
    }

    [TestMethod]
    public void AQueuedLifecycleMessageAppliesAtTheFrameBoundary()
    {
        var session = ClientSession();
        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        InstallBaseline(session.WorldReplica, Scope, subscriptionEpoch: 1, baselineId: 7,
            new SnapshotMemberRecord(key, 1, null));

        var packet = LifecyclePacket(Scope, subscriptionEpoch: 1, sequence: 1,
            new AuthorityLifecycleRecord(AuthorityLifecycleOp.Spawn, key, 2));
        TestAssert.IsTrue(session.TryEnqueueReplicaMessage(packet,
            new ApplyScope(Scope, streamSequence: packet.Sequence, hostTick: packet.HostTick)));
        TestAssert.AreEqual(1L, session.WorldReplica.Mirrors[key].Revision,
            "The baseline member is in the mirror before the queue is drained.");

        session.OnFrameBoundary(100);
        TestAssert.AreEqual(2L, session.WorldReplica.Mirrors[key].Revision, "The spawn updated the mirror at the boundary.");
        TestAssert.AreEqual(1L, session.InboundApplied);
        TestAssert.AreEqual(1L, session.WorldReplica.AppliedTotal);
    }

    [TestMethod]
    public void AMessageWhoseScopeDoesNotMatchItsQueueingIsRefused()
    {
        // Defense in depth: the applier re-derives the scope from the packet and requires it to be
        // the scope the apply window was opened for.
        var session = ClientSession();
        var other = new ScopeKey(PoolKind.GroundEnemy, 102);
        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        InstallBaseline(session.WorldReplica, Scope, 1, 7, new SnapshotMemberRecord(key, 1, null));
        InstallBaseline(session.WorldReplica, other, 1, 7, new SnapshotMemberRecord(
            ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 102, 9, 1), 1, null));

        var packet = LifecyclePacket(Scope, 1, 1,
            new AuthorityLifecycleRecord(AuthorityLifecycleOp.Spawn, key, 2));
        session.TryEnqueueReplicaMessage(packet, new ApplyScope(other, streamSequence: packet.Sequence));

        session.OnFrameBoundary(100);
        TestAssert.AreEqual(1L, session.WorldReplica.Mirrors[key].Revision,
            "A packet must not ride in under another scope's apply window.");
        TestAssert.AreEqual(1L, session.InboundDropped);
    }

    [TestMethod]
    public void TheSnapshotConversationInstallsThroughTheSessionInbox()
    {
        // Begin/Chunk/Commit ride the same queue as every other replica message and are applied at
        // the frame boundary, inside the apply window — never on the socket thread.
        var session = ClientSession();
        session.WorldReplica.Subscribe(Scope);
        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        var snapshot = new FrozenScopeSnapshot(Scope, 7, 1, 0, 0,
            new List<SnapshotMemberRecord> { new(key, 1, [30]) });
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(snapshot, out var image));
        var hash = AuthoritySnapshotCodec.Hash(image);
        var chunkCount = AuthoritySnapshotCodec.ChunkCountFor(image.Length);

        var beginHeader = new AuthorityEnvelopeHeader(AuthorityFamily.SnapshotBegin, Epoch,
            default, 0, 100, 0, 0);
        session.TryEnqueueReplicaMessage(
            AuthoritySnapshotBeginPacket.Create(beginHeader, Scope, 7, 1, 0, image.Length, chunkCount, hash),
            new ApplyScope(Scope));
        var chunkHeader = new AuthorityEnvelopeHeader(AuthorityFamily.SnapshotChunk, Epoch,
            default, 0, 100, 0, image.Length);
        session.TryEnqueueReplicaMessage(
            AuthoritySnapshotChunkPacket.Create(chunkHeader, Scope, 7, 1, 0, image),
            new ApplyScope(Scope));
        var commitHeader = new AuthorityEnvelopeHeader(AuthorityFamily.SnapshotCommit, Epoch,
            default, 0, 100, 0, 0);
        session.TryEnqueueReplicaMessage(
            AuthoritySnapshotCommitPacket.Create(commitHeader, Scope, 7, 1, chunkCount, hash),
            new ApplyScope(Scope));

        TestAssert.IsEmpty(session.WorldReplica.Mirrors, "The socket path queued them; nothing applied yet.");
        session.OnFrameBoundary(100);

        TestAssert.IsTrue(session.WorldReplica.Mirrors.ContainsKey(key));
        CollectionAssert.AreEquivalent(new byte[] { 30 }, session.WorldReplica.Mirrors[key].CanonicalState);
        TestAssert.AreEqual(3L, session.InboundApplied);
        TestAssert.AreEqual(1L, session.WorldReplica.SnapshotsInstalledTotal);
    }

    [TestMethod]
    public void TheReplicaSurvivesAReset()
    {
        var session = ClientSession();
        session.WorldReplica.Subscribe(Scope);
        session.Reset();

        TestAssert.IsNull(session.WorldReplica);
        TestAssert.IsNull(session.ReplicaApplier);
    }

    private static long InstallBaseline(ClientWorldReplica replica, ScopeKey scope, long subscriptionEpoch,
        long baselineId, params SnapshotMemberRecord[] members)
    {
        replica.Subscribe(scope);
        var snapshot = new FrozenScopeSnapshot(scope, baselineId, subscriptionEpoch, 0, 0,
            new List<SnapshotMemberRecord>(members));
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(snapshot, out var image));
        var hash = AuthoritySnapshotCodec.Hash(image);
        var chunkCount = AuthoritySnapshotCodec.ChunkCountFor(image.Length);
        TestAssert.IsTrue(replica.BeginSnapshot(scope, subscriptionEpoch, baselineId, chunkCount, image.Length, hash));
        for (var index = 0; index < chunkCount; index++)
        {
            var offset = (long)index * AuthorityLimits.ChunkMaxBytes;
            var length = (int)Math.Min(AuthorityLimits.ChunkMaxBytes, image.Length - offset);
            var chunk = new byte[length];
            Buffer.BlockCopy(image, (int)offset, chunk, 0, length);
            TestAssert.IsTrue(replica.ReceiveSnapshotChunk(scope, subscriptionEpoch, baselineId, index, chunk));
        }
        return replica.CommitSnapshot(scope, subscriptionEpoch, baselineId, chunkCount, hash)
            ? subscriptionEpoch
            : throw new InvalidOperationException("baseline install failed");
    }
}
