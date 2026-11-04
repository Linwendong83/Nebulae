using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Utils;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class FleetCombatRegressionTest
{
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public void ReplicaBuildingsWithoutBuilderComponentsRespectTheToggleAndClearExistingTargets(bool space, bool attackBuildings, bool mobile)
    {
        var oldGame = GameMain.data; var oldSession = Multiplayer.Session;
        var harmony = new Harmony("nebulae.tests.replica-building-targets");
        try
        {
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var sector = Uninitialized<SpaceSector>(); game.spaceSector = sector;
            var factory = Uninitialized<PlanetFactory>();
            var enemies = new EnemyData[2];
            enemies[1] = new EnemyData
            {
                id = 1,
                builderId = 0,
                dynamic = mobile,
                astroId = space ? 100 : 101,
                originAstroId = space ? 1000001 : 101,
                pos = new VectorLF3(10, 0, 0)
            };
            factory.enemyPool = sector.enemyPool = enemies;
            sector.skillSystem = Uninitialized<SkillSystem>(); sector.skillSystem.astroFactories = new PlanetFactory[102];
            sector.skillSystem.astroFactories[101] = factory;
            var player = Uninitialized<Player>(); var module = new CombatModuleComponent { attackBuilding = attackBuildings };
            var mecha = new Mecha { groundCombatModule = module, spaceCombatModule = module };
            player.SetHiddenProperty(nameof(Player.mecha), mecha); game.SetHiddenProperty(nameof(GameData.mainPlayer), player);
            var session = Uninitialized<MultiplayerSession>(); session.Client = Uninitialized<global::NebulaeNetwork.Client>(); Multiplayer.Session = session;
            harmony.CreateClassProcessor(typeof(UnitTargetClassification_Patch)).Patch();
            var unit = new UnitComponent { id = 1, craftId = 2, behavior = EUnitBehavior.SeekForm };
            var target = new SkillTarget { id = 1, type = ETargetType.Enemy, astroId = space ? 1000001 : 101 };
            var position = VectorLF3.zero;
            unit.DetermineBehavior(ref target, ref position, 0, true);
            TestAssert.AreEqual(attackBuildings || mobile ? 1 : 0, unit.hatred.max.objectId);
            unit.hatred.HateTarget(EObjectType.Enemy, 1, 100, 100, EHatredOperation.Set);
            var cleared = space ? unit.ClearSpaceHatredBuilding(sector) : unit.ClearLocalHatredBuilding(factory);
            TestAssert.AreEqual(!mobile, cleared, "Switching off must also be able to discard a building already held in hatred.");
            TestAssert.AreEqual(0, enemies[1].builderId, "Targeting must not create a builder component in the replica.");
        }
        finally { harmony.UnpatchSelf(); GameMain.data = oldGame; Multiplayer.Session = oldSession; }
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public void DeployedCommandTransitionsMatchTheNativeButton(bool space, bool dispatch, bool command)
    {
        var previous = GameMain.data; var previousSession = Multiplayer.Session;
        try
        {
            Multiplayer.Session = null;
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var sector = Uninitialized<SpaceSector>(); game.spaceSector = sector;
            sector.combatSpaceSystem = Uninitialized<CombatSpaceSystem>();
            var fleets = sector.combatSpaceSystem.fleets = new DataPool<FleetComponent>(); fleets.Reset();
            var factory = Uninitialized<PlanetFactory>();
            factory.combatGroundSystem = Uninitialized<CombatGroundSystem>(); factory.combatGroundSystem.fleets = fleets;
            var planet = new PlanetData { id = 101, factory = factory, factoryLoaded = true };
            factory.SetHiddenProperty(nameof(PlanetFactory.planet), planet);
            game.galaxy = sector.galaxy = new GalaxyData { stars = [new StarData { id = 1, planets = [planet] }], astrosFactory = new PlanetFactory[102] };
            sector.galaxy.astrosFactory[101] = factory;
            var player = Uninitialized<Player>(); player.SetHiddenProperty(nameof(Player.planetData), planet);
            var id = fleets.Add().id;
            var initial = new FleetComponent
            {
                id = id,
                craftId = 12,
                owner = -1,
                dispatch = dispatch,
                target = new SkillTarget { id = 8, astroId = 1000001, type = ETargetType.Enemy },
                targetPos = new VectorLF3(2, 3, 4)
            };
            fleets.buffer[id] = initial;
            var module = new CombatModuleComponent
            {
                isSpace = space,
                sector = sector,
                moduleEnabled = true,
                fleetCount = 1,
                moduleFleets = [new ModuleFleet { fleetId = id, fleetAstroId = 101, fleetEnabled = true, inCommand = command }]
            };
            var entry = Uninitialized<UIZS_FleetEntry>(); entry.gameData = game;
            entry.mecha = new Mecha { player = player, spaceCombatModule = module, groundCombatModule = module }; entry.isSpace = space; entry.fleetIndex = 0;
            AccessTools.Method(typeof(UIZS_FleetEntry), "OnCommandButtonClick").Invoke(entry, [0]);
            var native = fleets.buffer[id]; var nativeCommand = module.moduleFleets[0].inCommand;
            fleets.buffer[id] = initial; module.moduleFleets[0].inCommand = command;
            TestAssert.IsTrue(FleetCommandMode.Apply(module, 0, !dispatch || !command));
            TestAssert.AreEqual(nativeCommand, module.moduleFleets[0].inCommand);
            TestAssert.AreEqual(native.dispatch, fleets.buffer[id].dispatch);
            TestAssert.AreEqual(native.target, fleets.buffer[id].target);
            TestAssert.AreEqual(native.targetPos, fleets.buffer[id].targetPos);
        }
        finally { GameMain.data = previous; Multiplayer.Session = previousSession; }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeSpaceSensorUsesTheFleetsPilotRelaySetting(bool attackRelay)
    {
        var previousGame = GameMain.data; var previousSession = Multiplayer.Session;
        var harmony = new Harmony("nebulae.tests.fleet-sensor-owner");
        try
        {
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var host = Uninitialized<Player>(); host.SetHiddenProperty(nameof(Player.mecha), new Mecha { spaceCombatModule = new CombatModuleComponent { attackRelay = !attackRelay } });
            game.SetHiddenProperty(nameof(GameData.mainPlayer), host);
            var pilot = new Mecha { spaceCombatModule = new CombatModuleComponent { attackRelay = attackRelay } };
            var session = Uninitialized<MultiplayerSession>();
            session.Client = Uninitialized<global::NebulaeNetwork.Client>(); session.LocalPlayer = new LocalPlayer { Data = new PlayerData(2, 101) };
            session.Combat = new CombatManager { Players = [new CombatManager.PlayerPosition { id = 7, mecha = pilot }], IndexByPlayerId = new() { [7] = 0 } };
            Multiplayer.Session = session;
            var sector = Uninitialized<SpaceSector>(); game.spaceSector = sector;
            sector.craftPool = new CraftData[3]; sector.craftPool[1] = new CraftData { id = 1, owner = -1, fleetId = 1 };
            sector.craftPool[2] = new CraftData { id = 2, owner = 1, astroId = 100 };
            sector.combatSpaceSystem = Uninitialized<CombatSpaceSystem>();
            sector.combatSpaceSystem.fleets = new DataPool<FleetComponent>(); sector.combatSpaceSystem.fleets.Reset();
            sector.enemyPool = new EnemyData[2]; sector.enemyCursor = 2;
            sector.enemyPool[1] = new EnemyData { id = 1, dfRelayId = 1, originAstroId = 1000001, astroId = 100, pos = new VectorLF3(10, 0, 0) };
            var hive = Uninitialized<EnemyDFHiveSystem>(); hive.starData = new StarData { id = 1 };
            sector.dfHivesByAstro = new EnemyDFHiveSystem[2]; sector.dfHivesByAstro[1] = hive;
            sector.skillSystem = Uninitialized<SkillSystem>(); sector.skillSystem.combatStats = new DataPool<CombatStat>(); sector.skillSystem.combatStats.Reset();
            var state = new CraftState(false, true, true, 5111, 60, 0, (byte)ECraftProto.Fleet, 0, 100, 1, 0,
                0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, playerOwner: 7);
            NativeCraftSimulation.Install(sector, sector.craftPool, 2, 2, state);
            harmony.CreateClassProcessor(typeof(UnitSensorOwner_Patch)).Patch();
            var unit = new UnitComponent { id = 1, craftId = 2 };
            unit.SensorLogic_Space(ref sector.craftPool[2], new PrefabDesc { craftUnitSensorRange = 1000 }, sector);
            TestAssert.AreEqual(attackRelay ? 1 : 0, unit.hatred.max.objectId,
                "A host/local player's opposite relay toggle must not change another pilot's targeting.");
            unit.behavior = EUnitBehavior.Engage;
            unit.PostGameTick_Space(sector, new PrefabDesc(), ref sector.craftPool[2]);
            TestAssert.AreEqual(attackRelay ? 1 : 0, unit.hatred.max.objectId,
                "PostGameTick must retain targets using the same pilot setting as the sensor.");
            host.mecha.spaceCombatModule.attackBuilding = !attackRelay;
            pilot.spaceCombatModule.attackBuilding = attackRelay;
            sector.enemyPool[1].builderId = 1;
            unit.hatred.Reset(); unit.behavior = EUnitBehavior.SeekForm;
            var target = new SkillTarget { id = 1, astroId = 1000001, type = ETargetType.Enemy };
            var position = VectorLF3.zero;
            unit.DetermineBehavior(ref target, ref position, 0, true);
            TestAssert.AreEqual(attackRelay ? 1 : 0, unit.hatred.max.objectId,
                "Manual building orders must also use the pilot's settings.");
        }
        finally { harmony.UnpatchSelf(); GameMain.data = previousGame; Multiplayer.Session = previousSession; }
    }

    [TestMethod]
    public void RecallKeepsManualCommandUntilTheNativeFleetReturns()
    {
        var previousGame = GameMain.data; var previousSession = Multiplayer.Session;
        try
        {
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var sector = Uninitialized<SpaceSector>(); game.spaceSector = sector;
            sector.combatSpaceSystem = Uninitialized<CombatSpaceSystem>();
            var fleets = sector.combatSpaceSystem.fleets = new DataPool<FleetComponent>(); fleets.Reset();
            var id = fleets.Add().id;
            fleets.buffer[id] = new FleetComponent { id = id, craftId = 2, dispatch = true };
            var module = new CombatModuleComponent
            {
                isSpace = true,
                fleetCount = 1,
                moduleFleets = [new ModuleFleet { fleetId = id, inCommand = true }]
            };
            var session = Uninitialized<MultiplayerSession>(); session.Authority = new NebulaeWorld.Authority.AuthoritySessionState();
            Multiplayer.Session = session;
            var prediction = new FleetCommandPrediction();
            prediction.Begin(module, 0, true, 1);
            TestAssert.IsTrue(module.moduleFleets[0].inCommand, "Vanilla RecycleFleet leaves this set until OnFleetComponentRemoved; clearing it early lets auto-defence cancel recall.");
            TestAssert.IsFalse(fleets.buffer[id].dispatch);
            prediction.Dispose();
        }
        finally { GameMain.data = previousGame; Multiplayer.Session = previousSession; }
    }

    [TestMethod]
    public void FleetHooksInstallOnTheActualGameAndCommandsRoundTrip()
    {
        var harmony = new Harmony("nebulae.tests.fleet-command-hooks");
        try
        {
            harmony.CreateClassProcessor(typeof(FleetCommandButton_Patch)).Patch();
            // CommandFleets contains Unity Physics/Camera internal calls and requires the
            // runtime scene. Install its resolved order hooks here; the runtime suite drives
            // the real cursor-selection entry point on all peers.
            foreach (var (target, patch) in DeclaredPatches(typeof(FleetOrder_Patch)))
                if (target?.DeclaringType == typeof(FleetComponent))
                {
                    if (patch.IsDefined(typeof(HarmonyPostfix), false)) harmony.Patch(target, postfix: new HarmonyMethod(patch));
                    else harmony.Patch(target, prefix: new HarmonyMethod(patch));
                }
            harmony.Patch(AccessTools.Method(typeof(CombatModuleComponent), nameof(CombatModuleComponent.CommandFleetsToPos)),
                prefix: new HarmonyMethod(typeof(FleetOrder_Patch), nameof(FleetOrder_Patch.Enter)),
                finalizer: new HarmonyMethod(typeof(FleetOrder_Patch), nameof(FleetOrder_Patch.Exit)));
            harmony.CreateClassProcessor(typeof(UnitSensorOwner_Patch)).Patch();
            harmony.CreateClassProcessor(typeof(FleetPose_Patch)).Patch();
            foreach (var kind in new[] { CombatIntentKind.FleetCommand, CombatIntentKind.FleetAttackTarget,
                CombatIntentKind.FleetAttackPosition, CombatIntentKind.FleetOrbit, CombatIntentKind.FleetCancel })
            {
                TestAssert.IsTrue(CombatIntent.TryDecode(new CombatIntent(kind, 103, 1, 2, 3).Encode(), out var decoded));
                TestAssert.AreEqual(kind, decoded.Kind); TestAssert.AreEqual(103, decoded.Argument);
            }
        }
        finally { harmony.UnpatchSelf(); }
    }
}
