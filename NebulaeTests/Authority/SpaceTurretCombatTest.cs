using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeModel.Utils;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;
using NebulaeWorld.Combat;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class SpaceTurretCombatTest
{
    private Harmony harmony = null!;
    private GameData oldGame = null!;
    private MultiplayerSession oldSession = null!, session = null!;
    private EnemyDFHiveSystem hive = null!;
    private SpaceSector sector = null!;
    private SkillSystem skills = null!;

    private static CombatManager.PlayerPosition Pilot(ushort id, double radius, bool alive = true, int planet = 0, int star = 1)
    {
        var player = Uninitialized<Player>(); player.uPosition = new VectorLF3(radius, 0, 0);
        player.uVelocity = new VectorLF3(0, 2000, 0);
        var mecha = new Mecha { player = player }; player.SetHiddenProperty(nameof(Player.mecha), mecha);
        return new CombatManager.PlayerPosition
        {
            id = id,
            uPosition = player.uPosition,
            skillTargetU = player.uPosition,
            starId = star,
            planetId = planet,
            isAlive = alive,
            mecha = mecha
        };
    }
    private static DFSTurretComponent Turret() => new()
    {
        id = 1,
        enemyId = 1,
        builderId = 0,
        attackRange = 5000,
        sensorRange = 5000,
        localDir = Vector3.up,
        state = EDFTurretState.Searching
    };

    [TestInitialize]
    public void SetUp()
    {
        oldGame = GameMain.data; oldSession = Multiplayer.Session;
        session = Uninitialized<MultiplayerSession>(); session.Combat = new CombatManager();
        session.LocalPlayer = new LocalPlayer { Data = new PlayerData(1, 101) };
        session.Combat.Players = [Pilot(9, 22000)]; session.Combat.IndexByPlayerId[9] = 0;
        session.Combat.ActivedStars.Add(1); Multiplayer.Session = session;
        var game = Uninitialized<GameData>(); GameMain.data = game;
        sector = game.spaceSector = Uninitialized<SpaceSector>();
        sector.enemyPool = new EnemyData[2]; sector.enemyPool[1] = new EnemyData { id = 1, originAstroId = 1000001, rot = Quaternion.identity };
        sector.craftPool = new CraftData[2]; sector.craftCursor = 1;
        sector.astros = new AstroData[2]; sector.astros[1] = new AstroData
        {
            uRot = Quaternion.identity,
            uRotNext = Quaternion.identity,
            uPos = new VectorLF3(20000, 0, 0),
            uPosNext = new VectorLF3(20000, 0, 0)
        };
        hive = Uninitialized<EnemyDFHiveSystem>(); hive.sector = sector; hive.hiveAstroId = 1000001;
        hive.starData = new StarData { id = 1 }; hive.orbitRadius = 20000;
        sector.maxHiveCount = 2; sector.dfHivesByAstro = new EnemyDFHiveSystem[2]; sector.dfHivesByAstro[1] = hive;
        skills = sector.skillSystem = Uninitialized<SkillSystem>();
        skills.playerSkillTargetU = new VectorLF3(-12345, 0, 0); skills.playerVelocityU = new Vector3(999, 999, 999);
        skills.dfsTowerPlasmas = new DataPoolRenderer<GeneralProjectile>(); skills.dfsTowerPlasmas.ResetPool();
        harmony = new Harmony("nebulae.tests.space-turret-combat");
        harmony.CreateClassProcessor(typeof(SpaceTurretPlayerInput_Patch)).Patch();
        harmony.CreateClassProcessor(typeof(DFSTurretComponent_Patch)).Patch();
    }
    [TestCleanup]
    public void CleanUp() { harmony.UnpatchSelf(); Multiplayer.Session = oldSession; GameMain.data = oldGame; }

    [TestMethod]
    public void DedicatedCameraCannotDisableTurretsForAnAliveRemotePilot()
    {
        var turret = Turret(); turret.state = EDFTurretState.None;
        var builders = new[] { new EnemyBuilderComponent { state = 3 } };
        turret.LogicTick(builders, EAggressiveLevel.Normal, false);
        TestAssert.AreEqual(EDFTurretState.Searching, turret.state);
        session.Combat.Players[0].isAlive = false;
        turret.LogicTick(builders, EAggressiveLevel.Normal, true);
        TestAssert.AreEqual(EDFTurretState.None, turret.state);
    }

    [TestMethod]
    [DataRow(2000.0, true)]
    [DataRow(3999.0, true)]
    [DataRow(4001.0, false)]
    public void SearchUsesTheNativeRadialDistanceInMetres(double radialOffset, bool expected)
    {
        session.Combat.Players[0] = Pilot(9, 20000 + radialOffset);
        var turret = Turret();
        TestAssert.AreEqual(expected, turret.SearchTarget(hive, EAggressiveLevel.Normal));
        TestAssert.AreEqual(expected ? 9 : 0, turret.target.id);
    }

    [TestMethod]
    [DataRow(false, 0, 1)]
    [DataRow(true, 101, 1)]
    [DataRow(true, 0, 2)]
    public void SearchRejectsDeadGroundedAndOtherSystemPilots(bool alive, int planet, int star)
    {
        session.Combat.Players[0] = Pilot(9, 22000, alive, planet, star);
        var turret = Turret(); TestAssert.IsFalse(turret.SearchTarget(hive, EAggressiveLevel.Normal));
    }

    [TestMethod]
    public void PlasmaLeadsTheSelectedHighSpeedPilotAndLeavesSharedInputsUnchanged()
    {
        var turret = Turret(); turret.target = new SkillTargetLocal { id = 9, type = ETargetType.Player };
        turret.Shoot_Plasma(hive, new PrefabDesc { dfTurretAttackDamage = 24000 });
        ref var shot = ref skills.dfsTowerPlasmas.buffer[1];
        TestAssert.AreEqual(10000f, shot.uVel.x, 0.01f); TestAssert.AreEqual(2000f, shot.uVel.y, 0.01f);
        TestAssert.AreEqual(9, shot.target.id);
        TestAssert.AreEqual(-12345.0, skills.playerSkillTargetU.x); TestAssert.AreEqual(999f, skills.playerVelocityU.y);
        session.Combat.Players[0].mecha.player.uVelocity = new VectorLF3(0, -1000, 0);
        turret.Shoot_Plasma(hive, new PrefabDesc());
        TestAssert.AreEqual(-1000f, skills.dfsTowerPlasmas.buffer[2].uVel.y, 0.01f);
    }

    [TestMethod]
    public void RangeCheckDoesNotRewriteGlobalPlayerPose()
    {
        var turret = Turret();
        TestAssert.IsTrue(turret.TargetIsInRange(hive, new SkillTargetLocal { id = 9, type = ETargetType.Player }, 5000, 5000, out _));
        TestAssert.AreEqual(-12345.0, skills.playerSkillTargetU.x);
        TestAssert.AreEqual(0.0, hive.player_local_pos.x);
    }

    [TestMethod]
    public void AimResolvesTheTargetIdWithoutUsingThePreviousTurretsPlayer()
    {
        session.Combat.Players = [Pilot(9, 22000), Pilot(7, 18000)]; session.Combat.IndexByPlayerId[7] = 1;
        var turret = Turret(); var target = new SkillTargetLocal { id = 7, type = ETargetType.Player };
        var pos = VectorLF3.zero; var rot = Quaternion.identity;
        TestAssert.AreEqual(-2000f, SpaceTurretPlayerInput_Patch.Aim(ref turret, hive, ref target, ref pos, ref rot).x, 0.01f);
        target.id = 9;
        TestAssert.AreEqual(2000f, SpaceTurretPlayerInput_Patch.Aim(ref turret, hive, ref target, ref pos, ref rot).x, 0.01f);
        TestAssert.AreEqual(0.0, hive.player_local_pos.x);
    }

    [TestMethod]
    public void SinglePlayerPlasmaRetainsNativePositionAndVelocityInputs()
    {
        Multiplayer.Session = null!;
        skills.playerSkillTargetU = new VectorLF3(22000, 0, 0); skills.playerVelocityU = new Vector3(0, 500, 0);
        var turret = Turret(); turret.target = new SkillTargetLocal { id = 1, type = ETargetType.Player };
        turret.Shoot_Plasma(hive, new PrefabDesc());
        TestAssert.AreEqual(10000f, skills.dfsTowerPlasmas.buffer[1].uVel.x, 0.01f);
        TestAssert.AreEqual(500f, skills.dfsTowerPlasmas.buffer[1].uVel.y, 0.01f);
    }

    [TestMethod]
    public void NativeAimFiresAtTheRemoteSailingPilotWithAValidBuilder()
    {
        var oldPrefabs = SpaceSector.PrefabDescByModelIndex;
        try
        {
            SpaceSector.PrefabDescByModelIndex = [new PrefabDesc { dfTurretAttackEnergy = 1, dfTurretAttackDamage = 24000 }];
            session.Combat.Players[0].mecha.player.movementState = EMovementState.Sail;
            var camera = Uninitialized<Player>(); camera.SetHiddenProperty(nameof(Player.mecha), new Mecha());
            GameMain.data.SetHiddenProperty(nameof(GameData.mainPlayer), camera);
            hive.evolve = new EvolveData();
            sector.enemyPool[1].pos = new VectorLF3(2000, -1000, 0);
            harmony.CreateClassProcessor(typeof(NativeDarkFogTurret_Patch)).Patch();
            var turret = Turret(); turret.state = EDFTurretState.Aiming; turret.type = EDFTurretType.Plasma;
            turret.target = new SkillTargetLocal { id = 9, type = ETargetType.Player };
            turret.Aim(hive, [new EnemyBuilderComponent { state = 3, energy = 1000 }], EAggressiveLevel.Normal);
            TestAssert.AreEqual(1, skills.dfsTowerPlasmas.count);
            TestAssert.IsGreaterThan(2000f, skills.dfsTowerPlasmas.buffer[1].uVel.y);
        }
        finally { SpaceSector.PrefabDescByModelIndex = oldPrefabs; }
    }

    [TestMethod]
    public void SpaceBodyAndShieldShareTheAcceptedUniversalPoseAcrossDifferentRenderOrigins()
    {
        var frame = new RemoteCombatPose.Frame(new Vector3(-100, 200, 300), Quaternion.identity,
            new VectorLF3(10000, 20000, 30000), Quaternion.identity);
        var offset = new Vector3(0, 1, 0);
        var origin = new VectorLF3(10000, 0, 30000);
        var rotation = new Quaternion(0, 0, 1, 0);
        var local = RemoteCombatPose.SimulationLocalPoint(frame, offset, true, origin, rotation);
        var world = Maths.QRotateLF(rotation, local) + origin;
        TestAssert.AreEqual(10000.0, world.x, 0.001); TestAssert.AreEqual(20001.0, world.y, 0.001); TestAssert.AreEqual(30000.0, world.z, 0.001);
        var ground = RemoteCombatPose.SimulationLocalPoint(frame, offset, false, origin, rotation);
        TestAssert.AreEqual(-100f, ground.x); TestAssert.AreEqual(201f, ground.y); TestAssert.AreEqual(300f, ground.z);
    }

    [TestMethod]
    public void HighSpeedAcceptedPoseCannotLeaveTheShieldBehindTheCurrentBody()
    {
        var view = new CombatManager.PlayerPosition { skillTargetULast = new VectorLF3(200, 0, 0), skillTargetU = new VectorLF3(400, 0, 0) };
        var projectile = new VectorLF3(300, 0, 0); var end = new VectorLF3(700, 0, 0); var current = new VectorLF3(600, 0, 0);
        var oldOrigin = projectile + (view.skillTargetU - view.skillTargetULast); var direction = (end - oldOrigin).normalized;
        TestAssert.IsFalse(Phys.RayCastSphereLF(oldOrigin, direction, (end - oldOrigin).magnitude, view.skillTargetU, 60, out _),
            "The previous-frame shield is behind the compensated ray, although the body at x=600 is hit.");
        SkillSystem_Common_Patch.SetCollisionPose(skills, ref view, new Vector3(600, 0, 0), current);
        var origin = projectile + (skills.playerSkillTargetU - skills.playerSkillTargetULast); direction = (end - origin).normalized;
        TestAssert.IsTrue(Phys.RayCastSphereLF(origin, direction, (end - origin).magnitude, skills.playerSkillTargetU, 60, out _));
        TestAssert.AreEqual(400.0, skills.playerSkillTargetULast.x);
        TestAssert.AreEqual(600.0, skills.playerSkillTargetU.x);
    }
}
