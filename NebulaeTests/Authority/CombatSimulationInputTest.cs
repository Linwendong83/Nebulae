using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Players;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;
using NebulaeWorld.Combat;
using NebulaeWorld.MonoBehaviours.Remote;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class CombatSimulationInputTest
{

    [TestMethod]
    public void SpaceCombatKeepsLatestUniversalCoordinatesInDoublePrecision()
    {
        var sample = new RemotePlayerMovement.Snapshot { UPosition = new Double3(100000000.125, -200000000.25, 300000000.375) };
        var view = new CombatManager.PlayerPosition { uPosition = new VectorLF3(1, 2, 3) };
        CombatManager.SetSimulationPosition(ref view, sample, null);
        TestAssert.AreEqual(sample.UPosition.x, view.uPosition.x);
        TestAssert.AreEqual(sample.UPosition.y, view.uPosition.y);
        TestAssert.AreEqual(sample.UPosition.z, view.uPosition.z);
    }

    [TestMethod]
    public void GroundCombatUsesTheSampleInTheCurrentPlanetFrame()
    {
        var planet = new PlanetData { uPosition = new VectorLF3(100000000, 200000000, 300000000), runtimeRotation = new Quaternion(0, 0, 1, 0) };
        var sample = new RemotePlayerMovement.Snapshot { LocalPlanetId = 101, LocalPlanetPosition = new Float3(10, 200, 3), UPosition = new Double3(1, 2, 3) };
        var view = new CombatManager.PlayerPosition { position = new Vector3(99, 99, 99) };
        CombatManager.SetSimulationPosition(ref view, sample, planet);
        TestAssert.AreEqual(10f, view.position.x);
        TestAssert.AreEqual(200f, view.position.y);
        TestAssert.AreEqual(99999990d, view.uPosition.x);
        TestAssert.AreEqual(199999800d, view.uPosition.y);
        TestAssert.AreEqual(300000003d, view.uPosition.z);
    }

    [TestMethod]
    public void LocalAndRemoteAiUseTheSamePublishedPoseAndDiscardItAfterTravel()
    {
        var combat = new CombatManager();
        var local = new CombatManager.PlayerPosition { planetId = -1, starId = 1, uPosition = new VectorLF3(999, 0, 0) };
        TestAssert.IsFalse(combat.ApplyLocalMovement(ref local, null));
        var movement = new PlayerMovement { LocalPlanetId = -1, UPosition = new Double3(100000000.125, 20, 30) };
        combat.RecordLocalMovement(movement, 1);
        var remote = new CombatManager.PlayerPosition();
        CombatManager.SetSimulationPosition(ref remote, new RemotePlayerMovement.Snapshot { UPosition = movement.UPosition }, null);
        TestAssert.IsTrue(combat.ApplyLocalMovement(ref local, null));
        TestAssert.AreEqual(remote.uPosition.x, local.uPosition.x);
        local.starId = 2; local.uPosition = new VectorLF3(777, 0, 0);
        TestAssert.IsFalse(combat.ApplyLocalMovement(ref local, null));
        TestAssert.AreEqual(777d, local.uPosition.x);
        local.starId = 1; local.planetId = 101;
        TestAssert.IsFalse(combat.ApplyLocalMovement(ref local, new PlanetData { id = 101 }));
    }

    [TestMethod]
    [DataRow(8112)]
    [DataRow(8113)]
    public void NativeSpaceSensorUsesTheSimulationTargetInsteadOfTheRenderedBody(int proto)
    {
        var previousSession = Multiplayer.Session;
        var previousPrefabs = SpaceSector.PrefabDescByModelIndex;
        var harmony = new Harmony("nebulae.tests.sensor-simulation-input");
        try
        {
            var session = Uninitialized<MultiplayerSession>();
            session.Combat = new CombatManager();
            var body = Uninitialized<Player>();
            body.uPosition = new VectorLF3(900000000, 0, 0);
            session.Combat.Players = [new CombatManager.PlayerPosition { id = 7, planetId = -1, starId = 1, isAlive = true,
                uPosition = new VectorLF3(2000, 0, 0), mecha = new Mecha { player = body, energyShieldRadius = 6 } }];
            Multiplayer.Session = session;
            var sector = Uninitialized<SpaceSector>();
            sector.astros = new AstroData[2];
            sector.astros[1] = new AstroData { uPos = new VectorLF3(2000, 0, 0), uRot = Quaternion.identity };
            sector.skillSystem = Uninitialized<SkillSystem>();
            sector.skillSystem.maxHatredSpaceTmp = 1000000;
            var hive = Uninitialized<EnemyDFHiveSystem>();
            hive.starData = new StarData { id = 1 };
            hive.hiveAstroId = 1000001;
            hive.orbitRadius = 2000;
            hive.player_local_pos = new VectorLF3(999999, 0, 0);
            AccessTools.Field(typeof(SpaceSector), nameof(SpaceSector.PrefabDescByModelIndex)).SetValue(null,
                new[] { new PrefabDesc { unitSensorRange = 1000 } });
            harmony.CreateClassProcessor(typeof(SpaceEnemySensor_Patch)).Patch();
            var enemy = new EnemyData { id = 3, modelIndex = 0, astroId = 1000001, pos = new VectorLF3(30, 0, 0) };
            var unit = new EnemyUnitComponent { id = 1, enemyId = 3, protoId = proto };
            unit.SensorLogic_Space(ref enemy, sector, hive, 1000000, EAggressiveLevel.Normal);
            TestAssert.AreEqual(7, unit.hatred.max.objectId, "Both sensor branches must use the selected player's simulation pose.");
            TestAssert.AreEqual(900000000d, body.uPosition.x, "Sensing must not move the rendered body.");
            TestAssert.AreEqual(999999d, hive.player_local_pos.x, "A worker must not overwrite shared hive inputs.");
        }
        finally
        {
            harmony.UnpatchSelf(); Multiplayer.Session = previousSession;
            AccessTools.Field(typeof(SpaceSector), nameof(SpaceSector.PrefabDescByModelIndex)).SetValue(null, previousPrefabs);
        }
    }
}
