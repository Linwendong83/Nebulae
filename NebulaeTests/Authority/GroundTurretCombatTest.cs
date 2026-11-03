using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Players;
using NebulaeWorld;
using NebulaeWorld.Combat;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class GroundTurretCombatTest
{
    private Harmony harmony = null!;
    private MultiplayerSession previousSession = null!, session = null!;
    private GameData previousData = null!;
    private PrefabDesc[] previousPrefabs = null!;
    private PlanetFactory factory = null!;
    private DFGBaseComponent core = null!;
    private SkillSystem skills = null!;
    private EnemyBuilderComponent[] builders = null!;

    private static CombatManager.PlayerPosition Pilot(ushort id, float x, int planet = 101) => new()
    { id = id, planetId = planet, isAlive = true, skillTargetL = new Vector3(x, 210, 0), velocityL = new Vector3(id, 0, 1) };
    private static DFGTurretComponent Turret(EDFTurretType type = EDFTurretType.Plasma) => new()
    {
        id = 1,
        enemyId = 2,
        baseId = 1,
        builderId = 0,
        attackRange = 10,
        sensorRange = 20,
        enemyPos = new Vector3(0, 210, 0),
        muzzleWPos = new Vector3(0, 210, 0),
        enemyRot = Quaternion.identity,
        localDir = Vector3.right,
        type = type,
        state = EDFTurretState.Searching
    };

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session; previousData = GameMain.data; previousPrefabs = SpaceSector.PrefabDescByModelIndex;
        session = Uninitialized<MultiplayerSession>(); session.Combat = new CombatManager(); session.DarkFogTurrets = new NativeDarkFogTurrets();
        session.LocalPlayer = new LocalPlayer { Data = new PlayerData(1, 101) };
        session.Combat.Players = [Pilot(9, 5), Pilot(7, 60)];
        Multiplayer.Session = session;
        factory = Uninitialized<PlanetFactory>();
        AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, new PlanetData { id = 101, radius = 200, scale = 1 });
        factory.enemyPool = new EnemyData[4]; factory.enemyCursor = 3;
        factory.enemyPool[1] = new EnemyData { id = 1, counterAttack = true };
        factory.enemyPool[2] = new EnemyData { id = 2, pos = new VectorLF3(0, 210, 0), rot = Quaternion.identity };
        factory.entityPool = new EntityData[3]; factory.craftPool = new CraftData[3];
        factory.entityPool[1] = new EntityData { id = 1, pos = new Vector3(4, 210, 0) };
        factory.craftPool[1] = new CraftData { id = 1, pos = new VectorLF3(3, 210, 0) };
        core = new DFGBaseComponent { id = 1, enemyId = 1 };
        core.hatred.HateTarget(ETargetType.Player, 7, 1000, EHatredOperation.Set);
        skills = factory.skillSystem = Uninitialized<SkillSystem>();
        skills.playerSkillTargetL = new Vector3(999, 210, 0); skills.playerVelocityL = new Vector3(999, 999, 999);
        AccessTools.Field(typeof(SkillSystem), "fire_lock").SetValue(skills, new object());
        skills.dfgTowerLasers = new DataPoolRenderer<LocalLaserOneShot>(); skills.dfgTowerLasers.ResetPool();
        skills.dfgTowerPlasmas = new DataPoolRenderer<LocalGeneralProjectile>(); skills.dfgTowerPlasmas.ResetPool();
        var data = Uninitialized<GameData>(); data.spaceSector = Uninitialized<SpaceSector>(); data.spaceSector.skillSystem = skills;
        GameMain.data = data; SetBackingField(factory, "gameData", data);
        AccessTools.Field(typeof(SpaceSector), nameof(SpaceSector.PrefabDescByModelIndex)).SetValue(null,
            new[] { new PrefabDesc { dfTurretAttackEnergy = 10, dfTurretAttackInterval = 30, dfTurretAttackDamage = 50, dfTurretAttackHeat = 20 } });
        builders = [new EnemyBuilderComponent { state = 4, energy = 1000 }];
        harmony = new Harmony("nebulae.tests.ground-turret-combat");
        harmony.CreateClassProcessor(typeof(DFGTurretComponent_Patch)).Patch();
        harmony.CreateClassProcessor(typeof(GroundTurretBaseTarget_Patch)).Patch();
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchSelf(); NativeDarkFogTurrets.Active = false;
        Multiplayer.Session = previousSession; GameMain.data = previousData;
        AccessTools.Field(typeof(SpaceSector), nameof(SpaceSector.PrefabDescByModelIndex)).SetValue(null, previousPrefabs);
    }

    [TestMethod]
    [DataRow(80f, 10f, true)]
    [DataRow(200f, 30f, true)]
    [DataRow(340f, 30f, false)]
    public void OcclusionUsesTheSimulatedPlanetEvenWhenTheCameraIsElsewhere(float radius, float angle, bool visible)
    {
        factory.planet.radius = radius;
        factory.enemyPool[1].counterAttack = false;
        var height = radius + 5;
        var radians = angle * (float)Math.PI / 180;
        var origin = new Vector3(0, height, 0);
        var target = new Vector3(height * (float)Math.Sin(radians), height * (float)Math.Cos(radians), 0);
        session.Combat.Players = [new CombatManager.PlayerPosition
            { id = 9, planetId = 101, isAlive = true, skillTargetL = target }];
        foreach (var camera in new PlanetData?[] { null, new() { radius = 80, scale = 1 }, new() { radius = 340, scale = 1 } })
            foreach (var type in new[] { EDFTurretType.Laser, EDFTurretType.Plasma })
            {
                SetBackingField(GameMain.data, "localPlanet", camera!);
                var turret = Turret(type);
                turret.attackRange = turret.sensorRange = 1000;
                turret.enemyPos = turret.muzzleWPos = origin;
                turret.localDir = target - origin;
                turret.target = new SkillTargetLocal { type = ETargetType.Player, id = 9 };
                turret.Aim(factory, null, null, EAggressiveLevel.Normal);
                TestAssert.AreEqual(visible, turret.targetInShootRange, $"R={radius}, camera={camera?.realRadius}, type={type}");
            }
    }

    [TestMethod]
    [DataRow(EAggressiveLevel.Normal)]
    [DataRow(EAggressiveLevel.Passive)]
    public void RepeatedSearchKeepsTheHatedRemotePlayerInsteadOfPlayerOne(EAggressiveLevel level)
    {
        var turret = Turret();
        for (var i = 0; i < 4; i++)
        {
            TestAssert.IsTrue(turret.SearchTarget(factory, core, level));
            TestAssert.AreEqual(7, turret.target.id);
        }
        TestAssert.IsTrue(factory.enemyPool[1].counterAttack);
        TestAssert.IsTrue(turret.TargetIsInRange(factory, turret.target, 1, 1, out var distance, core, level));
        TestAssert.AreEqual(3600f, distance);
    }

    [TestMethod]
    public void HatredTiesUsePlayerIdAndInvalidPlayersAreSkipped()
    {
        core.hatred.HateTarget(ETargetType.Player, 9, 1000, EHatredOperation.Set);
        TestAssert.AreEqual(7, GroundTurretCombat.RetaliationPlayer(factory, core));
        session.Combat.Players[1].isAlive = false;
        TestAssert.AreEqual(9, GroundTurretCombat.RetaliationPlayer(factory, core));
        session.Combat.Players[0].planetId = 202;
        TestAssert.AreEqual(0, GroundTurretCombat.RetaliationPlayer(factory, core));
    }

    [TestMethod]
    public void DeadCameraPlayerCannotEraseLiveRemotePlayerHatred()
    {
        core.hatred.HateTarget(ETargetType.Player, 9, 500, EHatredOperation.Set);
        core.PostGameTick(factory, new Player { isAlive = false });
        TestAssert.AreEqual(7, GroundTurretCombat.RetaliationPlayer(factory, core));
        var turret = Turret(); TestAssert.IsTrue(turret.SearchTarget(factory, core, EAggressiveLevel.Normal));
        core.PostGameTick(factory, new Player { isAlive = false });
        TestAssert.IsTrue(turret.TargetIsInRange(factory, turret.target, 20, 10, out _, core, EAggressiveLevel.Normal));
        session.Combat.Players[1].isAlive = false; session.Combat.Players[0].planetId = 202;
        core.hatred.HateTarget(ETargetType.None, 1, 50, EHatredOperation.Set);
        core.hatred.HateTarget(ETargetType.None, 99999, 70, EHatredOperation.Set);
        core.hatred.HateTarget(ETargetType.Craft, 1, 80, EHatredOperation.Set); factory.craftPool[1].isInvincible = true;
        core.PostGameTick(factory, new Player { isAlive = true });
        TestAssert.AreEqual(ETargetType.None, core.hatred.max.targetType);
        TestAssert.AreEqual(1, core.hatred.max.objectId); TestAssert.AreEqual(50, core.hatred.max.value);
        TestAssert.AreEqual(0, GroundTurretCombat.RetaliationPlayer(factory, core));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(55)]
    public void MissingDeadAndCrossPlanetPlayersNeverResolveToCameraPlayer(int id)
    {
        session.Combat.Players[1].planetId = 202;
        session.Combat.Players[0].isAlive = false;
        var turret = Turret(); var target = new SkillTargetLocal { type = ETargetType.Player, id = id };
        TestAssert.IsFalse(turret.TargetIsInRange(factory, target, 10000, 10000, out _, core, EAggressiveLevel.Normal));
        session.Combat.Players = [];
        TestAssert.IsFalse(turret.SearchTarget(factory, core, EAggressiveLevel.Normal));
        TestAssert.AreEqual(0, turret.target.id);
    }

    [TestMethod]
    public void OrdinaryAndPassiveRulesRespectRangesAndCounterAttackDoesNotExtendCraftRange()
    {
        var turret = Turret(); factory.enemyPool[1].counterAttack = false;
        TestAssert.IsTrue(turret.SearchTarget(factory, core, EAggressiveLevel.Normal)); TestAssert.AreEqual(9, turret.target.id);
        TestAssert.IsFalse(turret.TargetIsInRange(factory, turret.target, 4, 100, out _, core, EAggressiveLevel.Normal));
        TestAssert.IsTrue(turret.TargetIsInRange(factory, turret.target, 5, 1, out _, core, EAggressiveLevel.Normal));
        TestAssert.IsFalse(turret.SearchTarget(factory, core, EAggressiveLevel.Passive));
        factory.enemyPool[1].counterAttack = true;
        factory.craftPool[1].pos = new VectorLF3(100, 210, 0);
        TestAssert.IsFalse(turret.TargetIsInRange(factory, new SkillTargetLocal { type = ETargetType.Craft, id = 1 }, 1000, 10, out _, core, EAggressiveLevel.Normal));
        TestAssert.IsFalse(turret.SearchTarget(factory, core, EAggressiveLevel.Dummy));
    }

    [TestMethod]
    [DataRow(EDFTurretType.Laser)]
    [DataRow(EDFTurretType.Plasma)]
    public void NativeShotsUseReselectedTargetWithoutChangingSharedPlayerInputs(EDFTurretType type)
    {
        var turret = Turret(type); turret.target = new SkillTargetLocal { type = ETargetType.Player, id = 9 };
        turret.Aim(factory, core, builders, EAggressiveLevel.Normal);
        TestAssert.AreEqual(7, turret.target.id); TestAssert.IsTrue(turret.targetInShootRange);
        TestAssert.AreEqual(990, builders[0].energy); TestAssert.AreEqual(30, turret.fire);
        TestAssert.AreEqual(999f, skills.playerSkillTargetL.x); TestAssert.AreEqual(999f, skills.playerVelocityL.x);
        if (type == EDFTurretType.Laser)
        {
            TestAssert.AreEqual(1, skills.dfgTowerLasers.count);
            var shot = skills.dfgTowerLasers.buffer[1]; TestAssert.AreEqual(7, shot.target.id); TestAssert.AreEqual(60f, shot.endPos.x);
        }
        else
        {
            TestAssert.AreEqual(1, skills.dfgTowerPlasmas.count);
            var shot = skills.dfgTowerPlasmas.buffer[1]; TestAssert.AreEqual(7, shot.target.id);
            var expected = (new Vector3(60, 0, 0) + session.Combat.Players[1].velocityL * (60f / 2.5f / 60f)).normalized;
            TestAssert.AreEqual(expected.x, shot.dir.x, 1e-6f); TestAssert.AreEqual(expected.z, shot.dir.z, 1e-6f);
        }
    }

    [TestMethod]
    public void CooldownHeatEnergyAndOcclusionStillPreventEmission()
    {
        var turret = Turret(); turret.SearchTarget(factory, core, EAggressiveLevel.Normal);
        turret.fire = 5; turret.Aim(factory, core, builders, EAggressiveLevel.Normal);
        turret.fire = 0; turret.heat = -1; turret.Aim(factory, core, builders, EAggressiveLevel.Normal);
        turret.heat = 0; builders[0].energy = 10; turret.Aim(factory, core, builders, EAggressiveLevel.Normal);
        builders[0].energy = 1000; session.Combat.Players[1].skillTargetL = new Vector3(0, -210, 0);
        turret.Aim(factory, core, builders, EAggressiveLevel.Normal);
        TestAssert.AreEqual(0, skills.dfgTowerPlasmas.count); TestAssert.AreEqual(1000, builders[0].energy);
    }

    [TestMethod]
    public void DegenerateDirectionsAndInvalidVelocityCannotEmitNonfiniteShots()
    {
        var turret = Turret(); turret.localDir = new Vector3(float.NaN, 0, 0);
        session.Combat.Players[1].skillTargetL = turret.muzzleWPos;
        turret.Aim(factory, core, builders, EAggressiveLevel.Normal);
        TestAssert.IsTrue(GroundTurretCombat.Finite(turret.localDir)); TestAssert.AreEqual(0, skills.dfgTowerPlasmas.count);
        session.Combat.Players[1].velocityL = new Vector3(float.PositiveInfinity, 0, 0);
        TestAssert.AreEqual(0, GroundTurretCombat.RetaliationPlayer(factory, core));
    }

    [TestMethod]
    public void ZeroOrOverflowingPlasmaInterceptCannotSpendEnergyOrEmit()
    {
        session.Combat.Players = [Pilot(7, 60)];
        session.Combat.Players[0].velocityL = new Vector3(-150, 0, 0);
        var turret = Turret(); turret.Aim(factory, core, builders, EAggressiveLevel.Normal);
        TestAssert.IsFalse(turret.targetInShootRange); TestAssert.AreEqual(0, skills.dfgTowerPlasmas.count);
        session.Combat.Players[0].skillTargetL = new Vector3(1e9f, 210, 0);
        session.Combat.Players[0].velocityL = new Vector3(1e18f, 0, 0);
        turret.Aim(factory, core, builders, EAggressiveLevel.Normal);
        TestAssert.IsFalse(turret.targetInShootRange); TestAssert.AreEqual(0, skills.dfgTowerPlasmas.count);
        TestAssert.AreEqual(1000, builders[0].energy);
    }

    private GroundEnemyState State(DFGTurretComponent turret, bool counter) => new(GroundEnemyKind.GroundTurret, false, false,
        8106, 0, 0, 0, 0, 101, 101, 1, 0, 0, 0, 210, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
        builderState: 4, energy: 1000, nativeTurret: NativeDarkFogTurrets.Export(turret), turretCounterAttack: counter);

    [TestMethod]
    public void SnapshotAuthorizationIsBoundToItsPlayerAndExpiresWithoutClientHatred()
    {
        session.Client = Uninitialized<global::NebulaeNetwork.Client>();
        var turret = Turret(); turret.target = new SkillTargetLocal { type = ETargetType.Player, id = 7 }; turret.state = EDFTurretState.Aiming;
        var state = State(turret, true);
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(state, out var bytes));
        TestAssert.IsTrue(GroundEnemyStateCodec.TryDecode(bytes, 0, bytes.Length, out var decoded, out _));
        TestAssert.IsTrue(decoded.TurretCounterAttack);
        session.DarkFogTurrets.Install(factory, 2, decoded);
        TestAssert.IsTrue(session.DarkFogTurrets.CounterAttackFor(factory, 7, 2));
        TestAssert.IsFalse(session.DarkFogTurrets.CounterAttackFor(factory, 9, 2));
        NativeDarkFogTurrets.Active = true;
        turret.Aim(factory, core, builders, EAggressiveLevel.Passive);
        TestAssert.AreEqual(7, skills.dfgTowerPlasmas.buffer[1].target.id);
        session.DarkFogTurrets.Install(factory, 2, State(turret, false));
        TestAssert.IsFalse(session.DarkFogTurrets.CounterAttackFor(factory, 7, 2));
        turret.fire = 0; turret.Aim(factory, core, builders, EAggressiveLevel.Passive);
        TestAssert.IsFalse(turret.targetInShootRange); TestAssert.AreEqual(1, skills.dfgTowerPlasmas.count);
        session.DarkFogTurrets.Install(factory, 2, state);
        var entries = (System.Collections.IDictionary)AccessTools.Field(typeof(NativeDarkFogTurrets), "entries").GetValue(session.DarkFogTurrets);
        SetField(entries.Values.Cast<object>().Single(), "Received", -121L);
        TestAssert.IsFalse(session.DarkFogTurrets.CounterAttackFor(factory, 7, 2));
    }

    [TestMethod]
    public void TargetChangeDisconnectAndEnemyRemovalRevokeThePreviousAuthorization()
    {
        session.Client = Uninitialized<global::NebulaeNetwork.Client>();
        var turret = Turret(); turret.state = EDFTurretState.Aiming;
        turret.target = new SkillTargetLocal { type = ETargetType.Player, id = 7 };
        session.DarkFogTurrets.Install(factory, 2, State(turret, true));
        turret.target.id = 9;
        session.DarkFogTurrets.Install(factory, 2, State(turret, true));
        TestAssert.IsFalse(session.DarkFogTurrets.CounterAttackFor(factory, 7, 2));
        TestAssert.IsTrue(session.DarkFogTurrets.CounterAttackFor(factory, 9, 2));
        session.Combat.Players = [Pilot(7, 60)];
        TestAssert.IsFalse(session.DarkFogTurrets.CounterAttackFor(factory, 9, 2));
        session.Combat.Players = [Pilot(9, 5)]; factory.enemyPool[2].id = 0;
        TestAssert.IsFalse(session.DarkFogTurrets.CounterAttackFor(factory, 9, 2));
    }

    [TestMethod]
    public void SinglePlayerSearchAndRetaliationStillUseNativeRules()
    {
        Multiplayer.Session = null;
        var turret = Turret();
        TestAssert.IsTrue(turret.SearchTarget(factory, core, EAggressiveLevel.Normal));
        TestAssert.AreEqual(1, turret.target.id);
        TestAssert.IsTrue(turret.CounterAttackPlayer(factory, core));
        factory.enemySystem = Uninitialized<EnemyDFGroundSystem>(); factory.enemySystem.local_player_exist_alive = true;
        TestAssert.IsTrue(turret.TargetIsInRange(factory, turret.target, 1, 1, out _, core, EAggressiveLevel.Normal));
    }

    [TestMethod]
    public void InFlightGroundShotsExpireWhenTheirPlayerTargetLeavesOrDies()
    {
        session.Client = Uninitialized<global::NebulaeNetwork.Client>();
        session.Combat.IndexByPlayerId[7] = 1; session.Combat.Players[1].mecha = new Mecha();
        skills.astroFactories = new PlanetFactory[102]; skills.astroFactories[101] = factory;
        var caster = new SkillTargetLocal { type = ETargetType.Enemy, id = 2 };
        var target = new SkillTargetLocal { type = ETargetType.Player, id = 7 };
        TestAssert.IsTrue(LocalSkillLifetime.IsValid(skills, 101, caster, target));
        session.Combat.Players[1].planetId = 202;
        TestAssert.IsFalse(LocalSkillLifetime.IsValid(skills, 101, caster, target));
        session.Combat.Players[1].planetId = 101; session.Combat.Players[1].isAlive = false;
        TestAssert.IsFalse(LocalSkillLifetime.IsValid(skills, 101, caster, target));
    }

    [TestMethod]
    [DataRow(EDFTurretType.Laser)]
    [DataRow(EDFTurretType.Plasma)]
    public void OnlyAnActualMatchingShotCanSuppressTheHostVisual(EDFTurretType type)
    {
        var kind = type == EDFTurretType.Laser ? BattleEffectKind.GroundTowerLaser : BattleEffectKind.GroundTowerPlasma;
        var target = new SkillTargetLocal { type = ETargetType.Player, id = 7 };
        TestAssert.IsFalse(NativeDarkFogTurrets.HasGroundShot(skills, kind, 101, 2, target));
        var turret = Turret(type); turret.Aim(factory, core, builders, EAggressiveLevel.Normal);
        TestAssert.IsTrue(NativeDarkFogTurrets.HasGroundShot(skills, kind, 101, 2, target));
        TestAssert.IsFalse(NativeDarkFogTurrets.HasGroundShot(skills, kind, 102, 2, target));
        TestAssert.IsFalse(NativeDarkFogTurrets.HasGroundShot(skills, kind, 101, 3, target));
        target.id = 9; TestAssert.IsFalse(NativeDarkFogTurrets.HasGroundShot(skills, kind, 101, 2, target));
        if (type == EDFTurretType.Laser) skills.dfgTowerLasers.buffer[1].life = 0;
        else skills.dfgTowerPlasmas.buffer[1].life = 0;
        target.id = 7; TestAssert.IsFalse(NativeDarkFogTurrets.HasGroundShot(skills, kind, 101, 2, target));
    }

    [TestMethod]
    public void MovementVelocityTravelsWithItsPoseAndRejectsNonfiniteValues()
    {
        var movement = new PlayerMovement { PlayerId = 7, LocalPlanetId = 101, VelocityL = new Float3(3, 4, 5), LocalPlanetPosition = new Float3(1, 210, 0) };
        var display = RoundTrip(new PlayerMovementDisplay(movement), processor =>
        {
            processor.RegisterNestedType<Float3>();
            processor.RegisterNestedType<Double3>();
        });
        TestAssert.AreEqual(5f, display.VelocityL.z);
        var view = new CombatManager.PlayerPosition { planetId = 101, starId = 1 };
        session.Combat.RecordLocalMovement(movement, 1); session.Combat.ApplyLocalMovement(ref view, null);
        TestAssert.AreEqual(new Vector3(3, 4, 5), view.velocityL); TestAssert.AreEqual(1f, view.position.x);
        display.VelocityL = new Float3(float.NaN, 0, 0); TestAssert.IsFalse(display.HasFinitePose());
    }

    [TestMethod]
    public void ParallelTurretsKeepIndependentTargetsAndVelocityInputs()
    {
        System.Threading.Tasks.Parallel.For(0, 16, index =>
        {
            var id = index % 2 == 0 ? 7 : 9;
            var localCore = new DFGBaseComponent { id = 1, enemyId = 1 };
            localCore.hatred.HateTarget(ETargetType.Player, id, 1000, EHatredOperation.Set);
            var turret = Turret();
            turret.Aim(factory, localCore, [new EnemyBuilderComponent { state = 4, energy = 1000 }], EAggressiveLevel.Normal);
            TestAssert.AreEqual(id, turret.target.id);
        });
        TestAssert.AreEqual(16, skills.dfgTowerPlasmas.count);
        TestAssert.AreEqual(8, skills.dfgTowerPlasmas.buffer.Count(x => x.id > 0 && x.target.id == 7));
        TestAssert.AreEqual(8, skills.dfgTowerPlasmas.buffer.Count(x => x.id > 0 && x.target.id == 9));
        TestAssert.AreEqual(999f, skills.playerSkillTargetL.x); TestAssert.AreEqual(999f, skills.playerVelocityL.x);
    }

    [TestMethod]
    public void AuthorizationSurvivesAnAbsoluteFieldDeltaAndCannotAuthorizeACraft()
    {
        var turret = Turret(); turret.target = new SkillTargetLocal { type = ETargetType.Player, id = 7 }; turret.state = EDFTurretState.Aiming;
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(State(turret, false), out var before));
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(State(turret, true), out var after));
        var fields = global::NebulaeModel.Authority.AuthorityStateFields.Describe(global::NebulaeModel.Authority.PoolKind.GroundEnemy, before);
        TestAssert.IsTrue(global::NebulaeModel.Networking.FieldDeltaCodec.TryEncode(before, fields, after, fields, out var patch));
        var applied = global::NebulaeModel.Networking.FieldDeltaCodec.Apply(before, fields, patch);
        CollectionAssert.AreEqual(after, applied);
        turret.target.type = ETargetType.Craft;
        TestAssert.IsFalse(GroundEnemyStateCodec.TryEncode(State(turret, true), out _));
    }

    private static bool Buckets(HashSystem __instance)
    { __instance.activeBucketsCount = 1; return false; }
    private static bool ParallelBuckets(HashSystem __instance, ref int[] __result, ref int _activeBucketsCount)
    { __result = __instance.activeBuckets; _activeBucketsCount = 1; return false; }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HashSearchPreservesPlayerPriorityAndChoosesValidBuildingsAndCraft(bool parallel)
    {
        harmony.Patch(AccessTools.Method(typeof(HashSystem), nameof(HashSystem.GetBucketIdxesInArea), [typeof(Vector3), typeof(float)]),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(GroundTurretCombatTest), nameof(Buckets))));
        harmony.Patch(AccessTools.Method(typeof(HashSystem), nameof(HashSystem.GetBucketIdxesInAreaParallel), [typeof(Vector3), typeof(float), typeof(int), typeof(int).MakeByRefType()]),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(GroundTurretCombatTest), nameof(ParallelBuckets))));
        factory.hashSystemStatic = new HashSystem(true) { activeBuckets = [0], bucketOffsets = [0], bucketCursors = [1], hashPool = [1] };
        factory.hashSystemDynamic = new HashSystem(true) { activeBuckets = [0], bucketOffsets = [0], bucketCursors = [1], hashPool = [0x60000001] };
        factory.enemyPool[1].counterAttack = false;
        var turret = Turret(); var thread = parallel ? 1 : -1;
        TestAssert.IsTrue(turret.SearchTarget(factory, core, EAggressiveLevel.Normal, thread));
        TestAssert.AreEqual(ETargetType.Player, turret.target.type); TestAssert.AreEqual(9, turret.target.id);
        session.Combat.Players[0].skillTargetL = new Vector3(15, 210, 0);
        TestAssert.IsTrue(turret.SearchTarget(factory, core, EAggressiveLevel.Normal, thread));
        TestAssert.AreEqual(ETargetType.Craft, turret.target.type);
        factory.craftPool[1].isInvincible = true;
        TestAssert.IsTrue(turret.SearchTarget(factory, core, EAggressiveLevel.Normal, thread));
        TestAssert.AreEqual(ETargetType.None, turret.target.type); TestAssert.AreEqual(1, turret.target.id);
        factory.entityPool[1].id = 0; session.Combat.Players = [];
        TestAssert.IsFalse(turret.SearchTarget(factory, core, EAggressiveLevel.Normal, thread));
    }
}
