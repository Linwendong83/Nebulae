using System.Reflection;
using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Combat;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Authority.Adapters;
using NebulaeWorld.Combat;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class PlayerMissileLifetimeTest
{
    private GameData previousGame = null!;
    private MultiplayerSession previousSession = null!;
    private long previousTick;
    private MultiplayerSession session = null!;
    private SkillSystem skills = null!;
    private PlanetFactory factory = null!;
    private PlayerWeaponManager manager = null!;
    private NativePlayerWeaponPool pool = null!;
    private PlayerWeaponManager.Entry entry = null!;
    private Dictionary<(PoolKind, int, int), ObjectKey> targets = null!;
    private readonly AuthorityEpoch epoch = new(43, 57);
    private static readonly MethodInfo Sample = AccessTools.Method(typeof(PlayerWeaponManager), "Sample");

    [TestInitialize]
    public void Setup()
    {
        previousGame = GameMain.data; previousSession = Multiplayer.Session; previousTick = GameMain.gameTick;
        var game = Uninitialized<GameData>();
        var star = new StarData { id = 1 };
        factory = Uninitialized<PlanetFactory>();
        var planet = new PlanetData { id = 101, factory = factory, star = star };
        SetBackingField(factory, nameof(PlanetFactory.planet), planet);
        factory.enemyPool = new EnemyData[8]; factory.enemyCursor = 7;
        factory.enemyPool[5] = new EnemyData { id = 5, protoId = 8128, astroId = 101 };
        factory.enemyPool[6] = new EnemyData { id = 6, protoId = 8128, astroId = 101 };
        star.planets = [planet];
        game.galaxy = new GalaxyData { stars = [star] };
        SetBackingField(game, nameof(GameData.localStar), star);
        SetBackingField(game, nameof(GameData.localPlanet), planet);
        game.relativeRot = new Quaternion(0, 0, 0, 1);
        var sector = Uninitialized<SpaceSector>();
        sector.enemyPool = new EnemyData[8]; sector.enemyCursor = 7;
        sector.enemyPool[5] = new EnemyData { id = 5, protoId = 8128, astroId = 1000001 };
        sector.galaxyAstros = new AstroData[200];
        game.spaceSector = sector;
        GameMain.data = game; GameMain.gameTick = 100;
        skills = Uninitialized<SkillSystem>(); skills.sector = sector; skills.gameData = game;
        skills.astroFactories = new PlanetFactory[200]; skills.astroFactories[101] = factory;
        skills.mechaMissiles = new DataPool<GeneralMissile>(); skills.mechaMissiles.Reset();
        skills.mechaMissileTrails = new VFTrailRenderer { trailStride = 50 };
        skills.mechaMissiles.cursor = 2;
        sector.skillSystem = skills;
        session = Uninitialized<MultiplayerSession>();
        session.Client = Uninitialized<global::NebulaeNetwork.Client>();
        session.LocalPlayer = new LocalPlayer { Data = new PlayerData(2, 101) };
        session.Authority = new AuthoritySessionState(); session.Authority.BeginAuthorityWorld(epoch, false);
        session.AuthorityRuntime = new AuthoritySession(session.Authority);
        var replica = new ClientWorldReplica(epoch);
        SetBackingField(session.AuthorityRuntime, nameof(AuthoritySession.WorldReplica), replica);
        targets = (Dictionary<(PoolKind, int, int), ObjectKey>)AccessTools.Field(typeof(ClientWorldReplica), "currentSlots").GetValue(replica);
        foreach (var scope in new[] { new ScopeKey(PoolKind.GroundEnemy, 101), new ScopeKey(PoolKind.SpaceEnemy, 0) })
            replica.Versions.GetOrCreateScope(scope).SetPhase(SubscriptionPhase.Live);
        session.IsGameLoaded = true; session.Impacts = new BattleImpactCapture();
        Multiplayer.Session = session;
        manager = new PlayerWeaponManager(); session.PlayerWeapons = manager;
        var pools = NativePlayerWeaponPool.Create(skills);
        pool = pools.Single(p => p.Kind == BattleEffectKind.MechaMissile);
        SetField(manager, "skills", skills); SetField(manager, "pools", pools); SetField(manager, "epoch", epoch);
        entry = new PlayerWeaponManager.Entry
        {
            Shot = new PlayerShotId(3, 0, 1),
            Id = 1,
            Star = 1,
            Pool = pool,
            Target = Key(false),
            Launched = true,
            LaunchAudio = true,
            SampleTick = GameMain.gameTick - 1
        };
        AccessTools.Method(typeof(PlayerWeaponManager), "Bind").Invoke(manager, [entry]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        GameMain.data = previousGame; Multiplayer.Session = previousSession; GameMain.gameTick = previousTick;
    }

    private ObjectKey Key(bool space, long generation = 1, int id = 5) =>
        ObjectKey.Create(epoch, space ? PoolKind.SpaceEnemy : PoolKind.GroundEnemy, space ? 0 : 101, id, generation);

    private static GeneralMissile Missile(bool space, int life = 10) => new()
    {
        id = 91,
        life = life,
        modelIndex = 431,
        damage = 1200,
        damageIncoming = 1200,
        mask = ETargetTypeMask.Enemy,
        targetCombatStatId = 12,
        target = new SkillTarget { type = ETargetType.Enemy, id = 5, astroId = space ? 0 : 101 },
        caster = new SkillTarget { type = ETargetType.Player, id = 3 },
        uPos = new VectorLF3(400, 500, 600),
        uVel = new Vector3(120, 0, 0),
        uRot = new Quaternion(0, 0, 0, 1)
    };

    private void Receive(PlayerWeaponEvent kind, GeneralMissile missile = default)
    {
        var sample = new PlayerWeaponSample
        {
            Shot = entry.Shot,
            Kind = pool.Kind,
            Event = kind,
            Star = 1,
            Tick = GameMain.gameTick,
            Target = entry.Target,
            Payload = kind == PlayerWeaponEvent.State ? NativeDarkFogTurrets.Export(missile) : []
        };
        manager.Receive(new PlayerWeaponPacket { EpochHigh = epoch.High, EpochLow = epoch.Low, Data = PlayerWeaponSample.Encode([sample]) });
        manager.TickClient();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void DeadOrRecycledTargetCannotBlockCorrectionsOrAuthoritativeEnd(bool space, bool recycled)
    {
        entry.Target = Key(space);
        skills.mechaMissiles.buffer[1] = Missile(space); skills.mechaMissiles.buffer[1].id = 1;
        if (recycled) targets[(entry.Target.Kind, entry.Target.Scope, 5)] = Key(space, 2);
        Receive(PlayerWeaponEvent.State, Missile(space, 20));
        TestAssert.IsNull(entry.Pending, "A dead launch target must not gate a host correction.");
        var restored = (GeneralMissile)pool.Read(entry.Id);
        TestAssert.AreEqual(20, restored.life); TestAssert.AreEqual(400d, restored.uPos.x);
        TestAssert.AreEqual(0, restored.target.id, "Never follow an absent enemy or a reused slot from a stale sample.");
        TestAssert.AreEqual(0, restored.targetCombatStatId); TestAssert.AreEqual(1200, restored.damage);
        TestAssert.AreEqual(ETargetTypeMask.Enemy, restored.mask);
        using var scope = manager.BeforeSkill(restored, skills, out var run);
        TestAssert.IsTrue(run, "Lost targets must not stop native flight, smoke or lifetime updates.");
        Receive(PlayerWeaponEvent.End);
        TestAssert.IsFalse(pool.Exists(1)); TestAssert.AreEqual(0, manager.ActiveCount);
    }

    [TestMethod]
    public void LostTargetStillAllowsNativeTailMotionAndExpiry()
    {
        var missile = Missile(false, -3);
        entry.Id = pool.Install(NativeDarkFogTurrets.Export(missile), 1, entry.Target);
        for (var i = 0; i < 3; i++)
        {
            using var scope = manager.BeforeSkill(pool.Read(entry.Id), skills, out var run);
            TestAssert.IsTrue(run);
            pool.Advance(entry.Id, skills, GameMain.gameTick, 1);
            if (i == 0)
            {
                var moved = (GeneralMissile)pool.Read(entry.Id);
                TestAssert.AreEqual(-2, moved.life); TestAssert.AreEqual(402d, moved.uPos.x);
            }
        }
        TestAssert.IsFalse(pool.Exists(entry.Id), "The native lifetime must remove the model instead of leaving it frozen.");
    }

    [TestMethod]
    public void HostRetargetCorrectionReplacesThePreviousGenerationGuard()
    {
        targets[(PoolKind.GroundEnemy, 101, 5)] = entry.Target;
        var initial = Missile(false); initial.id = 1;
        skills.mechaMissiles.buffer[1] = initial;
        TestAssert.IsTrue(EnemyTargetIdentity.ValidateMissile(skills, ref initial));
        entry.Target = Key(false, 2);
        targets[(PoolKind.GroundEnemy, 101, 5)] = entry.Target;
        Receive(PlayerWeaponEvent.State, Missile(false, 20));
        var corrected = (GeneralMissile)pool.Read(entry.Id);
        TestAssert.AreEqual(5, corrected.target.id);
        TestAssert.IsTrue(EnemyTargetIdentity.ValidateMissile(skills, ref corrected), "A host-confirmed new target must replace the old generation guard.");
    }

    [TestMethod]
    public void HostSamplesPublishTheCurrentTargetAfterNativeReacquisition()
    {
        session.Client = null!; session.Server = Uninitialized<global::NebulaeNetwork.Server>();
        session.AuthorityRuntime.GroundEnemyHost = new GroundEnemySnapshotAdapter(epoch);
        var missile = Missile(false); missile.id = 1; missile.target.id = 6;
        skills.mechaMissiles.buffer[1] = missile;
        var sample = (PlayerWeaponSample)Sample.Invoke(manager, [entry, PlayerWeaponEvent.State])!;
        TestAssert.IsTrue(session.AuthorityRuntime.GroundEnemyHost.TryGetEnemyKey(101, 6, out var current));
        TestAssert.AreEqual(current, sample.Target, "The envelope must describe the payload's current target, not the launch target.");
        missile.target.id = 0; skills.mechaMissiles.buffer[1] = missile;
        sample = (PlayerWeaponSample)Sample.Invoke(manager, [entry, PlayerWeaponEvent.State])!;
        TestAssert.IsFalse(sample.Target.IsValid);
    }
}
