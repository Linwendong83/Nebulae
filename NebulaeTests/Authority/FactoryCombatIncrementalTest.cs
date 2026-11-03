using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using NebulaeWorld.Authority.Adapters;
using HarmonyLib;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class FactoryCombatIncrementalTest
{
    private static readonly AuthorityEpoch Epoch = new(15, 31);
    private static readonly ScopeKey Scope = new(PoolKind.Entity, 101);

    private sealed class Sink : IReplicationSink
    {
        public readonly List<(ushort Peer, AuthorityEnvelopePacket Packet)> Packets = [];
        public void Send(ushort subscriberId, AuthorityEnvelopePacket packet) => Packets.Add((subscriberId, packet));
        public IEnumerable<AuthorityWorldStatePacket> States(ushort peer) => Packets.Where(row => row.Peer == peer)
            .Select(row => row.Packet).OfType<AuthorityWorldStatePacket>().Where(packet => packet.RecordCount > 0);
    }

    private sealed class World
    {
        public long Tick;
        public bool Readable = true;
        public readonly PlanetFactory Factory;
        public readonly FactoryCombatSnapshotAdapter Source;
        public readonly Sink Sink = new();
        public readonly HostWorldReplicator Host;
        public World(int count)
        {
            Factory = new PlanetFactory
            {
                entityPool = new EntityData[count + 2],
                entityCursor = count + 1,
                skillSystem = Uninitialized<SkillSystem>()
            };
            Factory.skillSystem.combatStats = new DataPool<CombatStat>();
            AccessTools.PropertySetter(typeof(PlanetFactory), nameof(PlanetFactory.planet)).Invoke(Factory, [new PlanetData { id = 101 }]);
            Factory.skillSystem.combatStats.Reset();
            for (var i = 1; i <= count; i++) Factory.entityPool[i].id = i;
            Source = new FactoryCombatSnapshotAdapter(Epoch, id => Readable && id == 101 ? Factory : null!, () => Tick);
            Host = new HostWorldReplicator(Epoch, new CompositeHostWorldView(Source), Sink);
            Subscribe(1);
            Sink.Packets.Clear();
        }
        public void Subscribe(ushort peer)
        {
            var offset = Sink.Packets.Count;
            Host.Subscribe(peer, Scope);
            var begin = Sink.Packets.Skip(offset).Select(row => row.Packet).OfType<AuthoritySnapshotBeginPacket>().Single();
            Host.OnSnapshotAck(peer, begin.BaselineId, begin.CutoffSequence, true);
        }
        public void Frame(long tick) { Tick = tick; Host.CaptureFrame(tick); }
        public void Damage(int id, int hp)
        {
            ref var stat = ref Factory.skillSystem.combatStats.Add();
            stat.objectType = (int)EObjectType.Entity; stat.objectId = id; stat.astroId = stat.originAstroId = 101;
            stat.hp = hp; stat.hpMax = 1000;
            Factory.entityPool[id].combatStatId = stat.id;
            Source.MarkState(101, id);
        }
    }

    [TestMethod]
    public void StateCandidateSurvivesCadenceAndUnreportedChangesConvergeAtAudit()
    {
        var world = new World(100);
        world.Host.StateIntervalTicks = 3;
        world.Damage(7, 750);
        world.Source.TryGetEntityKey(101, 7, out var key);
        TestAssert.IsTrue(world.Source.TryReadState(key, out _), "A readonly rule query must not drain the replication candidate.");
        world.Frame(1); world.Frame(2);
        TestAssert.IsFalse(world.Sink.States(1).Any());
        world.Frame(3);
        TestAssert.AreEqual(1, world.Sink.States(1).Count());
        world.Factory.skillSystem.combatStats.buffer[1].hp = 500; // An unhooked writer.
        world.Host.EngagementProvider = _ => false;
        world.Frame(120);
        TestAssert.AreEqual(1, world.Sink.States(1).Count());
        for (var tick = 121; tick <= 127; tick++) world.Frame(tick);
        TestAssert.AreEqual(2, world.Sink.States(1).Count());
        world.Readable = false;
        var events = world.Host.EventsAppended;
        world.Frame(128);
        TestAssert.AreEqual(events, world.Host.EventsAppended, "An unavailable factory is not a wave of removals.");
    }

    [TestMethod]
    public void SameFrameReuseRetiresTheOldKeyAndPublishesTheNewGeneration()
    {
        var world = new World(1);
        TestAssert.IsTrue(world.Source.TryGetEntityKey(101, 1, out var old));
        world.Factory.entityPool[1].id = 0; world.Source.Removed(old);
        world.Factory.entityPool[1].id = 1; world.Source.Added(world.Factory, 1);
        world.Frame(1);
        TestAssert.IsTrue(world.Source.TryGetEntityKey(101, 1, out var current));
        TestAssert.AreEqual(old.Generation + 1, current.Generation);
        TestAssert.IsFalse(world.Source.TryReadState(old, out _));
        TestAssert.IsFalse(world.Source.TryReadState(ObjectKey.Create(new AuthorityEpoch(16, 32), PoolKind.Entity, 101, 1, current.Generation), out _));
        TestAssert.AreEqual(3L, world.Host.EventsAppended, "New spawn, its state, then retirement of the old key.");
    }

    [TestMethod]
    public void SnapshotForAnotherPeerDoesNotEraseAnUnpublishedHealingEvent()
    {
        var world = new World(1);
        world.Damage(1, 750); world.Frame(1);
        world.Sink.Packets.Clear();
        world.Host.StateIntervalTicks = 30;
        world.Factory.entityPool[1].combatStatId = 0;
        world.Source.MarkState(101, 1);
        world.Tick = 2;
        world.Subscribe(2); // Freeze during a paced interval, before the next ordinary state read.
        world.Frame(2);
        TestAssert.AreEqual(1, world.Sink.States(1).Count(), "The existing peer must receive healing even though a new snapshot read it first.");
        TestAssert.AreEqual(2L, world.Host.EventsAppended, "Both damage and healing were published.");
    }
}
