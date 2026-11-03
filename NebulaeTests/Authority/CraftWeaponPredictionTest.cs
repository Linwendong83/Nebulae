using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Combat;
using NebulaeModel.Utils;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;
using NebulaePatcher.Patches.Authority;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
internal class CraftWeaponPredictionTest
{
    private readonly AuthorityEpoch epoch = new(7, 11);
    private MultiplayerSession oldSession = null!;
    private GameData oldData = null!;
    private long oldTick;
    private AuthoritySession runtime = null!;
    private SpaceSector sector = null!;
    private SkillSystem skills = null!;
    private CraftWeaponPrediction prediction = null!;
    private ObjectKey source, target;

    private static DataPoolRenderer<T> Pool<T>() where T : struct, IPoolElement => new()
    { buffer = new T[32], capacity = 32, recycleIds = new int[32], cursor = 1 };

    private void Baseline(ObjectKey key, long subscription = 1)
    {
        var scope = new ScopeKey(key.Kind, key.Scope); var replica = runtime.WorldReplica;
        replica.Subscribe(scope);
        var frozen = new FrozenScopeSnapshot(scope, subscription, subscription, 0, 100, [new SnapshotMemberRecord(key, 1, [1])]);
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(frozen, out var image)); var hash = AuthoritySnapshotCodec.Hash(image);
        TestAssert.IsTrue(replica.BeginSnapshot(scope, subscription, subscription, 1, image.Length, hash));
        TestAssert.IsTrue(replica.ReceiveSnapshotChunk(scope, subscription, subscription, 0, image));
        TestAssert.IsTrue(replica.CommitSnapshot(scope, subscription, subscription, 1, hash));
        replica.Versions.TryGetScope(scope, out var version); version.SetPhase(SubscriptionPhase.Live);
        replica.SetLocalComponentBinding(key, key.NativeId);
    }

    [TestInitialize]
    public void Setup()
    {
        oldSession = Multiplayer.Session; oldData = GameMain.data; oldTick = GameMain.gameTick;
        runtime = new AuthoritySession(new AuthoritySessionState()); runtime.BeginAuthorityWorld(epoch, false);
        var session = Uninitialized<MultiplayerSession>(); session.AuthorityRuntime = runtime; session.Authority = runtime.Identity;
        session.LocalPlayer = new LocalPlayer { Data = new PlayerData(7, 0) };
        session.Client = Uninitialized<global::NebulaeNetwork.Client>();
        session.BattleVisuals = new BattleVisualManager(); prediction = session.BattleVisuals.CraftWeapons;
        Multiplayer.Session = session;
        var game = Uninitialized<GameData>(); GameMain.data = game; GameMain.gameTick = 100;
        game.SetHiddenProperty(nameof(GameData.localStar), new StarData { id = 1 });
        sector = Uninitialized<SpaceSector>(); game.spaceSector = sector;
        skills = sector.skillSystem = Uninitialized<SkillSystem>();
        skills.fighterLasers = Pool<LocalLaserOneShot>(); skills.fighterPlasmas = Pool<LocalGeneralProjectile>();
        skills.fighterShieldPlasmas = Pool<LocalGeneralProjectile>();
        skills.warshipTypeFLasers = new DataPool<SpaceLaserOneShot>(); skills.warshipTypeFLasers.Reset();
        skills.warshipTypeFPlasmas = Pool<GeneralProjectile>(); skills.warshipTypeAPlasmas = Pool<GeneralProjectile>();
        sector.craftPool = new CraftData[4]; sector.craftPool[1] = new CraftData { id = 1, astroId = 100, port = 2 };
        source = ObjectKey.Create(epoch, PoolKind.SpaceCraft, 0, 1, 1);
        target = ObjectKey.Create(epoch, PoolKind.SpaceEnemy, 0, 3, 1);
        Baseline(source); Baseline(target); InstallOwner(7);
    }
    private void InstallOwner(ushort owner)
    {
        var state = new CraftState(false, true, true, 5111, 60, 2, (byte)ECraftProto.Item, 0,
            100, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 99, owner);
        NativeCraftSimulation.Install(sector, sector.craftPool, 1, 1, state);
    }
    [TestMethod]
    public void RemoteNativeWeaponSuppressionRestoresCooldownsOnFailureWithoutTouchingMotion()
    {
        var previous = AuthorityRuleGuard.Probe; var planet = NativeCraftSimulation.FleetPlanet;
        try
        {
            AuthorityRuleGuard.Probe = () => new AuthorityGuardSession(true, false, false);
            NativeCraftSimulation.FleetPlanet = 0;
            Multiplayer.Session.Authority.SetConnection(new ConnectionEpoch(3), 7);
            InstallOwner(8);
            var unit = new UnitComponent { craftId = 1, fire0 = 15, fire1 = -10, speed = 123 };
            var args = new object[] { unit, sector, new PrefabDesc(), sector.craftPool[1], default(CraftWeaponPrediction_Patch.State) };
            AccessTools.Method(typeof(CraftWeaponPrediction_Patch), "Prefix").Invoke(null, args);
            unit = (UnitComponent)args[0];
            TestAssert.IsGreaterThan(0, unit.fire0); TestAssert.IsGreaterThan(0, unit.fire1);
            unit.isShooting0 = unit.isShooting1 = true;
            var error = new InvalidOperationException("native failure");
            var final = new object[] { unit, args[4], error };
            TestAssert.AreSame(error, AccessTools.Method(typeof(CraftWeaponPrediction_Patch), "Finalizer").Invoke(null, final));
            unit = (UnitComponent)final[0];
            TestAssert.AreEqual(15, unit.fire0); TestAssert.AreEqual(-10, unit.fire1);
            TestAssert.IsFalse(unit.isShooting0); TestAssert.IsFalse(unit.isShooting1);
            TestAssert.AreEqual(123f, unit.speed);
            TestAssert.AreEqual(0, skills.warshipTypeAPlasmas.count);
        }
        finally { AuthorityRuleGuard.Probe = previous; NativeCraftSimulation.FleetPlanet = planet; }
    }

    [TestMethod]
    public void LocalNativeWeaponClockIsNotRewrittenByTheOwnershipGuard()
    {
        var previous = AuthorityRuleGuard.Probe; var planet = NativeCraftSimulation.FleetPlanet;
        try
        {
            AuthorityRuleGuard.Probe = () => new AuthorityGuardSession(true, false, false);
            NativeCraftSimulation.FleetPlanet = 0;
            Multiplayer.Session.Authority.SetConnection(new ConnectionEpoch(3), 7);
            var unit = new UnitComponent { craftId = 1, fire0 = 15, fire1 = 4, speed = 123 };
            var args = new object[] { unit, sector, new PrefabDesc(), sector.craftPool[1], default(CraftWeaponPrediction_Patch.State) };
            AccessTools.Method(typeof(CraftWeaponPrediction_Patch), "Prefix").Invoke(null, args);
            unit = (UnitComponent)args[0];
            TestAssert.AreEqual(15, unit.fire0); TestAssert.AreEqual(4, unit.fire1);
            var final = new object[] { unit, args[4], null! };
            AccessTools.Method(typeof(CraftWeaponPrediction_Patch), "Finalizer").Invoke(null, final);
            unit = (UnitComponent)final[0];
            TestAssert.AreEqual(15, unit.fire0); TestAssert.AreEqual(4, unit.fire1);
            TestAssert.AreEqual(123f, unit.speed);
        }
        finally { AuthorityRuleGuard.Probe = previous; NativeCraftSimulation.FleetPlanet = planet; }
    }
    [TestCleanup]
    public void Cleanup()
    {
        prediction.Clear(); runtime.Dispose(); Multiplayer.Session = oldSession; GameMain.data = oldData; GameMain.gameTick = oldTick;
    }
    private int Fire(BattleEffectKind kind)
    {
        var unit = new UnitComponent { craftId = 1, muzzleIndex0 = 1, muzzleIndex1 = 1 };
        var capture = prediction.Begin(skills, unit, sector.craftPool[1], new PrefabDesc { craftUnitMuzzleCount0 = 2, craftUnitMuzzleCount1 = 2 }, false);
        var caster = new SkillTarget { id = 1, type = ETargetType.Craft, astroId = 100 };
        var enemy = new SkillTarget { id = 3, type = ETargetType.Enemy, astroId = 1000001 };
        int id;
        if (kind == BattleEffectKind.SpaceLaser)
        {
            ref var shot = ref skills.warshipTypeFLasers.Add(); id = shot.id;
            shot.caster = caster; shot.target = enemy; shot.life = 15; shot.endPosU = new VectorLF3(0, 0, 100);
        }
        else
        {
            ref var shot = ref skills.warshipTypeFPlasmas.Add(); id = shot.id;
            shot.caster = caster; shot.target = enemy; shot.life = shot.lifemax = 120; shot.uVel = Vector3.forward;
        }
        prediction.End(capture); return id;
    }
    private BattleEffectData Effect(BattleEffectKind kind, int id)
    {
        var effect = prediction.Launch(kind, id)!;
        effect.Kind = kind; effect.Id = 50; effect.Generation = 8; effect.Tick = GameMain.gameTick;
        effect.SourceAstroId = 100; effect.SourceObjectId = 1; effect.Life = 15;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        if (kind == BattleEffectKind.SpaceLaser) skills.warshipTypeFLasers.buffer[id].Export(writer);
        else skills.warshipTypeFPlasmas.buffer[id].Export(writer);
        writer.Flush(); effect.Payload = stream.ToArray(); return effect;
    }
    [TestMethod]
    [DataRow(BattleEffectKind.SpaceLaser)]
    [DataRow(BattleEffectKind.SpacePlasmaF)]
    public void EachNativeShotCanReplaceOnlyOneMatchingHostShot(BattleEffectKind kind)
    {
        var id = Fire(kind); var effect = Effect(kind, id);
        var frame = new BattleVisualFrame(); frame.Effects.Add(effect);
        effect = BattleVisualFrame.Import(frame.Export()).Effects.Single();
        TestAssert.IsTrue(prediction.Reconcile(effect));
        TestAssert.IsFalse(prediction.Reconcile(effect), "A second host launch cannot reuse an already paired prediction.");
    }
    [TestMethod]
    [DataRow("target")]
    [DataRow("source")]
    [DataRow("port")]
    [DataRow("muzzle")]
    [DataRow("direction")]
    public void ConflictingPredictionIsRemovedBeforeHostFallback(string changed)
    {
        var id = Fire(BattleEffectKind.SpacePlasmaF); var effect = Effect(BattleEffectKind.SpacePlasmaF, id);
        if (changed == "target") effect.TargetGeneration++;
        if (changed == "source") effect.SourceGeneration++;
        if (changed == "port") effect.SourcePort++;
        if (changed == "muzzle") effect.MuzzleIndex++;
        if (changed == "direction")
        {
            var value = skills.warshipTypeFPlasmas.buffer[id]; value.uVel = Vector3.right;
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            value.Export(writer); writer.Flush(); effect.Payload = stream.ToArray();
        }
        TestAssert.IsFalse(prediction.Reconcile(effect));
        if (changed != "source") TestAssert.AreEqual(0, skills.warshipTypeFPlasmas.buffer[id].id);
        else TestAssert.AreEqual(id, skills.warshipTypeFPlasmas.buffer[id].id, "A stale source must not erase a newer source's prediction.");
    }
    [TestMethod]
    public void HostFirstFallbackSuppressesTheLaterCorrespondingPrediction()
    {
        var id = Fire(BattleEffectKind.SpacePlasmaF); var effect = Effect(BattleEffectKind.SpacePlasmaF, id);
        prediction.Clear(); skills.warshipTypeFPlasmas.Remove(id);
        TestAssert.IsFalse(prediction.Reconcile(effect));
        id = Fire(BattleEffectKind.SpacePlasmaF);
        TestAssert.AreEqual(0, skills.warshipTypeFPlasmas.buffer[id].id);
    }
    [TestMethod]
    public void RemoteOwnershipDisablesWeaponsWithoutRemovingTheMotionEntry()
    {
        InstallOwner(8);
        TestAssert.IsFalse(NativeCraftSimulation.PredictsWeapons(0, 1));
        var capture = prediction.Begin(skills, new UnitComponent { craftId = 1 }, sector.craftPool[1], new PrefabDesc(), false);
        TestAssert.IsNull(capture);
        TestAssert.AreEqual(1, sector.craftPool[1].id);
        InstallOwner(7); TestAssert.IsTrue(NativeCraftSimulation.PredictsWeapons(0, 1));
    }
    [TestMethod]
    public void RecycledTargetSlotsCannotMatchAnOldLaunch()
    {
        var id = Fire(BattleEffectKind.SpacePlasmaF); var effect = Effect(BattleEffectKind.SpacePlasmaF, id);
        target = ObjectKey.Create(epoch, PoolKind.SpaceEnemy, 0, 3, 2); Baseline(target, 2);
        TestAssert.IsFalse(prediction.Reconcile(effect));
        TestAssert.AreEqual(0, skills.warshipTypeFPlasmas.buffer[id].id);
    }
    [TestMethod]
    public void HostEndStopsOnlyItsOwnMatchedPrediction()
    {
        var id = Fire(BattleEffectKind.SpacePlasmaF); var effect = Effect(BattleEffectKind.SpacePlasmaF, id);
        TestAssert.IsTrue(prediction.Reconcile(effect));
        effect.Generation++; prediction.EndEffect(effect);
        TestAssert.AreEqual(id, skills.warshipTypeFPlasmas.buffer[id].id);
        effect.Generation--; prediction.EndEffect(effect);
        TestAssert.AreEqual(0, skills.warshipTypeFPlasmas.buffer[id].id);
    }
    [TestMethod]
    public void ADelayedHostEndCannotRemoveAReusedNativeProjectileSlot()
    {
        var id = Fire(BattleEffectKind.SpacePlasmaF); var old = Effect(BattleEffectKind.SpacePlasmaF, id);
        TestAssert.IsTrue(prediction.Reconcile(old));
        skills.warshipTypeFPlasmas.Remove(id);
        TestAssert.AreEqual(id, Fire(BattleEffectKind.SpacePlasmaF));
        prediction.EndEffect(old);
        TestAssert.AreEqual(id, skills.warshipTypeFPlasmas.buffer[id].id);
        var next = Effect(BattleEffectKind.SpacePlasmaF, id); next.Generation++;
        TestAssert.IsTrue(prediction.Reconcile(next));
    }

    [TestMethod]
    public void ReceivingAnEndPacketCannotMutateTheRunningNativePool()
    {
        var id = Fire(BattleEffectKind.SpacePlasmaF); var end = Effect(BattleEffectKind.SpacePlasmaF, id);
        TestAssert.IsTrue(prediction.Reconcile(end));
        end.End = true; end.Life = 0; end.Payload = [];
        var frame = new BattleVisualFrame(); frame.Effects.Add(end);
        Multiplayer.Session.BattleVisuals.Receive(new BattleVisualPacket { StarId = 1, Tick = 100, Sequence = 1 }, frame);
        TestAssert.AreEqual(id, skills.warshipTypeFPlasmas.buffer[id].id, "Only the subsequent safe frame boundary may remove the projectile.");
    }
}
