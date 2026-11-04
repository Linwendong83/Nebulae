using System.Reflection;
using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority.Adapters;
using NebulaePatcher.Patches.Authority;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class TurretChainRegressionTest
{
    [TestMethod]
    public void ImportedSpaceGlobalTargetIsPrunedBeforeNativePostTickDereferencesTheEnemyPool()
    {
        var previous = Multiplayer.Session;
        var harmony = new Harmony("nebulae.tests.defense-targets");
        try
        {
            var session = Uninitialized<MultiplayerSession>();
            session.Client = Uninitialized<global::NebulaeNetwork.Client>();
            Multiplayer.Session = session;
            harmony.CreateClassProcessor(typeof(DefenseTargets_Patch)).Patch();
            var sector = Uninitialized<SpaceSector>();
            sector.enemyPool = new EnemyData[8];
            var defense = Uninitialized<DefenseSystem>();
            AccessTools.Field(typeof(DefenseSystem), "spaceSector").SetValue(defense, sector);
            defense.globalTargets = new[] { new TimedSkillTarget { type = ETargetType.Enemy, id = 512, astroId = 0 } };
            defense.localGlobalTargetCursor = 1;
            defense.turrets = new DataPool<TurretComponent>(); defense.turrets.Reset();
            defense.PostGameTick(810408);
            TestAssert.AreEqual(0, defense.localGlobalTargetCursor);
        }
        finally { harmony.UnpatchSelf(); Multiplayer.Session = previous; }
    }

    [TestMethod]
    public void ImportedEnemyIndexesAreDiscardedWithoutRemovingBeaconTargets()
    {
        var defense = Uninitialized<DefenseSystem>();
        defense.globalTargets = new[]
        {
            new TimedSkillTarget { type = ETargetType.Enemy, id = 512 },
            new TimedSkillTarget { type = ETargetType.Entity, id = 3, astroId = 101 }
        };
        defense.localGlobalTargetCursor = 2;
        DefenseTargets_Patch.Prune(defense, true);
        TestAssert.AreEqual(1, defense.localGlobalTargetCursor);
        TestAssert.AreEqual(3, defense.globalTargets[0].id);
        TestAssert.AreEqual(ETargetType.Entity, defense.globalTargets[0].type);
    }

    [TestMethod]
    public void OrdinaryCheckpointPreservesContinuousAimAndFiringWithTheMatchingAngularVelocity()
    {
        var oldGame = GameMain.data;
        var oldSession = Multiplayer.Session;
        try
        {
            var (factory, binding, key, snapshot) = Setup();
            var apply = AccessTools.Method(typeof(TurretReplicaBinding), "Apply");
            TestAssert.IsTrue((bool)apply.Invoke(binding, new object[] { key, snapshot })!);
            ref var turret = ref factory.defenseSystem.turrets.buffer[1];
            turret.localDir = new Vector3(0, 0, 1);
            turret.currentAngleVelocity = new Vector2(6, -3);
            turret.roundFire = 40;
            turret.muzzleFire = 12;
            snapshot.SampleTick = 220;
            TestAssert.IsTrue((bool)apply.Invoke(binding, new object[] { key, snapshot })!);
            TestAssert.AreEqual(1f, turret.localDir.z);
            TestAssert.AreEqual(6f, turret.currentAngleVelocity.x);
            TestAssert.AreEqual(40, turret.roundFire);
            TestAssert.AreEqual(12, turret.muzzleFire);
        }
        finally { GameMain.data = oldGame; Multiplayer.Session = oldSession; }
    }

    [TestMethod]
    public void MissingTargetDoesNotLeaveTheTurretPendingOrReplayItsCheckpointEveryFrame()
    {
        var oldGame = GameMain.data;
        var oldSession = Multiplayer.Session;
        try
        {
            var (factory, binding, key, snapshot) = Setup();
            snapshot.TargetId = 512;
            var mirror = (TurretMirror)AccessTools.Field(typeof(TurretReplicaBinding), "mirror").GetValue(binding);
            mirror.Put(key, snapshot);
            TestAssert.AreEqual(0, mirror.PendingCount);
            ref var turret = ref factory.defenseSystem.turrets.buffer[1];
            turret.localDir = new Vector3(0, 0, 1);
            turret.roundFire = 40;
            mirror.Retry(101);
            TestAssert.AreEqual(1f, turret.localDir.z);
            TestAssert.AreEqual(40, turret.roundFire);
            TestAssert.IsTrue(mirror.TryGet(101, 1, out _, out _));
            TestAssert.IsTrue(turret.activeSearch);
        }
        finally { GameMain.data = oldGame; Multiplayer.Session = oldSession; }
    }

    private static (PlanetFactory, TurretReplicaBinding, ObjectKey, TurretState) Setup()
    {
        var factory = Uninitialized<PlanetFactory>();
        var planet = new PlanetData { id = 101, factory = factory, factoryLoaded = true };
        SetBackingField(factory, "planet", planet);
        factory.entityPool = new EntityData[8];
        factory.entityCursor = 2;
        factory.entityPool[1] = new EntityData { id = 1, modelIndex = 400, turretId = 1 };
        factory.defenseSystem = Uninitialized<DefenseSystem>();
        factory.defenseSystem.factory = factory;
        factory.defenseSystem.turrets = new DataPool<TurretComponent>();
        factory.defenseSystem.turrets.Reset();
        ref var turret = ref factory.defenseSystem.turrets.Add();
        turret.entityId = 1;
        turret.type = ETurretType.Missile;
        var sector = Uninitialized<SpaceSector>();
        sector.enemyPool = new EnemyData[8];
        sector.enemyCursor = 2;
        sector.enemyPool[1] = new EnemyData { id = 1, astroId = 100 };
        var game = Uninitialized<GameData>();
        game.galaxy = new GalaxyData { stars = new[] { new StarData { planets = new[] { planet } } } };
        AccessTools.Field(typeof(GameData), "spaceSector").SetValue(game, sector);
        GameMain.data = game;
        var session = Uninitialized<MultiplayerSession>();
        session.Client = Uninitialized<global::NebulaeNetwork.Client>();
        Multiplayer.Session = session;
        var epoch = new AuthorityEpoch(7, 9);
        return (factory, new TurretReplicaBinding(epoch), ObjectKey.Create(epoch, PoolKind.Turret, 101, 1, 1), new TurretState
        {
            EntityId = 1,
            ModelIndex = 400,
            Type = (byte)ETurretType.Missile,
            TargetId = 1,
            TargetType = (byte)ETargetType.Enemy,
            Direction = new Vector3(1, 0, 0),
            TargetDirection = new Vector3(0, 0, 1),
            RoundFire = 90,
            MuzzleFire = 30,
            SampleTick = 100
        });
    }
}
