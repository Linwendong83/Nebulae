using HarmonyLib;
using global::NebulaeModel.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Authority.Adapters;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class EnemyTargetLifetimeTest
{
    [TestMethod]
    public void TurretsAndFlyingMissilesRejectReusedEnemySlots()
    {
        var previousGame = GameMain.data; var previousSession = Multiplayer.Session;
        try
        {
            var factory = Uninitialized<PlanetFactory>();
            var planet = new PlanetData { id = 101, factory = factory };
            AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, planet);
            factory.enemyPool = new EnemyData[8]; factory.enemyCursor = 2;
            factory.enemyPool[1] = new EnemyData { id = 1, protoId = 8128, astroId = 101 };
            var game = Uninitialized<GameData>();
            game.galaxy = new GalaxyData { stars = new[] { new StarData { planets = new[] { planet } } } };
            GameMain.data = game;
            var epoch = new AuthorityEpoch(44, 55);
            var runtime = new AuthoritySession(new AuthoritySessionState()); runtime.BeginAuthorityWorld(epoch, true);
            runtime.GroundEnemyHost = new GroundEnemySnapshotAdapter(epoch);
            var session = Uninitialized<MultiplayerSession>();
            session.Server = Uninitialized<global::NebulaeNetwork.Server>(); session.AuthorityRuntime = runtime;
            Multiplayer.Session = session;
            var target = new SkillTarget { id = 1, astroId = 101, type = ETargetType.Enemy };
            var turret = new TurretComponent { entityId = 3, target = target, isAiming = true, isLockingTarget = true };
            var skills = Uninitialized<SkillSystem>();
            skills.astroFactories = new PlanetFactory[200]; skills.astroFactories[101] = factory;
            skills.turretMissiles = new DataPool<GeneralMissile>(); skills.turretMissiles.Reset();
            skills.mechaMissiles = new DataPool<GeneralMissile>(); skills.mechaMissiles.Reset();
            var missile = new GeneralMissile { id = 1, target = target, life = 100 };
            TestAssert.IsTrue(EnemyTargetIdentity.Validate(factory, ref turret));
            TestAssert.IsTrue(EnemyTargetIdentity.ValidateMissile(skills, ref missile));
            TestAssert.IsTrue(runtime.GroundEnemyHost.TryGetEnemyKey(101, 1, out var original));
            runtime.GroundEnemyHost.Removed(original);
            factory.enemyPool[1].protoId = 8129;
            TestAssert.IsFalse(EnemyTargetIdentity.Validate(factory, ref turret));
            TestAssert.AreEqual(0, turret.target.id); TestAssert.IsFalse(turret.isAiming || turret.isLockingTarget);
            TestAssert.AreEqual(ETargetType.Enemy, turret.target.type, "Native reacquisition must never reinterpret an enemy id as a friendly building id.");
            missile.life++;
            TestAssert.IsFalse(EnemyTargetIdentity.ValidateMissile(skills, ref missile));
            TestAssert.AreEqual(0, missile.target.id); TestAssert.AreEqual(101, missile.life, "Keep native flight and expiry.");
            // A new search can select the replacement deliberately.
            // Native Search writes these two fields and retains the previous target kind.
            turret.target.id = target.id; turret.target.astroId = target.astroId;
            EnemyTargetIdentity.Observe(factory, turret);
            TestAssert.IsTrue(EnemyTargetIdentity.Validate(factory, ref turret));
            factory.enemyPool[1].isInvincible = true;
            TestAssert.IsFalse(EnemyTargetIdentity.Validate(factory, ref turret));
        }
        finally { GameMain.data = previousGame; Multiplayer.Session = previousSession; }
    }

    [TestMethod]
    public void MissingPlanetWrongTypeAndOutOfBoundsTargetsAreRejected()
    {
        TestAssert.IsFalse(EnemyTargetIdentity.TryResolve(new SkillTarget { id = int.MaxValue, astroId = 999999, type = ETargetType.Enemy }, out _));
        TestAssert.IsFalse(EnemyTargetIdentity.TryResolve(new SkillTarget { id = 1, astroId = 101, type = ETargetType.None }, out _));
        TestAssert.IsFalse(EnemyTargetIdentity.TryResolve(new SkillTarget { id = -1, astroId = 0, type = ETargetType.Enemy }, out _));
    }
}
