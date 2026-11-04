using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;
using NebulaeWorld.Combat;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class NativeCraftSimulationTest
{
    [TestMethod]
    public void NativeHealthGuardTouchesActualHpWritesAndLeavesShipGeometryNative()
    {
        var targets = NativeUnitHealth_Patch.TargetMethods().ToArray();
        TestAssert.HasCount(typeof(UnitComponent).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Count(method => method.GetMethodBody() != null && PatchProcessor.GetOriginalInstructions(method).Any(code =>
                code.StoresField(AccessTools.Field(typeof(CombatStat), nameof(CombatStat.hp))))), targets);
        foreach (var method in targets)
            TestAssert.IsTrue(PatchProcessor.GetOriginalInstructions(method).Any(code =>
                code.StoresField(AccessTools.Field(typeof(CombatStat), nameof(CombatStat.hp)))));
        foreach (var method in new[] { nameof(UnitComponent.GetTargetPosition_Space),
                     nameof(UnitComponent.RunBehavior_Engage_SAttackPlasma_Small), nameof(UnitComponent.RunBehavior_Engage_SAttackLaser_Large) })
            TestAssert.IsFalse(targets.Contains(AccessTools.Method(typeof(UnitComponent), method)), method + " must not receive a no-op health detour.");
        TestAssert.IsFalse(CraftWeaponPrediction_Patch.TargetMethods().Any(method =>
            method.Name is nameof(UnitComponent.RunBehavior_Engage_SAttackPlasma_Small) or nameof(UnitComponent.RunBehavior_Engage_SAttackLaser_Large)),
            "Capture native weapons at the dispatcher boundary, outside the large movement methods.");
    }

    [TestMethod]
    public void AUnitWaitsForFormationResearchInsteadOfDividingByZero()
    {
        var oldSession = Multiplayer.Session; var oldData = GameMain.data; var oldTick = GameMain.gameTick;
        using var runtime = new AuthoritySession(new AuthoritySessionState());
        runtime.BeginAuthorityWorld(new AuthorityEpoch(7, 11), false);
        foreach (var kind in new[] { PoolKind.SpaceCraft, PoolKind.SpaceEnemy })
        {
            var scope = new ScopeKey(kind, 0); runtime.WorldReplica.Subscribe(scope);
            runtime.WorldReplica.Versions.TryGetScope(scope, out var version); version.SetPhase(SubscriptionPhase.Live);
        }
        try
        {
            var session = Uninitialized<MultiplayerSession>(); session.AuthorityRuntime = runtime; Multiplayer.Session = session;
            var game = Uninitialized<GameData>(); GameMain.data = game; GameMain.gameTick = 20;
            game.history = Uninitialized<GameHistoryData>();
            AccessTools.Field(typeof(GameData), "<localStar>k__BackingField").SetValue(game, new StarData { id = 1 });
            var sector = Uninitialized<SpaceSector>(); game.spaceSector = sector;
            sector.craftPool = new CraftData[2]; sector.craftPool[1] = new CraftData { id = 1, astroId = 100 };
            sector.combatSpaceSystem = Uninitialized<CombatSpaceSystem>();
            sector.combatSpaceSystem.fleets = new DataPool<FleetComponent>(); sector.combatSpaceSystem.fleets.Reset();
            sector.combatSpaceSystem.units = new DataPool<UnitComponent>(); sector.combatSpaceSystem.units.Reset();
            var unit = new UnitComponent { id = 1, craftId = 1, protoId = 5111, behavior = EUnitBehavior.SeekForm };
            var state = new CraftState(false, true, true, 5111, 60, 0, (byte)ECraftProto.Item, 0,
                100, 0, 0, 200, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, -1, nativeUnit: CraftNativeStateCodec.Encode(unit));
            NativeCraftSimulation.Install(sector, sector.craftPool, 1, 1, state);
            NativeCraftSimulation.Prepare(sector);
            TestAssert.IsFalse(NativeCraftSimulation.CanRun(sector, 1));
            game.history.spaceFleetPortCount = 9;
            TestAssert.IsTrue(NativeCraftSimulation.CanRun(sector, 1));
        }
        finally { Multiplayer.Session = oldSession; GameMain.data = oldData; GameMain.gameTick = oldTick; }
    }

    [TestMethod]
    public void RemoteSpaceFleetsCannotTickTargetsOmittedByTheStarSubscription()
    {
        var previousSession = Multiplayer.Session; var previousData = GameMain.data; var previousTick = GameMain.gameTick;
        using var runtime = new AuthoritySession(new AuthoritySessionState());
        runtime.BeginAuthorityWorld(new AuthorityEpoch(7, 11), false);
        foreach (var kind in new[] { PoolKind.SpaceCraft, PoolKind.SpaceEnemy })
        {
            var scope = new ScopeKey(kind, 0); runtime.WorldReplica.Subscribe(scope);
            runtime.WorldReplica.Versions.TryGetScope(scope, out var version); version.SetPhase(SubscriptionPhase.Live);
        }
        var session = Uninitialized<MultiplayerSession>();
        session.AuthorityRuntime = runtime;
        var data = Uninitialized<GameData>();
        AccessTools.Field(typeof(GameData), "<localStar>k__BackingField").SetValue(data, new StarData { id = 1 });
        var sector = new SpaceSector { craftPool = new CraftData[4] };
        sector.combatSpaceSystem = Uninitialized<CombatSpaceSystem>();
        sector.combatSpaceSystem.fleets = new DataPool<FleetComponent>(); sector.combatSpaceSystem.fleets.Reset();
        sector.combatSpaceSystem.units = new DataPool<UnitComponent>(); sector.combatSpaceSystem.units.Reset();
        data.spaceSector = sector;
        try
        {
            Multiplayer.Session = session; GameMain.data = data; GameMain.gameTick = 10;
            for (var id = 1; id <= 3; id++)
            {
                var astro = id == 3 ? 0 : id * 100;
                sector.craftPool[id] = new CraftData { id = id, astroId = astro };
                var fleet = new FleetComponent
                {
                    id = id,
                    craftId = id,
                    target = new SkillTarget { type = ETargetType.Enemy, id = 9999 }
                };
                var state = new CraftState(false, true, true, 5100, 60, 0, (byte)ECraftProto.Fleet, 0,
                    astro, 0, 0, 200, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
                    sampleTick: -1, nativeFleet: CraftNativeStateCodec.Encode(fleet));
                NativeCraftSimulation.Install(sector, sector.craftPool, id, id, state);
            }
            NativeCraftSimulation.Prepare(sector);
            TestAssert.IsTrue(NativeCraftSimulation.CanRun(sector, 1));
            TestAssert.IsFalse(NativeCraftSimulation.CanRun(sector, 2));
            TestAssert.IsFalse(NativeCraftSimulation.CanRun(sector, 3));
        }
        finally { Multiplayer.Session = previousSession; GameMain.data = previousData; GameMain.gameTick = previousTick; }
    }

    [TestMethod]
    public void ARecycledFleetSlotIsTakenFromItsDeadOwnerInsteadOfBlockingTheNewCraft()
    {
        var previousSession = Multiplayer.Session; var previousData = GameMain.data; var previousTick = GameMain.gameTick;
        using var runtime = new AuthoritySession(new AuthoritySessionState());
        runtime.BeginAuthorityWorld(new AuthorityEpoch(7, 11), false);
        foreach (var kind in new[] { PoolKind.SpaceCraft, PoolKind.SpaceEnemy })
        {
            var scope = new ScopeKey(kind, 0); runtime.WorldReplica.Subscribe(scope);
            runtime.WorldReplica.Versions.TryGetScope(scope, out var version); version.SetPhase(SubscriptionPhase.Live);
        }
        var session = Uninitialized<MultiplayerSession>();
        session.AuthorityRuntime = runtime;
        var data = Uninitialized<GameData>();
        AccessTools.Field(typeof(GameData), "<localStar>k__BackingField").SetValue(data, new StarData { id = 1 });
        var sector = new SpaceSector { craftPool = new CraftData[4] };
        sector.combatSpaceSystem = Uninitialized<CombatSpaceSystem>();
        sector.combatSpaceSystem.fleets = new DataPool<FleetComponent>(); sector.combatSpaceSystem.fleets.Reset();
        sector.combatSpaceSystem.units = new DataPool<UnitComponent>(); sector.combatSpaceSystem.units.Reset();
        data.spaceSector = sector;
        try
        {
            Multiplayer.Session = session; GameMain.data = data; GameMain.gameTick = 10;
            // Craft 2 is gone, but fleet slot 3 still names it: the recycled host slot must win.
            sector.craftPool[1] = new CraftData { id = 1 };
            sector.combatSpaceSystem.fleets.buffer[3] = new FleetComponent { id = 3, craftId = 2 };
            sector.combatSpaceSystem.fleets.cursor = 4;
            var fleet = new FleetComponent { id = 3, craftId = 1 };
            var state = new CraftState(false, true, true, 5100, 60, 0, (byte)ECraftProto.Fleet, 0,
                100, 0, 0, 200, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
                sampleTick: -1, nativeFleet: CraftNativeStateCodec.Encode(fleet));
            NativeCraftSimulation.Install(sector, sector.craftPool, 1, 1, state);
            NativeCraftSimulation.Prepare(sector);
            TestAssert.AreEqual(3, sector.craftPool[1].fleetId,
                "A dead owner's fleet slot must not keep the recycled craft unbound.");
            TestAssert.AreEqual(1, sector.combatSpaceSystem.fleets.buffer[3].craftId);
        }
        finally { Multiplayer.Session = previousSession; GameMain.data = previousData; GameMain.gameTick = previousTick; }
    }

    [TestMethod]
    public void NativeFleetIsBoundOnceAndCanResumeFromItsOriginalComponent()
    {
        var previousSession = Multiplayer.Session;
        var previousData = GameMain.data;
        var session = Uninitialized<MultiplayerSession>();
        var identity = new AuthoritySessionState();
        var runtime = new AuthoritySession(identity);
        runtime.BeginAuthorityWorld(new AuthorityEpoch(7, 11), false);
        foreach (var kind in new[] { PoolKind.GroundCraft, PoolKind.GroundEnemy, PoolKind.Entity })
        {
            var scope = new ScopeKey(kind, 101); runtime.WorldReplica.Subscribe(scope);
            runtime.WorldReplica.Versions.TryGetScope(scope, out var version); version.SetPhase(SubscriptionPhase.Live);
        }
        AccessTools.Field(typeof(MultiplayerSession), "<AuthorityRuntime>k__BackingField").SetValue(session, runtime);
        var factory = Uninitialized<PlanetFactory>();
        var planet = new PlanetData { id = 101, factory = factory, factoryLoaded = true };
        AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, planet);
        factory.craftPool = new CraftData[4]; factory.craftPool[3] = new CraftData { id = 3, owner = 1 };
        factory.combatGroundSystem = Uninitialized<CombatGroundSystem>();
        factory.combatGroundSystem.fleets = new DataPool<FleetComponent>(); factory.combatGroundSystem.fleets.Reset();
        factory.combatGroundSystem.units = new DataPool<UnitComponent>(); factory.combatGroundSystem.units.Reset();
        var data = Uninitialized<GameData>();
        data.galaxy = new GalaxyData { stars = new[] { new StarData { planets = new[] { planet } } } };
        var fleet = new FleetComponent { id = 9, craftId = 3, owner = 1, dispatch = true, targetPos = new VectorLF3(1, 2, 3) };
        var state = new CraftState(false, true, false, 5100, 60, 0, (byte)ECraftProto.Fleet, 0,
            101, 1, 0, 200, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, nativeFleet: CraftNativeStateCodec.Encode(fleet));
        try
        {
            Multiplayer.Session = session; GameMain.data = data;
            NativeCraftSimulation.Install(factory, factory.craftPool, 3, 3, state);
            NativeCraftSimulation.Prepare(factory);
            TestAssert.AreEqual(9, factory.craftPool[3].fleetId);
            TestAssert.AreEqual(3, factory.combatGroundSystem.fleets.buffer[9].craftId);
            TestAssert.AreEqual(new VectorLF3(1, 2, 3), factory.combatGroundSystem.fleets.buffer[9].targetPos);
            NativeCraftSimulation.Prepare(factory);
            TestAssert.AreEqual(1, factory.combatGroundSystem.fleets.count);
            NativeCraftSimulation.Remove(factory, 3);
        }
        finally { Multiplayer.Session = previousSession; GameMain.data = previousData; runtime.Dispose(); }
    }
    [TestMethod]
    public void FullNativeComponentInputsSurviveTheirBoundedWireRecords()
    {
        var fleet = new FleetComponent
        {
            id = 9,
            craftId = 3,
            owner = -1,
            dispatch = true,
            target = new SkillTarget { id = 17, type = ETargetType.Enemy, astroId = 101 },
            targetPos = new VectorLF3(1, 2, 3),
            currentAssembleUnitsCount = 2
        };
        var bytes = CraftNativeStateCodec.Encode(fleet);
        var state = new CraftState(false, true, false, 5100, 60, 0, (byte)ECraftProto.Fleet, 0,
            101, -1, 0, 200, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 100, 7, bytes);
        TestAssert.IsTrue(CraftStateCodec.TryEncode(state, out var encoded));
        TestAssert.IsTrue(CraftStateCodec.TryDecode(encoded, 0, encoded.Length, out var restored, out _));
        TestAssert.AreEqual((ushort)7, restored.PlayerOwner);
        TestAssert.IsTrue(CraftNativeStateCodec.TryFleet(restored.NativeFleet, out var result));
        TestAssert.AreEqual(9, result.id); TestAssert.AreEqual(3, result.craftId);
        TestAssert.AreEqual(17, result.target.id); TestAssert.IsTrue(result.dispatch);
        var unit = new UnitComponent
        {
            id = 11,
            craftId = 4,
            protoId = 5101,
            behavior = EUnitBehavior.Engage,
            fire0 = 12,
            fire1 = 34,
            currentInitializeValue = 600000,
            anim = .5f,
            speed = 12
        };
        var body = CraftNativeStateCodec.Encode(unit);
        TestAssert.IsTrue(CraftNativeStateCodec.TryUnit(body, out var resumed));
        TestAssert.AreEqual(34, resumed.fire1); TestAssert.AreEqual(600000, resumed.currentInitializeValue);
        TestAssert.AreEqual(.5f, resumed.anim); TestAssert.AreEqual(12f, resumed.speed);
        TestAssert.IsFalse(CraftNativeStateCodec.TryUnit(body.Take(body.Length - 1).ToArray(), out _));
    }

    [TestMethod]
    public void NativeLoopsAndSharedCommitGuardsInstallOnTheRealGame()
    {
        var harmony = new Harmony("nebulae.tests.native-craft-loops");
        try
        {
            harmony.CreateClassProcessor(typeof(NativeCraftSimulation_Patch)).Patch();
            harmony.CreateClassProcessor(typeof(NativeCraftCommit_Patch)).Patch();
            harmony.CreateClassProcessor(typeof(NativeUnitHealth_Patch)).Patch();
            foreach (var type in new[] { typeof(CombatGroundSystem), typeof(CombatSpaceSystem) })
            {
                var target = AccessTools.Method(type, "GameTick");
                var code = NativeCraftSimulation_Patch.Transpiler(PatchProcessor.GetOriginalInstructions(target)).ToList();
                TestAssert.IsTrue(code.Any(x => x.Calls(AccessTools.Method(typeof(NativeCraftSimulation), nameof(NativeCraftSimulation.UnitId)))));
                TestAssert.IsTrue(code.Any(x => x.Calls(AccessTools.Method(typeof(NativeCraftSimulation), nameof(NativeCraftSimulation.FleetId)))));
                TestAssert.IsTrue(code.Any(x => x.Calls(AccessTools.Method(typeof(UnitComponent), nameof(UnitComponent.UpdateFireCondition)))));
            }
        }
        finally { harmony.UnpatchSelf(); }
    }

    [TestMethod]
    public void PredictedFleetsCannotCreateOrRemoveSharedCrafts()
    {
        var previous = AuthorityRuleGuard.Probe;
        var harmony = new Harmony("nebulae.tests.native-craft-commits");
        try
        {
            AuthorityRuleGuard.Probe = () => new AuthorityGuardSession(true, false, false);
            harmony.CreateClassProcessor(typeof(NativeCraftCommit_Patch)).Patch();
            var fleet = default(FleetComponent); var craft = default(CraftData); var upgrade = default(CombatUpgradeData);
            // Missing world/pools make any accidental native allocation fail immediately.
            fleet.AssembleUnits_Ground(ref craft, ref upgrade, null, null, null, 100);
            fleet.AssembleUnits_Space(ref craft, ref upgrade, null, null, null, 100);
            // Reflection keeps the test caller from inlining the native body before Harmony patches it.
            TestAssert.IsFalse((bool)AccessTools.Method(typeof(FleetComponent), nameof(FleetComponent.DeterminActiveEnemyUnits))
                .Invoke(fleet, [false, 100L])!);
            var factory = Uninitialized<PlanetFactory>();
            factory.craftPool = new CraftData[2]; factory.craftPool[1].prototype = ECraftProto.Fleet;
            factory.RemoveCraftDeferred(1);
            var sector = Uninitialized<SpaceSector>();
            sector.RemoveCraftDeferred(1);
        }
        finally { harmony.UnpatchSelf(); AuthorityRuleGuard.Probe = previous; }
    }
}
