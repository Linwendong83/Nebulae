using HarmonyLib;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority.Adapters;
using NebulaeWorld.Combat;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class EnemyCheckpointPoseTest
{

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CheckpointAppliesThePoseImmediatelyAndPreparationDoesNotPullItBack(bool ground)
    {
        var previousData = GameMain.data;
        var previousTick = GameMain.gameTick;
        var previousMap = HashSystem.bucketMap;
        var cellSize = AccessTools.Field(typeof(HashSystem), "<cellSize>k__BackingField");
        var previousCellSize = cellSize.GetValue(null);
        try
        {
            // One existing hash bucket suffices for this fixture; no Unity scene is required.
            cellSize.SetValue(null, 1000000f);
            HashSystem.bucketMap = new HashSystem.Cell[32];
            var factory = Uninitialized<PlanetFactory>();
            var planet = new PlanetData { id = 101, factory = factory };
            AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, planet);
            factory.enemyPool = new EnemyData[8];
            factory.enemyAnimPool = new AnimData[8];
            factory.enemyCursor = 8;
            factory.hashSystemDynamic = factory.hashSystemStatic = new HashSystem(true);
            factory.enemySystem = Uninitialized<EnemyDFGroundSystem>();
            factory.enemySystem.units = new DataPool<EnemyUnitComponent>();
            factory.enemySystem.units.Reset();
            var sector = Uninitialized<SpaceSector>();
            sector.enemyPool = new EnemyData[8];
            sector.enemyAnimPool = new AnimData[8];
            sector.enemyCursor = 8;
            sector.skillSystem = Uninitialized<SkillSystem>();
            var galaxy = Uninitialized<GalaxyData>();
            galaxy.stars = [new StarData { planets = [planet] }];
            var data = Uninitialized<GameData>();
            data.galaxy = galaxy;
            data.spaceSector = sector;
            GameMain.data = data;
            GameMain.gameTick = 100;
            var pool = ground ? factory.enemyPool : sector.enemyPool;
            pool[3] = new EnemyData { id = 3, unitId = 9, pos = new VectorLF3(0, 200, 0), rot = Quaternion.identity };
            var native = EnemyUnitStateCodec.Encode(new EnemyUnitComponent
            {
                id = 9,
                enemyId = 3,
                planetId = ground ? 101 : 0,
                protoId = (short)(ground ? 8128 : 8113),
                behavior = EEnemyBehavior.Engage
            });
            if (ground)
            {
                var state = new GroundEnemyState(GroundEnemyKind.GroundUnit, false, true,
                    protoId: 8128, modelIndex: 45, owner: 0, port: 1, stateFlags: 0,
                    astroId: 101, originAstroId: 101, baseId: 0, builderIndex: 0, level: 0,
                    posX: 30, posY: 199, posZ: 8, rotX: 0, rotY: 1, rotZ: 0, rotW: 0,
                    velX: 6, velY: 0, velZ: 0, hp: 0, hpMax: 0, hpRecover: 0, hpIncoming: 0,
                    sampleTick: 100, nativeUnit: native);
                new GroundEnemyReplicaPools(101).WriteEnemyState(3, state);
            }
            else
            {
                var state = new SpaceEnemyState(SpaceEnemyKind.SpaceUnit, false, true,
                    protoId: 8113, modelIndex: 61, owner: 0, port: 1, stateFlags: 0,
                    astroId: 1000101, originAstroId: 1000101, dockIndex: 0, builderIndex: 0, level: 0,
                    posX: 30, posY: 199, posZ: 8, rotX: 0, rotY: 1, rotZ: 0, rotW: 0,
                    velX: 6, velY: 0, velZ: 0, hp: 0, hpMax: 0, hpRecover: 0, hpIncoming: 0,
                    sampleTick: 100, nativeUnit: native);
                new SpaceEnemyReplicaPools().WriteEnemyState(3, state);
            }

            TestAssert.AreEqual(new VectorLF3(30, 199, 8), pool[3].pos);
            TestAssert.AreEqual(new Quaternion(0, 1, 0, 0), pool[3].rot);
            TestAssert.AreEqual(new Vector3(6, 0, 0), pool[3].vel);
            for (var i = 0; i < 120; i++)
            {
                GameMain.gameTick++;
                // Stand in for the movement produced by the native loop between checkpoints.
                pool[3].pos.x += pool[3].vel.x / 60.0;
                var position = pool[3].pos;
                if (ground) NativeEnemySimulation.Prepare(factory);
                else NativeEnemySimulation.Prepare(sector);
                TestAssert.AreEqual(position, pool[3].pos, "Preparation must not correct the native pose toward an old sample.");
            }
        }
        finally
        {
            GameMain.data = previousData;
            GameMain.gameTick = previousTick;
            HashSystem.bucketMap = previousMap;
            cellSize.SetValue(null, previousCellSize);
        }
    }
}
