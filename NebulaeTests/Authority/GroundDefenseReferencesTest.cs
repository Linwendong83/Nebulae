using HarmonyLib;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;
using NebulaeWorld.Combat;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class GroundDefenseReferencesTest
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PrecisionDronePlasmaRequiresALiveLocalEnemyAndSkillSlot(bool shield)
    {
        var pool = new DataPoolRenderer<LocalGeneralProjectile>(); pool.ResetPool(); pool.Add();
        var type = shield ? ESkillType.FighterShieldPlasmas : ESkillType.FighterPlasmas;
        if (shield) skills.fighterShieldPlasmas = pool; else skills.fighterPlasmas = pool;
        pool.buffer[1] = new LocalGeneralProjectile
        {
            id = 1, life = 10, astroId = 101,
            target = new SkillTargetLocal { id = 3, type = ETargetType.Enemy }
        };
        var skill = new SKILLID(1, type);
        TestAssert.IsTrue(GroundDefenseReferences.Valid(skill, skills, factory.enemyPool, 101));
        pool.buffer[1].target.id = 65536;
        owner.AddIncomingSkill(1, type);
        TestAssert.AreEqual(0, GroundDefenseReferences.ValidId(ref skill, skills, factory.enemyPool,
            factory.enemySystem.bases.buffer, ref factory.enemyPool[3]));
        TestAssert.AreEqual(0, owner.incomingSkillsCursor);
        TestAssert.AreEqual(0, skill.id);
    }

    [TestMethod]
    public void ProjectileConsumptionRejectsStaleIndexesWithoutChangingValidShots()
    {
        var shots = new LocalGeneralProjectile[8];
        shots[3] = new LocalGeneralProjectile
        {
            id = 3, life = 10, astroId = 101,
            target = new SkillTargetLocal { id = 3, type = ETargetType.Enemy }
        };
        TestAssert.AreEqual(3, GroundDefenseReferences_Patch.Snapshot(shots, 3).target.id);
        foreach (var id in new[] { -1, 0, 2, 8, 65536 })
            TestAssert.AreEqual(0, GroundDefenseReferences_Patch.Snapshot(shots, id).target.id);
        TestAssert.AreEqual(0, GroundDefenseReferences_Patch.Snapshot<LocalGeneralProjectile>(null!, 3).id);
        TestAssert.IsNull(GroundDefenseReferences_Patch.BaseAt(factory.enemySystem.bases.buffer, 65536));
        TestAssert.AreSame(owner, GroundDefenseReferences_Patch.BaseAt(factory.enemySystem.bases.buffer, 1));

        var native = AccessTools.Method(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.RunBehavior_Defense_Ground));
        var code = GroundDefenseReferences_Patch.Transpiler(PatchProcessor.GetOriginalInstructions(native)).ToArray();
        TestAssert.AreEqual(7, code.Count(i => i.operand is System.Reflection.MethodInfo m && m.Name == "Snapshot"));
        TestAssert.AreEqual(1, code.Count(i => i.operand is System.Reflection.MethodInfo m && m.Name == "BaseAt"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PatchedNativeDefensePreservesPrecisionDroneInterception(bool shield)
    {
        var prefabs = SpaceSector.PrefabDescByModelIndex;
        var harmony = new Harmony("nebulae.tests.ground-defense-drone");
        try
        {
            SetStatic(typeof(SpaceSector), nameof(SpaceSector.PrefabDescByModelIndex),
                new[] { new PrefabDesc { unitMaxMovementSpeed = 10, unitMaxMovementAcceleration = 10 } });
            harmony.CreateClassProcessor(typeof(GroundDefenseReferences_Patch)).Patch();
            var pool = new DataPoolRenderer<LocalGeneralProjectile>(); pool.ResetPool(); pool.Add();
            if (shield) skills.fighterShieldPlasmas = pool; else skills.fighterPlasmas = pool;
            pool.buffer[1] = new LocalGeneralProjectile
            {
                id = 1, life = 10, astroId = 101, pos = new Vector3(0, 200, 0),
                target = new SkillTargetLocal { id = 3, type = ETargetType.Enemy }
            };
            ref var unit = ref factory.enemySystem.units.buffer[1];
            unit.blockSkill = new SKILLID(1, shield ? ESkillType.FighterShieldPlasmas : ESkillType.FighterPlasmas);
            unit.RunBehavior_Defense_Ground(0, skills, factory.enemyPool, factory.enemySystem.bases.buffer,
                0, ref factory.enemyPool[3]);
            TestAssert.AreEqual(29, unit.stateTick);
            TestAssert.AreEqual(1, unit.blockSkill.id);
            TestAssert.AreEqual(3, pool.buffer[1].target.id);
        }
        finally
        {
            harmony.UnpatchSelf();
            SetStatic(typeof(SpaceSector), nameof(SpaceSector.PrefabDescByModelIndex), prefabs);
        }
    }

    [TestMethod]
    public void PatchedNativeDefenseRejectsADroneSlotThatIsStaleAtConsumption()
    {
        var prefabs = SpaceSector.PrefabDescByModelIndex;
        var session = Multiplayer.Session;
        var harmony = new Harmony("nebulae.tests.ground-defense-stale-drone");
        try
        {
            SetStatic(typeof(SpaceSector), nameof(SpaceSector.PrefabDescByModelIndex),
                new[] { new PrefabDesc { unitMaxMovementSpeed = 10, unitMaxMovementAcceleration = 10 } });
            // Bypass the earlier multiplayer validation to exercise a reference that became
            // invalid between validation and the actual native pool read.
            Multiplayer.Session = null!;
            skills.fighterPlasmas = new DataPoolRenderer<LocalGeneralProjectile>();
            skills.fighterPlasmas.ResetPool();
            factory.enemySystem.units.buffer[1].blockSkill = new SKILLID(65536, ESkillType.FighterPlasmas);
            TestAssert.ThrowsExactly<IndexOutOfRangeException>(() => RunDefense());
            harmony.CreateClassProcessor(typeof(GroundDefenseReferences_Patch)).Patch();
            RunDefense();
            TestAssert.AreEqual(19, factory.enemySystem.units.buffer[1].stateTick);
            TestAssert.AreEqual(0, skills.fighterPlasmas.cursor - 1);
        }
        finally
        {
            harmony.UnpatchSelf();
            Multiplayer.Session = session;
            SetStatic(typeof(SpaceSector), nameof(SpaceSector.PrefabDescByModelIndex), prefabs);
        }
    }

    private void RunDefense() => factory.enemySystem.units.buffer[1].RunBehavior_Defense_Ground(
        0, skills, factory.enemyPool, factory.enemySystem.bases.buffer, 0, ref factory.enemyPool[3]);

    [TestMethod]
    public void ArrayAccessRechecksARetargetedShotAndPreservesValidReferences()
    {
        var enemies = new EnemyData[8]; enemies[3].id = 3;
        TestAssert.AreEqual(0, GroundDefenseReferences_Patch.EnemyAt(enemies, 65536).id);
        TestAssert.AreEqual(0, GroundDefenseReferences_Patch.EnemyAt(enemies, -1).id);
        GroundDefenseReferences_Patch.EnemyAt(enemies, 3).owner = 7;
        TestAssert.AreEqual(7, enemies[3].owner);
        var native = AccessTools.Method(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.RunBehavior_Defense_Ground));
        var code = GroundDefenseReferences_Patch.Transpiler(PatchProcessor.GetOriginalInstructions(native)).ToArray();
        TestAssert.IsFalse(code.Any(i => i.opcode == System.Reflection.Emit.OpCodes.Ldelema && Equals(i.operand, typeof(EnemyData))));
    }
    private GameData previousGame = null!;
    private MultiplayerSession previousSession = null!;
    private SkillSystem skills = null!;
    private PlanetFactory factory = null!;
    private DFGBaseComponent owner = null!;

    [TestInitialize]
    public void Setup()
    {
        previousGame = GameMain.data; previousSession = Multiplayer.Session;
        factory = Uninitialized<PlanetFactory>();
        factory.enemyPool = new EnemyData[8]; factory.enemyCursor = 4;
        factory.enemyPool[3] = new EnemyData
        {
            id = 3,
            originAstroId = 101,
            owner = 1,
            modelIndex = 0,
            pos = new VectorLF3(0, 200, 0),
            rot = new Quaternion(0, 0, 0, 1)
        };
        var system = factory.enemySystem = Uninitialized<EnemyDFGroundSystem>();
        system.bases = new ObjectPool<DFGBaseComponent>(); system.bases.Reset(); owner = system.bases.Add();
        owner.incomingSkills = new SKILLID[16];
        system.units = new DataPool<EnemyUnitComponent>(); system.units.Reset();
        system.units.Add(); system.units.buffer[1] = new EnemyUnitComponent
        {
            id = 1,
            enemyId = 3,
            stateTick = 20,
            behavior = EEnemyBehavior.Defense,
            blockSkill = new SKILLID(1, ESkillType.MechaMissiles)
        };
        skills = Uninitialized<SkillSystem>();
        skills.astroFactories = new PlanetFactory[102]; skills.astroFactories[101] = factory;
        skills.mechaMissiles = new DataPool<GeneralMissile>(); skills.mechaMissiles.Reset(); skills.mechaMissiles.Add();
        skills.turretMissiles = new DataPool<GeneralMissile>(); skills.turretMissiles.Reset(); skills.turretMissiles.Add();
        var game = Uninitialized<GameData>(); game.spaceSector = Uninitialized<SpaceSector>();
        game.spaceSector.skillSystem = skills; skills.sector = game.spaceSector;
        GameMain.data = game;
        Multiplayer.Session = Uninitialized<MultiplayerSession>();
    }

    [TestCleanup]
    public void Cleanup() { GameMain.data = previousGame; Multiplayer.Session = previousSession; }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ClearedMissileTargetRetiresBaseAndUnitLinksBeforeThePlanetIsForgotten(bool player)
    {
        var type = player ? ESkillType.MechaMissiles : ESkillType.TurretMissiles;
        owner.AddIncomingSkill(1, type); factory.enemySystem.units.buffer[1].blockSkill = new SKILLID(1, type);
        var missile = new GeneralMissile
        {
            id = 1,
            target = new SkillTarget { id = 0, astroId = 101, type = ETargetType.Enemy },
            caster = new SkillTarget { type = player ? ETargetType.Player : ETargetType.None }
        };
        EnemyTargetIdentity.ValidateMissile(skills, ref missile);
        TestAssert.AreEqual(0, owner.incomingSkillsCursor);
        TestAssert.AreEqual(0, factory.enemySystem.units.buffer[1].blockSkill.id);
        TestAssert.AreEqual(0, missile.target.astroId);
        skills.astroFactories[101] = null!;
        TestAssert.IsTrue(EnemyTargetIdentity.ValidateMissile(skills, ref missile), "Already clean and unloaded must remain safe.");
    }

    [TestMethod]
    public void DirectPoolRemovalRetiresReferencesBeforeTheSlotCanBeReused()
    {
        var harmony = new Harmony("nebulae.tests.ground-defense-retire");
        try
        {
            harmony.CreateClassProcessor(typeof(MissileTargetRetirement_Patch)).Patch();
            skills.mechaMissiles.buffer[1] = new GeneralMissile { id = 1, target = new SkillTarget { astroId = 101 } };
            owner.AddIncomingSkill(1, ESkillType.MechaMissiles);
            skills.mechaMissiles.Remove(1);
            TestAssert.AreEqual(0, owner.incomingSkillsCursor);
            TestAssert.AreEqual(0, factory.enemySystem.units.buffer[1].blockSkill.id);
        }
        finally { harmony.UnpatchSelf(); }
    }

    [TestMethod]
    public void GuardianRetargetKeepsInterceptionUntilTheMissileLeavesItsPlanet()
    {
        var planet = new PlanetData { id = 101, factory = factory };
        GameMain.data.galaxy = new GalaxyData { stars = [new StarData { planets = [planet] }] };
        factory.enemyPool[4] = new EnemyData { id = 4 }; factory.enemyCursor = 5;
        GameMain.spaceSector.enemyPool = new EnemyData[8]; GameMain.spaceSector.enemyPool[4].id = 4;
        GameMain.spaceSector.enemyCursor = 5;
        owner.AddIncomingSkill(1, ESkillType.MechaMissiles);
        var missile = new GeneralMissile
        {
            id = 1,
            life = 30,
            caster = new SkillTarget { type = ETargetType.Player },
            target = new SkillTarget { id = 3, astroId = 101, type = ETargetType.Enemy }
        };
        TestAssert.IsTrue(EnemyTargetIdentity.ValidateMissile(skills, ref missile));
        missile.target.id = 4;
        TestAssert.IsTrue(EnemyTargetIdentity.ValidateMissile(skills, ref missile));
        TestAssert.AreEqual(1, owner.incomingSkillsCursor);
        TestAssert.AreEqual(1, factory.enemySystem.units.buffer[1].blockSkill.id);
        missile.target.astroId = 0;
        TestAssert.IsTrue(EnemyTargetIdentity.ValidateMissile(skills, ref missile));
        TestAssert.AreEqual(0, owner.incomingSkillsCursor);
        TestAssert.AreEqual(0, factory.enemySystem.units.buffer[1].blockSkill.id);
    }

    [TestMethod]
    [DataRow(65536, 101, 1)]
    [DataRow(3, 1000001, 1)]
    [DataRow(0, 101, 1)]
    [DataRow(3, 101, 0)]
    public void InterceptionRejectsOutOfRangeSpaceDeadAndReusedSkillSlots(int target, int astro, int slot)
    {
        owner.AddIncomingSkill(1, ESkillType.MechaMissiles);
        skills.mechaMissiles.buffer[1] = new GeneralMissile
        {
            id = slot,
            life = 10,
            target = new SkillTarget { id = target, astroId = astro, type = ETargetType.Enemy }
        };
        ref var unit = ref factory.enemySystem.units.buffer[1];
        TestAssert.AreEqual(0, GroundDefenseReferences.ValidId(ref unit.blockSkill, skills, factory.enemyPool,
            factory.enemySystem.bases.buffer, ref factory.enemyPool[3]));
        TestAssert.AreEqual(0, unit.blockSkill.id); TestAssert.AreEqual(0, owner.incomingSkillsCursor);
    }

    [TestMethod]
    public void ValidInterceptionIsKeptAndNativeChecksAlsoValidateANewBaseListSelection()
    {
        skills.mechaMissiles.buffer[1] = new GeneralMissile
        {
            id = 1,
            life = 10,
            target = new SkillTarget { id = 3, astroId = 101, type = ETargetType.Enemy }
        };
        ref var unit = ref factory.enemySystem.units.buffer[1];
        TestAssert.AreEqual(1, GroundDefenseReferences.ValidId(ref unit.blockSkill, skills, factory.enemyPool,
            factory.enemySystem.bases.buffer, ref factory.enemyPool[3]));
        var method = AccessTools.Method(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.RunBehavior_Defense_Ground));
        var code = GroundDefenseReferences_Patch.Transpiler(PatchProcessor.GetOriginalInstructions(method)).ToArray();
        TestAssert.IsGreaterThanOrEqualTo(3, code.Count(i => i.operand is System.Reflection.MethodInfo m && m.Name == "ValidId"));
        var harmony = new Harmony("nebulae.tests.ground-defense-native");
        try { harmony.CreateClassProcessor(typeof(GroundDefenseReferences_Patch)).Patch(); }
        finally { harmony.UnpatchSelf(); }
    }
}
