
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// a host planet of ground enemies flows through the standard replicator and the
/// client's replica into a binding that rebuilds display-only shells. Verifies at the model level: full
/// lifecycle, parent-child inversion, slot reuse, scope rebuild
/// after losing the core — and never through a Kill path (the fake pools have no Kill to call).
/// </summary>
[TestClass]
public class GroundEnemyReplicationTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0A12080808080808, 0x0C12080808080808);
    private static readonly ScopeKey Scope = new(PoolKind.GroundEnemy, 101);

    private static ObjectKey Key(int nativeId, long generation) =>
        ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, nativeId, generation);

    private static GroundEnemyState BaseCore(int baseId) => new(
        GroundEnemyKind.GroundBase, true, false, 8100, 40, 1, 0, 0,
        101, 101, baseId, 5, 3, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 5000, 8000, 0, 0);

    private static GroundEnemyState Unit(int baseId, int hp = 500, byte unitBehavior = 0) => new(
        GroundEnemyKind.GroundUnit, true, false, 8130, 45, 2, 3, 0,
        101, 101, baseId, 0, 1, 10f, 0f, 20f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, hp, 900, 2, 0,
        unitBehavior: unitBehavior);

    private static byte[] Encoded(GroundEnemyState state)
    {
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(state, out var data));
        return data;
    }

    private sealed class FakeHostEnemies : IHostWorldView
    {
        public readonly List<ObjectKey> Members = [];
        public readonly Dictionary<ObjectKey, byte[]> States = [];

        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
        {
            members.AddRange(Members);
            return true;
        }

        public bool TryReadState(ObjectKey key, out byte[]? state) => States.TryGetValue(key, out state!);
    }

    private sealed class FakeClientPools : IGroundEnemyPools
    {
        public readonly Dictionary<int, GroundEnemyState> Shells = [];
        public int PureRemovals;
        public readonly List<int> Presented = [];
        public readonly List<(int Sp, int Max)> PresentedProgress = [];

        public bool EnemyExists(int enemyId) => Shells.ContainsKey(enemyId);
        public void CreateEnemyShell(int enemyId, in GroundEnemyState state) => Shells[enemyId] = state;
        public void WriteEnemyState(int enemyId, in GroundEnemyState state) => Shells[enemyId] = state;

        public void RemoveEnemyShell(int enemyId)
        {
            TestAssert.IsTrue(Shells.Remove(enemyId), "Pure removal releases a shell that exists; no Kill, no drops.");
            PureRemovals++;
        }

        public void PlayDeathPresentation(int enemyId, int builderSp, int builderSpMax)
        {
            TestAssert.IsTrue(Shells.ContainsKey(enemyId),
                "A death is presented while its shell is still live, before the removal that follows.");
            Presented.Add(enemyId);
            PresentedProgress.Add((builderSp, builderSpMax));
        }
    }

    private sealed class TestBindingObserver : IReplicaMirrorObserver, IReplicaDeathObserver
    {
        public readonly FakeClientPools Pools = new();
        public readonly GroundEnemyBinding Core;
        public int BaselinesInstalled;

        public TestBindingObserver() => Core = new GroundEnemyBinding(101, Pools, (key, _) => { });

        public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state) =>
            Core.ApplyState(key, state);

        public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
        {
            BaselinesInstalled++;
            Core.ReconcileBaseline(members);
        }

        public void OnMemberRemoved(ScopeKey scope, in ObjectKey key) => Core.RemoveMember(key);

        public void OnMemberDied(ScopeKey scope, in ObjectKey key) => Core.PlayDeath(key);
    }





    [TestMethod]
    public void AGroundLifecycleSurvivesTheFullConversation()
    {
        var enemies = new FakeHostEnemies();
        var core = Key(4, 1);
        var unitA = Key(9, 1);
        var lone = Key(12, 1);
        enemies.Members.AddRange([core, unitA, lone]);
        enemies.States[core] = Encoded(BaseCore(baseId: 2));
        enemies.States[unitA] = Encoded(Unit(baseId: 2));
        enemies.States[lone] = Encoded(Unit(baseId: 0, hp: 700));

        var hostSession = new AuthoritySession(new AuthoritySessionState());
        hostSession.BeginAuthorityWorld(Epoch, isHost: true);
        var clientSession = new AuthoritySession(new AuthoritySessionState());
        clientSession.BeginAuthorityWorld(Epoch, isHost: false);
        hostSession.RegisterHostReplication(enemies, new SinkToClientInbox(clientSession));

        var observer = new TestBindingObserver();
        clientSession.WorldReplica.MirrorObserver = observer;
        clientSession.WorldReplica.SnapshotAckSink = new AckToHost(hostSession);

        clientSession.WorldReplica.Subscribe(Scope);
        hostSession.HostReplicator.Subscribe(1, Scope);
        hostSession.OnFrameBoundary(100);
        clientSession.OnFrameBoundary(100);
        hostSession.OnFrameBoundary(110);
        clientSession.OnFrameBoundary(110);
        TestAssert.IsTrue(clientSession.WorldReplica.Versions.TryGetScope(Scope, out var clientScope));
        TestAssert.AreEqual(SubscriptionPhase.Live, clientScope.Phase);
        TestAssert.AreEqual(1, observer.BaselinesInstalled);
        TestAssert.IsTrue(observer.Pools.EnemyExists(4));
        TestAssert.IsTrue(observer.Pools.EnemyExists(9));
        TestAssert.AreEqual(500, observer.Pools.Shells[9].Hp);

        var rebuilt = Key(9, 2);
        enemies.Members.Remove(unitA);
        enemies.Members.Add(rebuilt);
        enemies.States[rebuilt] = Encoded(Unit(baseId: 2, hp: 880));
        enemies.States[lone] = Encoded(Unit(baseId: 0, hp: 100));
        hostSession.Capture.Capture(120);
        hostSession.OnFrameBoundary(120);
        clientSession.OnFrameBoundary(120);

        TestAssert.IsTrue(observer.Pools.EnemyExists(9));
        TestAssert.AreEqual(880, observer.Pools.Shells[9].Hp, "Same slot, new generation: fresh shell, never the dead one's HP.");
        TestAssert.AreEqual(0, observer.Pools.PureRemovals, "Slot reuse transfers ownership: the trailing despawn of the old generation must not delete the new shell (no Kill path exists in the fake pools by construction).");
        TestAssert.AreEqual(0, observer.Core.DeathsPresented,
            "The trailing despawn of a recycled slot presents nothing: the slot belongs to the new generation now.");
        TestAssert.AreEqual(100, observer.Pools.Shells[12].Hp);
    }

    [TestMethod]
    public void APublishedDeathIsPresentedWhileItsShellIsStillLive()
    {
        var observer = new TestBindingObserver();
        var unit = Key(9, 1);
        observer.Core.ReconcileBaseline(new List<SnapshotMemberRecord>
        {
            new(unit, 1, Encoded(Unit(baseId: 0)))
        });
        TestAssert.IsTrue(observer.Pools.EnemyExists(9));

        // The host published the death: the presentation runs while the shell is still bound, so it
        // can read the pose the last state record installed, and the removal follows it.
        observer.Core.PlayDeath(unit);
        TestAssert.AreEqual(1, observer.Core.DeathsPresented);
        CollectionAssert.AreEqual(new[] { 9 }, observer.Pools.Presented);
        observer.Core.RemoveMember(unit);
        TestAssert.IsFalse(observer.Pools.EnemyExists(9));
        TestAssert.AreEqual(1, observer.Pools.PureRemovals);
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(120, 400)]
    [DataRow(400, 400)]
    public void ABuildingDeathRetainsTheLatestProgressAndPresentsOnce(int sp, int max)
    {
        var observer = new TestBindingObserver();
        var key = Key(9, 1);
        GroundEnemyState Building(int progress, int maximum) => new(
            GroundEnemyKind.GroundBuilder, true, false, 8101, 41, 1, 0, 0,
            101, 101, 0, 3, 1, 10, 200, 20, 0, 0, 0, 1, 0, 0, 0, 500, 900, 0, 0,
            builderSp: progress, builderSpMax: maximum);
        observer.Core.ApplyState(key, Encoded(Building(1, 100)));
        observer.Core.ApplyState(key, Encoded(Building(sp, max)));
        observer.Core.PlayDeath(key);
        observer.Core.PlayDeath(key);
        TestAssert.AreEqual(1L, observer.Core.DeathsPresented);
        TestAssert.AreEqual((sp, max), observer.Pools.PresentedProgress.Single());
        observer.Core.RemoveMember(key);
        observer.Core.PlayDeath(key);
        TestAssert.HasCount(1, observer.Pools.Presented);
    }

    private static GroundEnemyState PersistentCore(bool destroyed, int progress = 400) => new(
        GroundEnemyKind.GroundBase, !destroyed, false, 8100, 40, 1, 0, destroyed ? (byte)0x80 : (byte)0,
        101, 101, 2, 1, 3, 0, 200, 0, 0, 0, 0, 1, 0, 0, 0,
        destroyed ? 0 : 5000, destroyed ? 0 : 8000, 0, 0,
        builderSp: destroyed ? 0 : progress, builderSpMax: 400);

    [TestMethod]
    [DataRow(120)]
    [DataRow(400)]
    public void APersistentCoreExplodesFromStateReplicationAndCanBeRebuiltAndKilledAgain(int progress)
    {
        var enemies = new FakeHostEnemies();
        var core = Key(4, 1);
        var child = Key(9, 1);
        enemies.Members.AddRange([core, child]);
        enemies.States[core] = Encoded(PersistentCore(false, progress));
        enemies.States[child] = Encoded(Unit(baseId: 2));
        using var host = new AuthoritySession(new AuthoritySessionState());
        using var client = new AuthoritySession(new AuthoritySessionState());
        host.BeginAuthorityWorld(Epoch, isHost: true);
        client.BeginAuthorityWorld(Epoch, isHost: false);
        host.RegisterHostReplication(enemies, new SinkToClientInbox(client));
        var observer = new TestBindingObserver();
        client.WorldReplica.MirrorObserver = observer;
        client.WorldReplica.SnapshotAckSink = new AckToHost(host);
        client.WorldReplica.Subscribe(Scope);
        host.HostReplicator.Subscribe(1, Scope);
        void Frame(long tick) { host.OnFrameBoundary(tick); host.OnFrameComplete(tick); client.OnFrameBoundary(tick); }
        Frame(100); Frame(110);
        TestAssert.IsTrue(observer.Pools.EnemyExists(4));
        TestAssert.IsEmpty(observer.Pools.Presented);

        // Native KillEnemyFinally keeps the core and its base links. There is no Despawn or
        // Death lifecycle packet: only stateFlags.isInvincible and builder progress change.
        enemies.States[core] = Encoded(PersistentCore(true));
        Frame(120); Frame(150);
        TestAssert.AreEqual(1L, observer.Core.DeathsPresented);
        TestAssert.AreEqual((progress, 400), observer.Pools.PresentedProgress.Single());
        TestAssert.IsTrue(client.WorldReplica.TryFindKey(PoolKind.GroundEnemy, 101, 4, out var stillBound));
        TestAssert.AreEqual(core, stillBound);
        TestAssert.IsTrue(observer.Pools.EnemyExists(4));
        TestAssert.IsTrue(observer.Pools.EnemyExists(9));
        TestAssert.AreEqual(0, observer.Pools.PureRemovals);
        // A delayed explicit death must not double-play the already presented transition.
        observer.Core.PlayDeath(core);
        TestAssert.AreEqual(1L, observer.Core.DeathsPresented);

        var rebuilt = Key(4, 2);
        enemies.Members.Remove(core); enemies.Members.Add(rebuilt);
        enemies.States[rebuilt] = Encoded(PersistentCore(false));
        Frame(180);
        TestAssert.AreEqual(1L, observer.Core.DeathsPresented);
        enemies.States[rebuilt] = Encoded(PersistentCore(true));
        Frame(210); Frame(240);
        TestAssert.AreEqual(2L, observer.Core.DeathsPresented);
        TestAssert.AreEqual((400, 400), observer.Pools.PresentedProgress.Last());
        TestAssert.AreEqual(0, observer.Pools.PureRemovals);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void JoiningOrResyncingOntoAPitDoesNotReplayTheCoreExplosion(bool hadLiveCore)
    {
        var observer = new TestBindingObserver();
        var core = Key(4, 1);
        if (hadLiveCore) observer.Core.ApplyState(core, Encoded(PersistentCore(false)));
        observer.Core.ReconcileBaseline([new SnapshotMemberRecord(core, 2, Encoded(PersistentCore(true)))]);
        observer.Core.ApplyState(core, Encoded(PersistentCore(true)));
        observer.Core.PlayDeath(core);
        TestAssert.IsEmpty(observer.Pools.Presented);
        TestAssert.AreEqual(0L, observer.Core.DeathsPresented);
        TestAssert.IsTrue(observer.Pools.EnemyExists(4));
        TestAssert.AreEqual(0, observer.Pools.PureRemovals);
    }

    [TestMethod]
    public void ABaselineDropIsNotADeathAndPresentsNothing()
    {
        // A resync baseline that no longer names a member evicts it silently: the client never saw
        // a published death for it, so it must not invent one.
        var observer = new TestBindingObserver();
        observer.Core.ReconcileBaseline(new List<SnapshotMemberRecord>
        {
            new(Key(9, 1), 1, Encoded(Unit(baseId: 0))),
            new(Key(12, 1), 1, Encoded(Unit(baseId: 0)))
        });
        observer.Core.ReconcileBaseline(new List<SnapshotMemberRecord>
        {
            new(Key(9, 1), 1, Encoded(Unit(baseId: 0)))
        });
        TestAssert.IsFalse(observer.Pools.EnemyExists(12));
        TestAssert.AreEqual(0, observer.Core.DeathsPresented);
        TestAssert.IsEmpty(observer.Pools.Presented);
    }

    [TestMethod]
    public void ATrailingDeathOfARecycledSlotPresentsNothing()
    {
        // The host publishes Spawn → State → Despawn within one frame, so the new generation owns
        // the slot by the time the old generation's death is applied.
        var observer = new TestBindingObserver();
        var first = Key(9, 1);
        observer.Core.ReconcileBaseline(new List<SnapshotMemberRecord>
        {
            new(first, 1, Encoded(Unit(baseId: 0)))
        });
        observer.Core.ReconcileBaseline(new List<SnapshotMemberRecord>
        {
            new(Key(9, 2), 1, Encoded(Unit(baseId: 0, hp: 880)))
        });

        observer.Core.PlayDeath(first);
        TestAssert.AreEqual(0, observer.Core.DeathsPresented,
            "The slot's new owner must not inherit the old generation's death.");
        observer.Core.RemoveMember(first);
        TestAssert.IsTrue(observer.Pools.EnemyExists(9),
            "The trailing death must not delete the new generation's shell either.");
    }

    [TestMethod]
    public void AUnitsBehaviorTravelsWithItsState()
    {
        var observer = new TestBindingObserver();
        var unit = Key(9, 1);
        // 9 is EEnemyBehavior.Engage: the panel prints it as "攻击中" for a unit that is fighting.
        observer.Core.ReconcileBaseline(new List<SnapshotMemberRecord>
        {
            new(unit, 1, Encoded(Unit(baseId: 0, unitBehavior: 9)))
        });
        TestAssert.AreEqual((byte)9, observer.Pools.Shells[9].UnitBehavior,
            "A defaulted byte would show 巡逻中 for a unit that is attacking.");
        observer.Core.ReconcileBaseline(new List<SnapshotMemberRecord>
        {
            new(unit, 1, Encoded(Unit(baseId: 0, unitBehavior: 2)))
        });
        TestAssert.AreEqual((byte)2, observer.Pools.Shells[9].UnitBehavior);
    }

    [TestMethod]
    public void LosingTheCoreRebuildsTheScope()
    {
        var observer = new TestBindingObserver();
        var core = Key(4, 1);
        var unit = Key(9, 1);
        observer.Core.ReconcileBaseline(new List<SnapshotMemberRecord>
        {
            new(core, 1, Encoded(BaseCore(baseId: 2))),
            new(unit, 2, Encoded(Unit(baseId: 2)))
        });
        TestAssert.IsTrue(observer.Pools.EnemyExists(4));
        TestAssert.IsTrue(observer.Pools.EnemyExists(9));
        observer.Core.ReconcileBaseline(new List<SnapshotMemberRecord>());
        TestAssert.IsFalse(observer.Pools.EnemyExists(4));
        TestAssert.IsFalse(observer.Pools.EnemyExists(9));
        TestAssert.AreEqual(2, observer.Pools.PureRemovals);
    }

    [TestMethod]
    public void TheCompositeFansOutToBothDomains()
    {
        var entityView = new FakeHostEnemies();
        var compositeView = new CompositeHostWorldView(new FakeGroundView(), new FakeGroundView());
        TestAssert.IsFalse(compositeView.TryReadMembers(new ScopeKey(PoolKind.Entity, 101), new List<ObjectKey>()));
        var applied = new List<string>();
        var composite = new CompositeMirrorObserver(new RecordingObserver("first", applied), new RecordingObserver("second", applied));
        var key = Key(1, 1);
        composite.OnStateApplied(Scope, key, 1, Encoded(Unit(baseId: 0)));
        CollectionAssert.AreEquivalent(new[] { "first", "second" }, applied);
    }

    private sealed class FakeGroundView : IHostWorldView
    {
        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members) => false;
        public bool TryReadState(ObjectKey key, out byte[]? state)
        {
            state = null;
            return false;
        }
    }


}
