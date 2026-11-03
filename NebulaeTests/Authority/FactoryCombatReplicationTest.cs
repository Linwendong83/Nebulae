
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// a host world of building combat facts flows through the standard replicator and
/// the client's replica into a binding that rebuilds the local entity → combat/construct
/// references. Verifies at the model level: damaged state
/// survives subscription and captures unchanged; entities with different host stat indexes bind
/// independently; heal → damage → death → same-slot rebuild never crosses references.
/// </summary>
[TestClass]
public class FactoryCombatReplicationTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0A08080808080808, 0x0C0C0C0C0C0C0C0C);
    private static readonly ScopeKey Scope = new(PoolKind.Entity, 101);

    private static ObjectKey Key(int nativeId, long generation) =>
        ObjectKey.Create(Epoch, PoolKind.Entity, 101, nativeId, generation);

    private static FactoryCombatState Damaged(int hp, int hpMax) => new(
        true, hp, hpMax, hpRecover: 0, hpIncoming: 0,
        hasConstructStat: true, damageRate: 300f, repairerCount: 0, repairerModuleId: -1, repairerValue: 0f);

    /// <summary>The host's canonical factory, as the adapter would read it — read-only to the pipeline.</summary>
    private sealed class FakeHostFactory : IHostWorldView
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

    /// <summary>Client-local pools the binding writes, plus the entity references it maintains.</summary>
    private sealed class FakeClientPools : IFactoryCombatPools
    {
        public readonly HashSet<int> Entities = [];
        public readonly Dictionary<int, int> EntityCombatRef = [];
        public readonly Dictionary<int, int> EntityConstructRef = [];
        public readonly Dictionary<int, FactoryCombatState> CombatStats = [];
        public readonly List<int> ReleasedCombat = [];

        private int combatCursor = 1;
        private int constructCursor = 1;

        public bool EntityExists(int entityId) => Entities.Contains(entityId);
        public int ReadEntityCombatStat(int entityId) => EntityCombatRef.TryGetValue(entityId, out var v) ? v : 0;
        public void WriteEntityCombatStat(int entityId, int statId) => EntityCombatRef[entityId] = statId;
        public int AllocateCombatStat() => combatCursor++;
        public void ReleaseCombatStat(int statId) => ReleasedCombat.Add(statId);
        public void WriteCombatStat(int statId, in FactoryCombatState state, int planetId, int entityId) =>
            CombatStats[statId] = state;
        public int ReadEntityConstructStat(int entityId) => EntityConstructRef.TryGetValue(entityId, out var v) ? v : 0;
        public void WriteEntityConstructStat(int entityId, int statId) => EntityConstructRef[entityId] = statId;
        public int AllocateConstructStat() => constructCursor++;
        public void ReleaseConstructStat(int statId)
        {
        }
        public void WriteConstructStat(int statId, in FactoryCombatState state, int entityId)
        {
        }
    }

    /// <summary>Turns the replica's mirror mutations into binding-core calls, as the game adapter does.</summary>
    private sealed class TestBindingObserver : IReplicaMirrorObserver
    {
        public readonly FakeClientPools Pools = new();
        public readonly FactoryCombatBinding Core;
        public readonly List<ObjectKey> StateApplied = [];
        public readonly List<ObjectKey> MembersRemoved = [];
        public int BaselinesInstalled;

        public TestBindingObserver()
        {
            Core = new FactoryCombatBinding(101, Pools, (key, _) => { });
        }

        public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state)
        {
            StateApplied.Add(key);
            Core.ApplyState(key, state);
        }

        public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
        {
            BaselinesInstalled++;
            Core.ReconcileBaseline(members);
        }

        public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
        {
            MembersRemoved.Add(key);
            Core.RemoveMember(key);
        }
    }

    /// <summary>A host sink that feeds a client session's inbox exactly as the network would.</summary>
    private sealed class SinkToClientInbox : IReplicationSink
    {
        private readonly AuthoritySession client;

        public SinkToClientInbox(AuthoritySession client) => this.client = client;

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
            TestAssert.IsTrue(client.TryEnqueueReplicaMessage(packet, new ApplyScope(scope, 0, packet.Sequence,
                packet.HostTick)));
        }

        private static ScopeKey TryScope(byte kind, int scope) => new((PoolKind)kind, scope);
    }



    [TestMethod]
    public void ABuildingCombatLifecycleSurvivesTheFullConversation()
    {
        // The host planet: one full-health building and two damaged ones with different host stat
        // indexes (damage landed on different slots of the host's global pool).
        var factory = new FakeHostFactory();
        var intact = Key(1, 1);
        var damagedA = Key(2, 1);
        var damagedB = Key(3, 1);
        factory.Members.AddRange([intact, damagedA, damagedB]);
        factory.States[intact] = Encoded(new FactoryCombatState());
        factory.States[damagedA] = Encoded(Damaged(640, 1000));
        factory.States[damagedB] = Encoded(Damaged(120, 200));

        var hostSession = new AuthoritySession(new AuthoritySessionState());
        hostSession.BeginAuthorityWorld(Epoch, isHost: true);
        var clientSession = new AuthoritySession(new AuthoritySessionState());
        clientSession.BeginAuthorityWorld(Epoch, isHost: false);
        hostSession.RegisterHostReplication(factory, new SinkToClientInbox(clientSession));

        var observer = new TestBindingObserver();
        clientSession.WorldReplica.MirrorObserver = observer;
        clientSession.WorldReplica.SnapshotAckSink = new AckToHost(hostSession);
        foreach (var entity in new[] { 1, 2, 3 })
        {
            observer.Pools.Entities.Add(entity);
        }

        // Join: baseline first, backlog after the ack, Live after the marker.
        clientSession.WorldReplica.Subscribe(Scope);
        hostSession.HostReplicator.Subscribe(1, Scope);
        hostSession.OnFrameBoundary(100);
        clientSession.OnFrameBoundary(100);
        hostSession.OnFrameBoundary(110);
        clientSession.OnFrameBoundary(110);
        TestAssert.IsTrue(clientSession.WorldReplica.Versions.TryGetScope(Scope, out var clientScope));
        TestAssert.AreEqual(SubscriptionPhase.Live, clientScope.Phase);

        // The three entities are bound exactly as the host described them, each with its own local
        // stat despite the host's unrelated stat indexes.
        TestAssert.AreEqual(0, observer.Pools.ReadEntityCombatStat(1),
            "A full-health host statement leaves no local reference.");
        var aStat = observer.Pools.ReadEntityCombatStat(2);
        var bStat = observer.Pools.ReadEntityCombatStat(3);
        TestAssert.IsTrue(aStat > 0 && bStat > 0 && aStat != bStat);
        TestAssert.AreEqual(640, observer.Pools.CombatStats[aStat].Hp);
        TestAssert.AreEqual(120, observer.Pools.CombatStats[bStat].Hp);
        TestAssert.AreEqual(1, observer.BaselinesInstalled);

        // The world moves on: A is repaired, B's building is destroyed and a new one takes slot 3,
        // and the intact building takes damage.
        var rebuilt = Key(3, 2);
        factory.Members.Remove(damagedB);
        factory.Members.Add(rebuilt);
        factory.States[damagedA] = Encoded(new FactoryCombatState());
        factory.States[intact] = Encoded(Damaged(880, 1000));
        factory.States[rebuilt] = Encoded(Damaged(990, 1000));
        hostSession.Capture.Capture(120);
        hostSession.OnFrameBoundary(120);
        clientSession.OnFrameBoundary(120);

        // The repaired building: reference cleared, local stat recycled.
        TestAssert.AreEqual(0, observer.Pools.ReadEntityCombatStat(2));
        CollectionAssert.Contains(observer.Pools.ReleasedCombat, aStat);

        // The destroyed building's key released its stat and is gone.
        CollectionAssert.Contains(observer.MembersRemoved, damagedB);
        CollectionAssert.Contains(observer.Pools.ReleasedCombat, bStat);

        // The rebuilt slot is a new key with a fresh stat — never the dead one's.
        var rebuiltStat = observer.Pools.ReadEntityCombatStat(3);
        TestAssert.IsTrue(rebuiltStat > 0 && rebuiltStat != bStat);
        TestAssert.AreEqual(990, observer.Pools.CombatStats[rebuiltStat].Hp);

        // The previously intact building is now damaged and bound.
        var intactStat = observer.Pools.ReadEntityCombatStat(1);
        TestAssert.AreEqual(880, observer.Pools.CombatStats[intactStat].Hp);

        // And the host's own facts were never touched by any of this: the reads that fed the
        // baseline and every capture are read-only, so the destroyed building's canonical state is
        // exactly what the host last published.
        CollectionAssert.AreEquivalent(Encoded(Damaged(120, 200)), factory.States[damagedB]);
    }

    [TestMethod]
    public void TheObserverSeesOnlyWhatTheVersionGateAccepted()
    {
        var replica = new ClientWorldReplica(Epoch);
        var applied = new List<ObjectKey>();
        var baselines = new List<int>();
        replica.MirrorObserver = new RecordingObserver(applied, baselines);

        // Install a baseline with one member, then stream one spawn plus two state records of
        // which one is stale. The observer must see the baseline, the spawn's state — and nothing
        // for the stale record.
        var member = Key(5, 1);
        var spawned = Key(6, 1);
        replica.Subscribe(Scope);
        var snapshot = new FrozenScopeSnapshot(Scope, 1, 1, 0, 0,
            new List<SnapshotMemberRecord> { new(member, 2, Encoded(Damaged(500, 900))) });
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(snapshot, out var image));
        var hash = AuthoritySnapshotCodec.Hash(image);
        TestAssert.IsTrue(replica.BeginSnapshot(Scope, 1, 1, 1, image.Length, hash));
        TestAssert.IsTrue(replica.ReceiveSnapshotChunk(Scope, 1, 1, 0, image));
        TestAssert.IsTrue(replica.CommitSnapshot(Scope, 1, 1, 1, hash));
        TestAssert.HasCount(1, baselines, "The baseline notifies once, for the whole membership.");

        TestAssert.IsTrue(replica.ApplyLifecycle(Scope, 1, 1,
            new List<AuthorityLifecycleRecord> { new(AuthorityLifecycleOp.Spawn, spawned, 1) }));
        TestAssert.IsEmpty(applied, "An identity alone notifies nothing — there is no state to bind.");

        TestAssert.IsTrue(replica.ApplyWorldState(Scope, 1, 2, 1,
            new List<AuthorityWorldStateRecord>
            {
                new(spawned, 2, Encoded(Damaged(300, 900))), // the host mints a fresh revision for the state
                new(member, 1, Encoded(Damaged(490, 900))) // stale: baseline shipped revision 2
            }));
        TestAssert.HasCount(1, applied);
        TestAssert.AreEqual(spawned, applied[0], "A refused record never reaches the binding.");

        // A despawn notifies so the binding can release, and the tombstone is final.
        TestAssert.IsTrue(replica.ApplyLifecycle(Scope, 1, 3,
            new List<AuthorityLifecycleRecord> { new(AuthorityLifecycleOp.Despawn, spawned, 2) }));
        TestAssert.HasCount(1, replica.Mirrors);
    }

    private sealed class RecordingObserver : IReplicaMirrorObserver
    {
        private readonly List<ObjectKey> applied;
        private readonly List<int> baselines;

        public RecordingObserver(List<ObjectKey> applied, List<int> baselines)
        {
            this.applied = applied;
            this.baselines = baselines;
        }

        public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state) => applied.Add(key);

        public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members) =>
            baselines.Add(members.Count);

        public void OnMemberRemoved(ScopeKey scope, in ObjectKey key) => applied.Remove(key);
    }
}
