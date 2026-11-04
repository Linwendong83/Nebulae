using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld.Combat;
using NebulaeWorld;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class NativeEnemySimulationTest
{
    private static EnemyUnitComponent Unit(int id = 9, int enemy = 3) => new()
    {
        id = id,
        enemyId = enemy,
        planetId = 101,
        protoId = 8128,
        level = 2,
        behavior = EEnemyBehavior.Engage,
        stateTick = 17,
        fire0 = 123,
        fire1 = 456,
        heat = 12,
        assaults = new TargetQueue { count = 1, targetAstroId = 101, target = new Vector3(1, 2, 3) },
        hatred = new HatredList { max = new HatredTarget { target = ((int)ETargetType.Player << 26) | 7, value = 1234 } },
        blockSkill = new SKILLID { id = 999, type = ESkillType.TurretGaussProjectiles }
    };

    [TestMethod]
    public void NativeSensorPlayerInputsAreSelectedWithoutChangingSharedWorkerState()
    {
        var previous = Multiplayer.Session;
        var session = Uninitialized<MultiplayerSession>();
        session.Combat = Uninitialized<CombatManager>();
        session.Combat.Players = new[]
        {
            new CombatManager.PlayerPosition { id = 2, isAlive = true, planetId = 101, position = new Vector3(30, 200, 0) },
            new CombatManager.PlayerPosition { id = 7, isAlive = true, planetId = 101, position = new Vector3(2, 200, 0) },
            new CombatManager.PlayerPosition { id = 9, isAlive = false, planetId = 101, position = new Vector3(1, 200, 0) }
        };
        var factory = Uninitialized<PlanetFactory>();
        AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, new PlanetData { id = 101 });
        var system = Uninitialized<EnemyDFGroundSystem>();
        system.local_player_pos = new Vector3(99, 88, 77);
        var enemy = new EnemyData { pos = new VectorLF3(0, 200, 0) };
        try
        {
            Multiplayer.Session = session;
            var context = EnemySensorContext.Begin(factory, enemy);
            try
            {
                TestAssert.AreEqual(7, EnemySensorContext.PlayerId());
                TestAssert.AreEqual(new Vector3(2, 200, 0), EnemySensorContext.GroundPosition(system));
                TestAssert.AreEqual(new Vector3(99, 88, 77), system.local_player_pos);
                TestAssert.AreEqual(1, Task.Run(EnemySensorContext.PlayerId).Result, "Another worker cannot borrow the selected player.");
            }
            finally { EnemySensorContext.Restore(context); }
        }
        finally { Multiplayer.Session = previous; }
    }

    [TestMethod]
    public void NativeInputsSurviveTheWireAndBindingWithoutBorrowingHostSkillIndexes()
    {
        var unit = Unit();
        var body = EnemyUnitStateCodec.Encode(unit);
        var state = new GroundEnemyState(GroundEnemyKind.GroundUnit, false, true, 8128, 45, 1, 1, 0,
            101, 101, 1, 0, 2, 0, 200, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
            unitBehavior: (byte)unit.behavior, sampleTick: 10, unitStateTick: 17, nativeUnit: body);
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(state, out var encoded));
        TestAssert.IsTrue(GroundEnemyStateCodec.TryDecode(encoded, 0, encoded.Length, out var restored, out _));
        var pool = new DataPool<EnemyUnitComponent>(); pool.Reset();
        var enemy = new EnemyData { id = 3, protoId = 8128 };
        TestAssert.IsTrue(NativeEnemySimulation.BindUnit(pool, ref enemy, restored.NativeUnit, 101));
        ref var installed = ref pool.buffer[enemy.unitId];
        TestAssert.AreEqual(9, installed.id, "The native phase ordinal remains the host's.");
        TestAssert.AreEqual(17, installed.stateTick);
        TestAssert.AreEqual(123, installed.fire0);
        TestAssert.AreEqual(456, installed.fire1);
        TestAssert.AreEqual(1234, installed.hatred.max.value);
        TestAssert.AreEqual(7, installed.hatred.max.objectId);
        TestAssert.AreEqual(new Vector3(1, 2, 3), installed.assaults.target);
        TestAssert.AreEqual(0, installed.blockSkill.id);
        TestAssert.AreEqual(1, pool.count);
        TestAssert.IsTrue(NativeEnemySimulation.BindUnit(pool, ref enemy, body, 101));
        TestAssert.AreEqual(1, pool.count, "A repeated checkpoint never allocates another unit.");
        var collision = Unit(enemy: 4); var other = new EnemyData { id = 4, protoId = 8128 };
        TestAssert.IsFalse(NativeEnemySimulation.BindUnit(pool, ref other, EnemyUnitStateCodec.Encode(collision), 101));
        TestAssert.AreEqual(3, pool.buffer[9].enemyId);
        TestAssert.IsFalse(EnemyUnitStateCodec.TryDecode(body.Take(body.Length - 1).ToArray(), out _));
    }

    [TestMethod]
    public void NativeLoopAdaptersInstallAgainstTheRealGameAndKeepBehaviorCallsIntact()
    {
        var harmony = new Harmony("nebulae.tests.native-enemy-loops");
        try
        {
            harmony.CreateClassProcessor(typeof(NativeEnemySimulation_Patch)).Patch();
            harmony.CreateClassProcessor(typeof(GroundEnemySensor_Patch)).Patch();
            harmony.CreateClassProcessor(typeof(SpaceEnemySensor_Patch)).Patch();
            foreach (var method in new[]
            {
                AccessTools.Method(typeof(EnemyDFGroundSystem), nameof(EnemyDFGroundSystem.GameTickLogic_Unit)),
                AccessTools.Method(typeof(GameLogic), "_enemy_ground_unit_parallel"),
                AccessTools.Method(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.GameTickLogic))
            })
            {
                var code = NativeEnemySimulation_Patch.Transpiler(PatchProcessor.GetOriginalInstructions(method), method).ToList();
                TestAssert.IsTrue(code.Any(x => x.Calls(AccessTools.Method(typeof(NativeEnemySimulation), nameof(NativeEnemySimulation.SimulationId)))));
                TestAssert.IsTrue(code.Any(x => x.Calls(AccessTools.Method(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.UpdateFireCondition)))));
                TestAssert.IsTrue(code.Any(x => x.Calls(AccessTools.Method(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.RunBehavior_None)))));
            }
        }
        finally { harmony.UnpatchSelf(); }
    }

    [TestMethod]
    public void NativeGroundTickAdvancesInstalledInputsWithoutAClientMotionImplementation()
    {
        var previous = AuthorityRuleGuard.Probe;
        var prefabs = SpaceSector.PrefabDescByModelIndex;
        var harmony = new Harmony("nebulae.tests.native-ground-tick");
        try
        {
            AuthorityRuleGuard.Probe = null;
            harmony.CreateClassProcessor(typeof(NativeEnemySimulation_Patch)).Patch();
            var factory = Uninitialized<PlanetFactory>();
            var planet = new PlanetData { id = 101, seed = 17, radius = 200, scale = 1 };
            AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, planet);
            factory.enemyPool = new EnemyData[4];
            factory.enemyPool[3] = new EnemyData
            {
                id = 3,
                protoId = 8128,
                modelIndex = 45,
                pos = new VectorLF3(0, 200, 0),
                rot = Quaternion.identity,
                vel = new Vector3(6, 0, 0)
            };
            var system = factory.enemySystem = Uninitialized<EnemyDFGroundSystem>();
            system.factory = factory; system.planet = planet;
            system.gameData = Uninitialized<GameData>();
            system.units = new DataPool<EnemyUnitComponent>(); system.units.Reset();
            system.bases = new ObjectPool<DFGBaseComponent>(); system.bases.Reset();
            factory.skillSystem = Uninitialized<SkillSystem>();
            AccessTools.Field(typeof(SpaceSector), nameof(SpaceSector.PrefabDescByModelIndex)).SetValue(null, new PrefabDesc[46]);
            SpaceSector.PrefabDescByModelIndex[45] = new PrefabDesc();
            var unit = Unit(); unit.behavior = EEnemyBehavior.None;
            NativeEnemySimulation.BindUnit(system.units, ref factory.enemyPool[3], EnemyUnitStateCodec.Encode(unit), 101);
            var expected = factory.enemyPool[3]; var expectedUnit = system.units.buffer[9];
            expectedUnit.RunBehavior_None(ref expected);
            system.GameTickLogic_Unit(10, EAggressiveLevel.Dummy);
            TestAssert.AreEqual(expected.pos, factory.enemyPool[3].pos);
            TestAssert.AreEqual(expected.vel, factory.enemyPool[3].vel);
        }
        finally
        {
            harmony.UnpatchSelf(); AuthorityRuleGuard.Probe = previous;
            AccessTools.Field(typeof(SpaceSector), nameof(SpaceSector.PrefabDescByModelIndex)).SetValue(null, prefabs);
        }
    }
}
