using NebulaeModel.Authority;
using NebulaeWorld.Authority.Adapters;
using UnityEngine;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class GalaxyCaptureParityTest
{
    private static DataPool<T> Pool<T>() where T : struct, IPoolElement
    { var pool = new DataPool<T>(); pool.Reset(); return pool; }

    [TestMethod]
    public void SeventyThousandRelaysProduceOnlyWatchedCandidatesAndGlobalExceptions()
    {
        var previous = GameMain.data; var session = NebulaeWorld.Multiplayer.Session;
        try
        {
            NebulaeWorld.Multiplayer.Session = null;
            const int population = 70000;
            var sector = Uninitialized<SpaceSector>(); sector.maxHiveCount = 18;
            sector.SetEnemyCapacity(population + 1); sector.enemyCursor = population + 1;
            sector.dfHivesByAstro = new EnemyDFHiveSystem[18];
            sector.dfHivesByAstro[1] = new EnemyDFHiveSystem { starData = new StarData { id = 1 } };
            sector.dfHivesByAstro[9] = new EnemyDFHiveSystem { starData = new StarData { id = 2 } };
            GameMain.data = Uninitialized<GameData>(); GameMain.data.spaceSector = sector;
            for (var id = 1; id <= population; id++)
                sector.enemyPool[id] = new EnemyData
                {
                    id = id,
                    originAstroId = id <= 9 ? 1000001 : 1000009,
                    dynamic = true,
                    dfRelayId = 1
                };
            sector.enemyPool[population].dfRelayId = 0;
            sector.enemyPool[population].dfTinderId = 1;
            var adapter = new SpaceEnemySnapshotAdapter(new AuthorityEpoch(3, 4));
            var scope = new ScopeKey(PoolKind.SpaceEnemy, 0);
            var all = new List<ObjectKey>();
            Check.IsTrue(adapter.TryReadMembers(scope, all)); Check.HasCount(population, all);
            var selected = new List<ObjectKey>(); var removed = new List<ObjectKey>();
            adapter.SetInterestedStars(new[] { 1 });
            Check.IsTrue(adapter.TryReadChanges(scope, 1, selected, removed));
            Check.HasCount(10, selected, "Nine local relays plus the global tinder; distant relays are not enumerated.");
            adapter.SetGlobalTargets(new HashSet<int> { 1234 }); selected.Clear();
            Check.IsTrue(adapter.TryReadChanges(scope, 2, selected, removed)); Check.HasCount(11, selected);
            adapter.SetGlobalTargets(new HashSet<int>()); selected.Clear();
            Check.IsTrue(adapter.TryReadChanges(scope, 3, selected, removed)); Check.HasCount(10, selected);
            adapter.SetInterestedStars(Array.Empty<int>()); selected.Clear();
            Check.IsTrue(adapter.TryReadChanges(scope, 4, selected, removed));
            Check.AreEqual(population, selected.Single().NativeId, "Interstellar travel retains the tinder.");
            Check.IsEmpty(removed, "An interest change is not a native despawn.");
        }
        finally { GameMain.data = previous; NebulaeWorld.Multiplayer.Session = session; }
    }

    [TestMethod]
    public void CapturedSnapshotsMatchFreshReadsAcrossChangesAndPoolReuse()
    {
        var previous = GameMain.data;
        try
        {
            var sector = Uninitialized<SpaceSector>(); sector.maxHiveCount = 18;
            sector.SetEnemyCapacity(16); sector.enemyCursor = 8;
            sector.dfHivesByAstro = new EnemyDFHiveSystem[18];
            sector.skillSystem = Uninitialized<SkillSystem>(); sector.skillSystem.combatStats = Pool<CombatStat>();
            var star = new StarData { id = 1, index = 0 };
            var hive = new EnemyDFHiveSystem
            {
                sector = sector,
                starData = star,
                hiveAstroId = 1000001,
                hiveAstroOrbit = new AstroOrbitData(),
                pbuilders = new GrowthPattern_DFSpace.Builder[16],
                builders = Pool<EnemyBuilderComponent>(),
                cores = Pool<DFSCoreComponent>(),
                nodes = Pool<DFSNodeComponent>(),
                replicators = Pool<DFSReplicatorComponent>(),
                units = Pool<EnemyUnitComponent>(),
                turrets = Pool<DFSTurretComponent>(),
                tinders = Pool<DFTinderComponent>()
            };
            hive.relays = new ObjectPool<DFRelayComponent>(); hive.relays.Reset(); hive.InitFormations();
            sector.dfHivesByAstro[1] = hive;
            GameMain.data = Uninitialized<GameData>(); GameMain.data.spaceSector = sector;
            for (var id = 1; id <= 7; id++)
            {
                ref var builder = ref hive.builders.Add(); builder.enemyId = id; builder.state = 3; builder.spMax = 100;
                sector.enemyPool[id] = new EnemyData
                {
                    id = id,
                    protoId = 8100,
                    astroId = 1000001,
                    originAstroId = 1000001,
                    builderId = id,
                    rot = Quaternion.identity
                };
            }
            hive.cores.Add().enemyId = 1; sector.enemyPool[1].dfSCoreId = 1;
            hive.nodes.Add().enemyId = 2; sector.enemyPool[2].dfSNodeId = 1;
            hive.replicators.Add().enemyId = 3; sector.enemyPool[3].dfSReplicatorId = 1;
            ref var turret = ref hive.turrets.Add(); turret.enemyId = 4; turret.builderId = 4;
            sector.enemyPool[4].dfSTurretId = 1;
            var relay = hive.relays.Add(); relay.enemyId = 5; sector.enemyPool[5].dfRelayId = relay.id; sector.enemyPool[5].builderId = 0;
            hive.tinders.Add().enemyId = 6; sector.enemyPool[6].dfTinderId = 1;
            ref var unit = ref hive.units.Add(); unit.enemyId = 7; unit.protoId = 8113;
            sector.enemyPool[7].unitId = 1; sector.enemyPool[7].protoId = 8113;
            for (var id = 5; id <= 7; id++) sector.enemyPool[id].dynamic = true;
            var epoch = new AuthorityEpoch(1, 2);
            var fresh = new SpaceEnemySnapshotAdapter(epoch); var captured = new SpaceEnemySnapshotAdapter(epoch);
            var freshHives = new DarkFogSnapshotAdapter(epoch); var capturedHives = new DarkFogSnapshotAdapter(epoch);
            var scope = new ScopeKey(PoolKind.SpaceEnemy, 0); var hiveScope = new ScopeKey(PoolKind.HiveSummary, 0);
            var freshKeys = new List<ObjectKey>(); var capturedKeys = new List<ObjectKey>();
            Check.IsTrue(fresh.TryReadMembers(scope, freshKeys)); Check.IsTrue(captured.TryReadMembers(scope, capturedKeys));
            CollectionAssert.AreEqual(freshKeys, capturedKeys);
            var freshHiveKeys = new List<ObjectKey>(); var capturedHiveKeys = new List<ObjectKey>();
            freshHives.TryReadMembers(hiveScope, freshHiveKeys); capturedHives.TryReadMembers(hiveScope, capturedHiveKeys);
            for (var tick = 0; tick < 64; tick++)
            {
                sector.enemyPool[5].builderId = tick >= 32 ? 5 : 0;
                hive.builders.buffer[5].matter = tick;
                hive.evolve.level = tick % 10;
                hive.evolve.threat = tick;
                hive.builders.buffer[3].sp = tick;
                hive.replicators.buffer[1].productSp = tick;
                hive.replicators.buffer[1].productSpMax = 100;
                hive.turrets.buffer[1].fire = tick; hive.turrets.buffer[1].localDir.x = tick * 0.01f;
                relay.stage = tick % 3; relay.direction = tick % 2;
                hive.forms[0].units[1 + tick] = tick % 4;
                sector.enemyPool[7].pos.x = tick * 0.25;
                fresh = new SpaceEnemySnapshotAdapter(epoch);
                var referenceKeys = new List<ObjectKey>();
                Check.IsTrue(fresh.TryReadMembers(scope, referenceKeys));
                CollectionAssert.AreEqual(freshKeys, referenceKeys);
                freshHives = new DarkFogSnapshotAdapter(epoch);
                referenceKeys.Clear();
                Check.IsTrue(freshHives.TryReadMembers(hiveScope, referenceKeys));
                CollectionAssert.AreEqual(freshHiveKeys, referenceKeys);
                captured.BeginCapture(scope);
                try
                {
                    for (var repeat = 0; repeat < 2; repeat++)
                        foreach (var key in freshKeys)
                        {
                            var a = fresh.TryReadState(key, out var before);
                            byte[] after;
                            var b = repeat == 0 ? captured.TryReadSnapshotState(key, out after) : captured.TryReadState(key, out after);
                            Check.AreEqual(a, b, "readability " + key);
                            if (a) CollectionAssert.AreEqual(before, after, "tick=" + tick + " " + key);
                        }
                }
                finally { captured.EndCapture(); }
                foreach (var key in freshHiveKeys)
                {
                    Check.IsTrue(freshHives.TryReadState(key, out var before)); Check.IsTrue(capturedHives.TryReadState(key, out var after));
                    CollectionAssert.AreEqual(before, after, "hive tick=" + tick);
                }
            }
            var retired = freshKeys.Last(); fresh.Removed(retired); captured.Removed(retired);
            fresh.Added(retired.NativeId); captured.Added(retired.NativeId);
            Check.IsFalse(fresh.TryReadState(retired, out _)); Check.IsFalse(captured.TryReadState(retired, out _));
            fresh.TryGetEnemyKey(retired.NativeId, out var freshReplacement); captured.TryGetEnemyKey(retired.NativeId, out var capturedReplacement);
            Check.AreEqual(freshReplacement, capturedReplacement); Check.AreNotEqual(retired, capturedReplacement);
            Check.AreEqual(fresh.TryReadState(freshReplacement, out var expected), captured.TryReadState(capturedReplacement, out var actual));
            CollectionAssert.AreEqual(expected, actual);
        }
        finally { GameMain.data = previous; }
    }
}
