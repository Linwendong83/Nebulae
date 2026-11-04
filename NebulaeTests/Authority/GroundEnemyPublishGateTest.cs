using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority.Adapters;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// The per-member publish gate as the snapshot adapter applies it.
/// </summary>
/// <remarks>
/// <para>
/// The policy predicates are covered by <see cref="EnemyStateGateTest"/>; what is locked here is the
/// adapter's use of them, which is where the polarity actually went wrong once. The adapter answers
/// a member's read from a cache of the last published record, and the replicator compares whatever
/// it gets against what it already sent — so answering "unchanged" for a record whose bytes moved is
/// indistinguishable from publishing nothing, and the fact never reaches a subscriber.
/// </para>
/// <para>
/// The member under test is a static one — a dark fog ground building — because that is the shape
/// that broke: a settled record whose hitpoints and structure points move while nothing about it
/// moves. A moving unit keeps the checkpoint policy and was never affected.
/// </para>
/// </remarks>
[TestClass]
public class GroundEnemyPublishGateTest
{
    private static readonly AuthorityEpoch Epoch = new(1, 2);
    private const int Planet = 101;
    private const int BuildingSlot = 1;
    private const int BuilderSlot = 1;

    [TestMethod]
    public void AStaticEnemyRecordRepublishesWhenItsFactsMove()
    {
        var previousGame = GameMain.data;
        try
        {
            var (adapter, key, builders) = BuildWorld();

            // The record has never been published, so it is encoded fresh.
            TestAssert.IsTrue(adapter.TryReadState(key, out var first));
            TestAssert.IsTrue(GroundEnemyStateCodec.TryDecode(first, 0, first.Length, out var decoded, out _));
            TestAssert.AreEqual(120, decoded.BuilderSp, "The first read publishes the host's structure points.");
            TestAssert.AreEqual(400, decoded.BuilderSpMax);

            // A settled reading is answered from the cache: the replicator's own comparison would
            // reach the same verdict, so handing back the published bytes skips the encoder.
            TestAssert.IsTrue(adapter.TryReadState(key, out var unchanged));
            TestAssert.AreSame(first, unchanged, "An unchanged reading must be suppressed, not re-encoded.");

            // The player damages the building and the host's builder loses structure points. Nothing
            // about the building moved, yet this is the record the client's health bar, its wreckage
            // and its destroyed state all come from — so it has to travel.
            builders.buffer[BuilderSlot].sp = 40;
            TestAssert.IsTrue(adapter.TryReadState(key, out var damaged));
            TestAssert.AreNotSame(first, damaged, "A moved fact must not be answered from the cache.");
            TestAssert.IsTrue(GroundEnemyStateCodec.TryDecode(damaged, 0, damaged.Length, out var after, out _));
            TestAssert.AreEqual(40, after.BuilderSp, "The damage has to reach the subscriber.");
            TestAssert.AreEqual(400, after.BuilderSpMax);

            // And the next settled reading is suppressed again, from the record just published.
            TestAssert.IsTrue(adapter.TryReadState(key, out var settled));
            TestAssert.AreSame(damaged, settled);
        }
        finally { GameMain.data = previousGame; }
    }

    [TestMethod]
    public void AStaticEnemyRecordPublishesADestroyedBuilding()
    {
        var previousGame = GameMain.data;
        try
        {
            var (adapter, key, builders) = BuildWorld();
            TestAssert.IsTrue(adapter.TryReadState(key, out var first));

            // A building the host has destroyed keeps its enemy slot (a based enemy is never removed
            // from the pool), so the only channel that can tell a subscriber it is gone is this
            // record: structure points at zero and the builder's state dropped with them.
            builders.buffer[BuilderSlot].sp = 0;
            builders.buffer[BuilderSlot].state = 0;
            TestAssert.IsTrue(adapter.TryReadState(key, out var destroyed));
            TestAssert.AreNotSame(first, destroyed);
            TestAssert.IsTrue(GroundEnemyStateCodec.TryDecode(destroyed, 0, destroyed.Length, out var after, out _));
            TestAssert.AreEqual(0, after.BuilderSp);
            TestAssert.AreEqual(0u, after.AnimationState);
        }
        finally { GameMain.data = previousGame; }
    }

    [TestMethod]
    public void NativeCoreStatePublishesItsDeathWithoutRemovingTheCoreOrItsBase()
    {
        var previousGame = GameMain.data;
        try
        {
            var (adapter, key, builders) = BuildWorld();
            var factory = GameMain.galaxy.PlanetById(Planet).factory;
            factory.enemyPool[BuildingSlot].dfGBaseId = 1;
            factory.enemySystem.bases = new ObjectPool<DFGBaseComponent>(); factory.enemySystem.bases.Reset();
            var core = factory.enemySystem.bases.Add();
            core.enemyId = BuildingSlot; core.builderId = BuilderSlot;
            var pools = new CorePools();
            var binding = new GroundEnemyBinding(Planet, pools);
            TestAssert.IsTrue(adapter.TryReadState(key, out var intact));
            TestAssert.IsTrue(binding.ApplyState(key, intact));

            // These are the actual KillEnemyFinally / NotifyBaseKilled changes. Both native
            // pool slots survive, so disappearance-based death publication cannot detect this.
            factory.enemyPool[BuildingSlot].isInvincible = true;
            builders.buffer[BuilderSlot].sp = 0;
            builders.buffer[BuilderSlot].state = 0;
            adapter.MarkState(Planet, BuildingSlot);
            TestAssert.IsTrue(adapter.TryReadState(key, out var destroyed));
            TestAssert.IsTrue(binding.ApplyState(key, destroyed));
            TestAssert.IsTrue(binding.ApplyState(key, destroyed));
            TestAssert.AreEqual(1, pools.Deaths);
            TestAssert.AreEqual((120, 400), pools.Progress);
            TestAssert.AreEqual(1, factory.enemyPool[BuildingSlot].id);
            TestAssert.AreSame(core, factory.enemySystem.bases.buffer[1]);
            TestAssert.AreEqual(0, pools.Removals);
            TestAssert.IsTrue(adapter.TryGetEnemyKey(Planet, BuildingSlot, out var pitKey));
            TestAssert.AreEqual(key, pitKey);

            builders.buffer[BuilderSlot].sp = 120;
            factory.enemyPool[BuildingSlot].isInvincible = false;
            TestAssert.IsTrue(adapter.TryGetEnemyKey(Planet, BuildingSlot, out var rebuilt));
            TestAssert.IsGreaterThan(key.Generation, rebuilt.Generation);
            TestAssert.IsTrue(adapter.TryReadState(rebuilt, out var aliveAgain));
            TestAssert.IsTrue(binding.ApplyState(rebuilt, aliveAgain));
            factory.enemyPool[BuildingSlot].isInvincible = true;
            builders.buffer[BuilderSlot].sp = 0;
            TestAssert.IsTrue(adapter.TryReadState(rebuilt, out var killedAgain));
            TestAssert.IsTrue(binding.ApplyState(rebuilt, killedAgain));
            TestAssert.AreEqual(2, pools.Deaths);
            TestAssert.AreEqual(0, pools.Removals);
        }
        finally { GameMain.data = previousGame; }
    }

    private sealed class CorePools : IGroundEnemyPools
    {
        public int Deaths, Removals;
        public (int Sp, int Max) Progress;
        public bool EnemyExists(int enemyId) => true;
        public void CreateEnemyShell(int enemyId, in GroundEnemyState state) { }
        public void WriteEnemyState(int enemyId, in GroundEnemyState state) { }
        public void RemoveEnemyShell(int enemyId) => Removals++;
        public void PlayDeathPresentation(int enemyId, int builderSp, int builderSpMax)
        { Deaths++; Progress = (builderSp, builderSpMax); }
    }

    /// <summary>
    /// Builds the smallest world one ground-enemy read needs: a planet factory holding a single
    /// static building enemy, its builder component, and the sector skill pool the host records
    /// hitpoints in. Nothing here runs a rule — the read path must not need one.
    /// </summary>
    private static (GroundEnemySnapshotAdapter Adapter, ObjectKey Key, DataPool<EnemyBuilderComponent> Builders) BuildWorld()
    {
        var game = Uninitialized<GameData>();
        var sector = Uninitialized<SpaceSector>();
        sector.skillSystem = Uninitialized<SkillSystem>();
        game.spaceSector = sector;

        var factory = Uninitialized<PlanetFactory>();
        AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, new PlanetData { id = Planet });
        game.galaxy = new GalaxyData { stars = [new StarData { planets = [new PlanetData { id = Planet, factory = factory }] }] };
        GameMain.data = game;

        factory.enemyPool = new EnemyData[4];
        factory.enemyPool[BuildingSlot] = new EnemyData
        {
            id = BuildingSlot,
            protoId = 8101,
            modelIndex = 41,
            astroId = Planet,
            originAstroId = Planet,
            owner = 1,
            builderId = BuilderSlot,
            pos = new VectorLF3(0, 200, 0),
            rot = Quaternion.identity
        };
        factory.enemyCursor = BuildingSlot + 1;
        factory.enemyAnimPool = new AnimData[4];

        factory.enemySystem = Uninitialized<EnemyDFGroundSystem>();
        var builders = new DataPool<EnemyBuilderComponent>();
        builders.Reset();
        builders.buffer[BuilderSlot] = new EnemyBuilderComponent
        {
            id = BuilderSlot,
            enemyId = BuildingSlot,
            builderIndex = 3,
            sp = 120,
            spMax = 400,
            matter = 40,
            maxMatter = 100,
            energy = 9000,
            maxEnergy = 9000,
            genMatter = 2,
            genEnergy = 1,
            idleEnergy = 1,
            workEnergy = 2,
            state = 4
        };
        builders.cursor = BuilderSlot + 1;
        factory.enemySystem.builders = builders;

        var adapter = new GroundEnemySnapshotAdapter(Epoch);
        TestAssert.IsTrue(adapter.TryGetEnemyKey(Planet, BuildingSlot, out var key));
        return (adapter, key, builders);
    }
}
