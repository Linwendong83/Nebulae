using NebulaeModel.Authority;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class AuthorityFieldDeltaTest
{
    private static readonly AuthorityEpoch Epoch = new(31, 47);
    private static readonly ScopeKey Scope = new(PoolKind.Entity, 101);
    private static readonly AuthoritySessionContext Context = new(true, Epoch, new ConnectionEpoch(1), 1, false);
    private static ObjectKey Key(int id = 1, long generation = 1) => ObjectKey.Create(Epoch, Scope.Kind, Scope.Scope, id, generation);

    private static byte[] State(int hp)
    {
        TestAssert.IsTrue(FactoryCombatStateCodec.TryEncode(new FactoryCombatState(true, hp, 1000, 2, 0,
            true, .5f, 1, 7, .25f), out var bytes));
        return bytes;
    }

    private sealed class World : IHostWorldView
    {
        public readonly Dictionary<ObjectKey, byte[]> Members = new() { [Key()] = State(900) };
        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members) { members.AddRange(Members.Keys); return scope == Scope; }
        public bool TryReadState(ObjectKey key, out byte[] state) => Members.TryGetValue(key, out state!);
    }
    private sealed class Sink : IReplicationSink
    {
        public readonly Dictionary<ushort, List<AuthorityEnvelopePacket>> Packets = new();
        public void Send(ushort subscriberId, AuthorityEnvelopePacket packet)
        { if (!Packets.TryGetValue(subscriberId, out var list)) Packets[subscriberId] = list = new(); list.Add(packet); }
    }
    private sealed class Harness
    {
        public readonly World World = new();
        public readonly Sink Sink = new();
        public readonly HostWorldReplicator Host;
        public readonly Dictionary<ushort, ClientWorldReplica> Clients = new();
        private readonly Dictionary<ushort, int> positions = new();
        public Harness() { Host = new HostWorldReplicator(Epoch, World, Sink) { StateIntervalTicks = 1 }; }
        public void Subscribe(ushort peer)
        {
            Clients[peer] = new ClientWorldReplica(Epoch); Clients[peer].Subscribe(Scope); positions[peer] = 0;
            Host.Subscribe(peer, Scope); Feed(peer);
        }
        public void Feed(ushort peer)
        {
            var client = Clients[peer]; var list = Sink.Packets[peer];
            while (positions[peer] < list.Count)
            {
                var packet = list[positions[peer]++];
                switch (packet)
                {
                    case AuthoritySnapshotBeginPacket begin:
                        TestAssert.IsTrue(client.BeginSnapshot(Scope, begin.SubscriptionEpoch, begin.BaselineId,
                            begin.ChunkCount, begin.TotalBytes, begin.SnapshotHash)); break;
                    case AuthoritySnapshotChunkPacket chunk:
                        TestAssert.IsTrue(client.ReceiveSnapshotChunk(Scope, chunk.SubscriptionEpoch, chunk.BaselineId,
                            chunk.ChunkIndex, chunk.Data)); break;
                    case AuthoritySnapshotCommitPacket commit:
                        TestAssert.IsTrue(client.CommitSnapshot(Scope, commit.SubscriptionEpoch, commit.BaselineId,
                            commit.ChunkCount, commit.SnapshotHash));
                        Host.OnSnapshotAck(peer, commit.BaselineId, 0, true); break;
                    case AuthorityWorldStatePacket state:
                        TestAssert.IsTrue(AuthorityWorldStateCodec.TryDecode(state.Data, 0, state.Data.Length,
                            state.RecordCount, Context, out var records, out _));
                        TestAssert.IsTrue(client.ApplyWorldState(Scope, state.SubscriptionEpoch, state.Sequence,
                            state.DeclaredBaselineId, records)); break;
                    case AuthorityLifecyclePacket lifecycle:
                        TestAssert.IsTrue(AuthorityLifecycleCodec.TryDecode(lifecycle.Data, 0, lifecycle.Data.Length,
                            lifecycle.RecordCount, Context, out var changes, out _));
                        TestAssert.IsTrue(client.ApplyLifecycle(Scope, lifecycle.SubscriptionEpoch, lifecycle.Sequence, changes)); break;
                }
            }
        }
        public AuthorityWorldStateRecord[] Records(ushort peer) => Sink.Packets[peer].OfType<AuthorityWorldStatePacket>()
            .SelectMany(packet =>
            {
                TestAssert.IsTrue(AuthorityWorldStateCodec.TryDecode(packet.Data, 0, packet.Data.Length,
                packet.RecordCount, Context, out var records, out _)); return records;
            }).ToArray();
    }

    [TestMethod]
    public void HealthOnlyChangeUsesSnapshotStateAsBaselineAndReconstructsExactCanonicalBytes()
    {
        var test = new Harness(); test.Subscribe(1);
        test.World.Members[Key()] = State(800); test.Host.CaptureFrame(1); test.Feed(1);
        var record = test.Records(1).Single();
        TestAssert.IsTrue(record.IsDelta); TestAssert.AreEqual(1L, record.BaseRevision);
        TestAssert.IsLessThan(State(800).Length, record.State.Length + 8);
        CollectionAssert.AreEqual(State(800), test.Clients[1].Mirrors[Key()].CanonicalState);
        var before = test.Sink.Packets[1].Count; test.Host.CaptureFrame(2);
        TestAssert.HasCount(before, test.Sink.Packets[1], "No change produces no state packet.");
    }

    [TestMethod]
    public void JoiningDuringAnUnpublishedChangeDoesNotReplaceAnotherClientsBaseline()
    {
        var test = new Harness(); test.Subscribe(1);
        test.World.Members[Key()] = State(800); test.Host.CaptureFrame(1); test.Feed(1);
        test.World.Members[Key()] = State(700); test.Subscribe(2);
        test.World.Members[Key()] = State(600); test.Host.CaptureFrame(2); test.Feed(1); test.Feed(2);
        CollectionAssert.AreEqual(State(600), test.Clients[1].Mirrors[Key()].CanonicalState);
        CollectionAssert.AreEqual(State(600), test.Clients[2].Mirrors[Key()].CanonicalState);
        TestAssert.HasCount(3, test.Records(1));
        TestAssert.HasCount(1, test.Records(2));
        TestAssert.AreNotEqual(test.Records(1).First().BaseRevision, test.Records(2).First().BaseRevision);
    }

    [TestMethod]
    public void SameSlotNewGenerationGetsFullFirstStateAndCannotUseItsPredecessorsPatch()
    {
        var test = new Harness(); test.Subscribe(1);
        test.World.Members.Remove(Key()); test.World.Members[Key(generation: 2)] = State(1000);
        test.Host.CaptureFrame(1); test.Feed(1);
        TestAssert.IsFalse(test.Records(1).Single().IsDelta);
        TestAssert.IsFalse(test.Clients[1].Mirrors.ContainsKey(Key()));
        CollectionAssert.AreEqual(State(1000), test.Clients[1].Mirrors[Key(generation: 2)].CanonicalState);
    }

    [TestMethod]
    public void FailedSecondPatchLeavesEveryMirrorAndObjectRevisionUntouchedAndRequestsBaseline()
    {
        var test = new Harness(); test.Subscribe(1); var client = test.Clients[1];
        client.Versions.TryGetScope(Scope, out var version);
        var before = State(900); var next = State(800);
        TestAssert.IsTrue(FieldDeltaCodec.TryEncode(before, AuthorityStateFields.Describe(Scope.Kind, before),
            next, AuthorityStateFields.Describe(Scope.Kind, next), out var patch));
        TestAssert.IsFalse(client.ApplyWorldState(Scope, version.SubscriptionEpoch, version.LastAppliedSequence + 1,
            version.BaselineId, new[] { new AuthorityWorldStateRecord(Key(), 2, patch, 1),
                new AuthorityWorldStateRecord(Key(), 5, patch, 4) }));
        CollectionAssert.AreEqual(before, client.Mirrors[Key()].CanonicalState);
        TestAssert.AreEqual(1L, client.Mirrors[Key()].Revision);
        TestAssert.Contains(Scope, client.ScopesNeedingResync);
    }

    [TestMethod]
    public void StaleRecordInABatchDoesNotReplaceTheBasisOfTheFollowingFreshPatch()
    {
        var test = new Harness(); test.Subscribe(1);
        test.World.Members[Key()] = State(800); test.Host.CaptureFrame(1); test.Feed(1);
        test.World.Members[Key()] = State(700); test.Host.CaptureFrame(2); test.Feed(1);
        var stale = test.Records(1).First(); var client = test.Clients[1];
        client.Versions.TryGetScope(Scope, out var version);
        var previous = State(700); var current = State(600);
        TestAssert.IsTrue(FieldDeltaCodec.TryEncode(previous, AuthorityStateFields.Describe(Scope.Kind, previous),
            current, AuthorityStateFields.Describe(Scope.Kind, current), out var patch));
        TestAssert.IsTrue(client.ApplyWorldState(Scope, version.SubscriptionEpoch, version.LastAppliedSequence + 1,
            version.BaselineId, new[] { stale, new AuthorityWorldStateRecord(Key(), 4, patch, 3) }));
        CollectionAssert.AreEqual(current, client.Mirrors[Key()].CanonicalState);
        TestAssert.AreEqual(4L, client.Mirrors[Key()].Revision);
    }

    [TestMethod]
    public void MaximumFullBatchStillFitsTheUpdatedEnvelopeCeiling()
    {
        var records = Enumerable.Range(1, AuthorityLimits.StateRecordCountMax).Select(id =>
            new AuthorityWorldStateRecord(Key(id), 1, new byte[AuthorityLimits.StateRecordMaxBytes])).ToArray();
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryEncode(records, out var bytes));
        TestAssert.IsLessThanOrEqualTo(AuthorityLimits.PayloadMaxBytes(AuthorityFamily.WorldState), bytes.Length);
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryDecode(bytes, 0, bytes.Length, records.Length, Context, out var decoded, out _));
        TestAssert.HasCount(records.Length, decoded);
    }

    [TestMethod]
    public void CanonicalCodecsDescribeAllBytesAndSparseChangesRoundTripAcrossObjectKinds()
    {
        var states = new List<(PoolKind Kind, byte[] Bytes)> { (PoolKind.Entity, State(900)) };
        var ground = new GroundEnemyState(GroundEnemyKind.GroundUnit, true, false, 8130, 45, 2, 3, 0,
            101, 101, 7, 0, 1, 10, 0, 20, 0, 0, 0, 1, 0, 0, 0, 500, 900, 2, 0);
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(ground, out var gb)); states.Add((PoolKind.GroundEnemy, gb));
        var space = new SpaceEnemyState(SpaceEnemyKind.SpaceUnit, true, false, 8113, 45, 2, 3, 0,
            1000101, 1000101, 0, 0, 0, 4900, -350, 1200, 0, 0, 0, 1, 0, 0, 0, 500, 900, 2, 0);
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(space, out var sb)); states.Add((PoolKind.SpaceEnemy, sb));
        var craft = new CraftState(true, true, false, 5101, 60, 1, 7, 0, 101, 1, 10, 0, 20, 0, 0, 0, 1,
            5, 0, 0, 500, 900, 0, 0);
        TestAssert.IsTrue(CraftStateCodec.TryEncode(craft, out var cb)); states.Add((PoolKind.GroundCraft, cb)); states.Add((PoolKind.SpaceCraft, cb));
        TestAssert.IsTrue(TurretStateCodec.TryEncode(new TurretState { EntityId = 9, ModelIndex = 400, Type = 1, BulletCount = 30 }, out var tb));
        states.Add((PoolKind.Turret, tb));
        var baseState = new GroundBaseState
        {
            EnemyId = 1,
            Evolution = Array.Empty<byte>(),
            Forms = new[] {
            new byte[72], new byte[108], new byte[48] }
        };
        // The codec preserves a bounded evolution blob; the field layer does not invent its contents.
        states.Add((PoolKind.Base, GroundBaseStateCodec.Encode(baseState)));
        using (var stream = new MemoryStream())
        {
            using var w = new BinaryWriter(stream);
            w.Write(1); w.Write(0); w.Write(true); w.Write(false); w.Write(true); w.Write(1);
            var evolve = new EvolveData(); evolve.Export(w); w.Write(0);
            for (var i = 0; i < 3; i++) w.Write(0.0);
            w.Write(0); w.Write(0);
            for (var i = 0; i < 3; i++) w.Write(0f);
            w.Write(1.0); w.Write(0f);
            for (var i = 0; i < 4; i++) w.Write(0f);
            for (var i = 0; i < 4; i++) w.Write(0.0);
            w.Write(true); w.Write(true);
            states.Add((PoolKind.HiveSummary, stream.ToArray()));
        }
        foreach (var item in states)
        {
            var fields = AuthorityStateFields.Describe(item.Kind, item.Bytes);
            TestAssert.IsNotNull(fields, item.Kind.ToString());
            TestAssert.IsGreaterThan(4, fields.Length, item.Kind.ToString());
            var changed = (byte[])item.Bytes.Clone(); var scalar = fields.First(field => field.Length == 4);
            changed[scalar.Offset] ^= 1;
            TestAssert.IsTrue(FieldDeltaCodec.TryEncode(item.Bytes, fields, changed, fields, out var patch));
            CollectionAssert.AreEqual(changed, FieldDeltaCodec.Apply(item.Bytes, fields, patch));
        }
    }

    [TestMethod]
    public void UnsupportedAuthorityLayoutUsesFullState()
    {
        var test = new Harness(); test.Subscribe(1);
        test.World.Members[Key()] = Enumerable.Range(0, 33).Select(i => (byte)(i + 17)).ToArray();
        test.Host.CaptureFrame(1);
        TestAssert.IsFalse(test.Records(1).Single().IsDelta, "An unsupported or incompatible layout stays a full bounded state.");
    }


}
