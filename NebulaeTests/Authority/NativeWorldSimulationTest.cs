using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaePatcher.Patches.Transpilers;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class NativeWorldSimulationTest
{
    [TestMethod]
    public void NativeFiringAndInventoryChangesUseCheckpointsWhileConfigurationChangesPublishImmediately()
    {
        var before = new TurretState
        {
            EntityId = 1,
            ModelIndex = 400,
            Type = 1,
            SampleTick = 100,
            TargetId = 2,
            ItemCount = 5,
            BulletCount = 20
        };
        var after = before;
        after.SampleTick++;
        after.BulletCount--;
        after.ItemCount--;
        after.TargetId = 3;
        after.Flags = 3;
        after.MuzzleIndex = 1;
        TestAssert.IsFalse(TurretStatePolicy.ShouldPublish(before, after));
        after.SampleTick = before.SampleTick + TurretStatePolicy.CheckpointIntervalTicks;
        TestAssert.IsTrue(TurretStatePolicy.ShouldPublish(before, after));
        after = before; after.Group++;
        TestAssert.IsTrue(TurretStatePolicy.ShouldPublish(before, after));
        after = before; after.SupernovaTick = -200;
        TestAssert.IsTrue(TurretStatePolicy.ShouldPublish(before, after));
        before.SupernovaTick = -200;
        after = before; after.SampleTick++; after.SupernovaTick++;
        TestAssert.IsFalse(TurretStatePolicy.ShouldPublish(before, after));
        after.SupernovaTick = -200;
        TestAssert.IsTrue(TurretStatePolicy.ShouldPublish(before, after));
        before.TargetId = 0; before.SupernovaTick = 0;
        after = before; after.SampleTick += 36000;
        TestAssert.IsFalse(TurretStatePolicy.ShouldPublish(before, after));
    }

    [TestMethod]
    public void PredictedNativeHitsReturnUsableReferencesWithoutMutatingWorldState()
    {
        var previous = AuthorityRuleGuard.Probe;
        var harmony = new Harmony("nebulae.tests.native-prediction-damage");
        try
        {
            AuthorityRuleGuard.Probe = () => new AuthorityGuardSession(true, false, false);
            harmony.CreateClassProcessor(typeof(SkillSystem_Transpiler)).Patch();
            // No world or object pools exist: touching any native damage path would fail.
            var skills = Uninitialized<SkillSystem>();
            var target = new SkillTarget { id = 999, type = ETargetType.Enemy, astroId = 101 };
            var caster = new SkillTarget { id = 1, type = ETargetType.Player };
            ref var hit = ref skills.DamageObject(100, 1, ref target, ref caster);
            TestAssert.AreEqual(0, hit.id);
            hit.lastImpact.mass = 2;
            var local = new SkillTargetLocal { id = 999, type = ETargetType.Enemy };
            var localCaster = new SkillTargetLocal { id = 1, type = ETargetType.None };
            ref var groundHit = ref skills.DamageGroundObjectByLocalCaster(null, 100, 1, ref local, ref localCaster);
            TestAssert.AreEqual(0f, groundHit.lastImpact.mass);
            ref var remoteHit = ref skills.DamageGroundObjectByRemoteCaster(null, 100, 1, ref local, ref caster);
            TestAssert.AreEqual(0, remoteHit.id);
        }
        finally { harmony.UnpatchSelf(); AuthorityRuleGuard.Probe = previous; }
    }

    [TestMethod]
    public void NativeTurretShootsAndConsumesLocalAmmoWithoutAllocatingSharedCombatStats()
    {
        var previous = AuthorityRuleGuard.Probe;
        var harmony = new Harmony("nebulae.tests.native-turret-fire");
        try
        {
            AuthorityRuleGuard.Probe = () => new AuthorityGuardSession(true, false, false);
            harmony.CreateClassProcessor(typeof(SkillSystem_Patch)).Patch();
            var factory = Uninitialized<PlanetFactory>();
            AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, new PlanetData { id = 101 });
            factory.entityPool = new EntityData[2];
            factory.entityPool[1] = new EntityData { id = 1, pos = new Vector3(0, 200, 0), rot = Quaternion.identity };
            factory.enemyPool = new EnemyData[2];
            factory.enemyPool[1] = new EnemyData { id = 1, astroId = 101, pos = new VectorLF3(0, 200, 20) };
            factory.enemySystem = Uninitialized<EnemyDFGroundSystem>();
            factory.enemySystem.bases = new ObjectPool<DFGBaseComponent>();
            factory.enemySystem.bases.Reset();
            factory.sector = Uninitialized<SpaceSector>();
            factory.sector.galaxy = new GalaxyData();
            var skills = factory.sector.skillSystem = Uninitialized<SkillSystem>();
            factory.skillSystem = skills;
            skills.sector = factory.sector;
            skills.astroFactories = new PlanetFactory[102];
            skills.astroFactories[101] = factory;
            skills.turretGaussProjectiles = new DataPoolRenderer<LocalGeneralProjectile>();
            skills.turretGaussProjectiles.ResetPool();
            // Combat stats and audio are deliberately absent. This shot starts on muzzle 1.
            var turret = new TurretComponent
            {
                id = 1,
                entityId = 1,
                type = ETurretType.Gauss,
                target = new SkillTarget { id = 1, astroId = 101, type = ETargetType.Enemy },
                isLockingTarget = true,
                bulletCount = 20,
                bulletDamage = 100,
                itemCount = 5,
                muzzleIndex = 1,
                roundFire = 10000
            };
            var prefab = new PrefabDesc
            {
                turretROF = 1000,
                turretMuzzleCount = 2,
                turretMuzzleInterval = 5000,
                turretRoundInterval = 10000,
                turretDamageScale = 1
            };
            var upgrades = new CombatUpgradeData { kineticDamageScale = 1 };
            turret.Shoot(factory, null, prefab, null, 1, 100, ref upgrades);
            TestAssert.AreEqual(19, (int)turret.bulletCount);
            TestAssert.AreEqual(5, (int)turret.itemCount);
            TestAssert.AreEqual(1, skills.turretGaussProjectiles.count);
            ref var projectile = ref skills.turretGaussProjectiles.buffer[1];
            TestAssert.AreEqual(1, projectile.target.id);
            TestAssert.AreEqual(0, projectile.targetCombatStatId);
            TestAssert.AreEqual(0, factory.enemyPool[1].combatStatId);
        }
        finally { harmony.UnpatchSelf(); AuthorityRuleGuard.Probe = previous; }
    }

}
