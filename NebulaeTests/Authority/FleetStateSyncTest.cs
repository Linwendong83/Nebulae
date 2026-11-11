using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Utils;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Authority.Adapters;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class FleetStateSyncTest
{
    [TestMethod]
    public void EmptyEnabledSlotsCanKeepTheBulkCommandModeWithoutLaunching()
    {
        var module = new CombatModuleComponent { fleetCount = 1, moduleFleets = [new ModuleFleet { fleetEnabled = true }] };
        TestAssert.IsTrue(FleetCommandMode.Apply(module, 0, true));
        TestAssert.IsTrue(module.moduleFleets[0].inCommand);
        TestAssert.AreEqual(0, module.moduleFleets[0].fleetId);
        TestAssert.IsTrue(FleetCommandMode.Apply(module, 0, false));
        TestAssert.IsFalse(module.moduleFleets[0].inCommand);
    }

    [TestMethod]
    public void LatestOrderSurvivesAnOlderSnapshotAndIgnoresAnOlderRejection()
    {
        var oldGame = GameMain.data; var oldSession = Multiplayer.Session;
        try
        {
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var sector = Uninitialized<SpaceSector>(); game.spaceSector = sector;
            sector.combatSpaceSystem = Uninitialized<CombatSpaceSystem>();
            var pool = sector.combatSpaceSystem.fleets = new DataPool<FleetComponent>(); pool.Reset(); pool.Add();
            var original = new FleetComponent { id = 1, craftId = 2, owner = -1, dispatch = true };
            pool.buffer[1] = original;
            var module = new CombatModuleComponent
            {
                isSpace = true,
                moduleEnabled = true,
                fleetCount = 1,
                moduleFleets = [new ModuleFleet { fleetId = 1, fleetEnabled = true, inCommand = true }]
            };
            var player = Uninitialized<Player>(); player.isAlive = true;
            player.SetHiddenProperty(nameof(Player.mecha), new Mecha { spaceCombatModule = module });
            game.SetHiddenProperty(nameof(GameData.mainPlayer), player);
            var session = Uninitialized<MultiplayerSession>(); Multiplayer.Session = session;
            session.Authority = new AuthoritySessionState(); session.Authority.SetConnection(new ConnectionEpoch(3), 2);
            session.CombatAuthority = new CombatAuthorityManager();
            var prediction = new FleetCommandPrediction();
            var first = prediction.CaptureOrder(module, 0, 1, original);
            first.After = original; first.After.GiveOrbitingAstroOrder(101);
            var second = prediction.CaptureOrder(module, 0, 2, first.After);
            second.After = first.After; second.After.GiveOrbitingAstroOrder(102);
            prediction.Receive(new AuthorityCommandResultPacket { Sequence = 1, ConnectionEpoch = 3, ResultCode = (byte)CommandResultCode.RejectedTarget });
            prediction.Tick();
            TestAssert.AreEqual(102, pool.buffer[1].target.astroId);
            pool.buffer[1] = original; // An older checkpoint arrives while command 2 is pending.
            prediction.Tick();
            TestAssert.AreEqual(102, pool.buffer[1].target.astroId);
            prediction.Receive(new AuthorityCommandResultPacket { Sequence = 2, ConnectionEpoch = 3, ResultCode = (byte)CommandResultCode.RejectedTarget });
            prediction.Tick();
            TestAssert.AreEqual(0, pool.buffer[1].target.astroId);
            TestAssert.AreEqual(0, prediction.PendingCount);
        }
        finally { GameMain.data = oldGame; Multiplayer.Session = oldSession; }
    }

    [TestMethod]
    public void RemotePlanetIdentityChangesTogetherWithoutAUnityTransform()
    {
        var player = Uninitialized<Player>();
        var planet = new PlanetData { id = 104 };
        NativeGameAccess.SetSimulationPlanet(player, planet);
        TestAssert.AreEqual(104, player.planetId);
        TestAssert.AreSame(planet, player.planetData);
        NativeGameAccess.SetSimulationPlanet(player, null);
        TestAssert.AreEqual(0, player.planetId);
        TestAssert.IsNull(player.planetData);
    }

    [TestMethod]
    public void FleetScaleUsesThePilotPlanetInsteadOfTheCameraPlanet()
    {
        var oldGame = GameMain.data; var oldSession = Multiplayer.Session;
        try
        {
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var session = Uninitialized<MultiplayerSession>(); Multiplayer.Session = session;
            var player = Uninitialized<Player>();
            NativeGameAccess.SetSimulationPlanet(player, new PlanetData { id = 101, radius = 200, scale = 1 });
            var position = new VectorLF3(0, 200, 0);
            var sector = Uninitialized<SpaceSector>();
            TestAssert.AreEqual(.2f, FleetPose.Scale(sector, ref position, player));
            game.SetHiddenProperty(nameof(GameData.localPlanet), new PlanetData { radius = 200, uPosition = new VectorLF3(10000, 0, 0) });
            TestAssert.AreEqual(.2f, FleetPose.Scale(sector, ref position, player));
        }
        finally { GameMain.data = oldGame; Multiplayer.Session = oldSession; }
    }

    [TestMethod]
    public void OrdersAndUnitBehaviorPublishOnTheNextSampleWithoutPublishingEveryMovementTick()
    {
        var oldGame = GameMain.data; var oldSession = Multiplayer.Session;
        var oldTick = GameMain.gameTick;
        try
        {
            Multiplayer.Session = null;
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var sector = Uninitialized<SpaceSector>(); game.spaceSector = sector;
            sector.craftPool = new CraftData[3]; sector.craftCursor = 3;
            sector.craftPool[1] = new CraftData { id = 1, protoId = 5100, isSpace = true, dynamic = true, astroId = 100, fleetId = 1 };
            sector.craftPool[2] = new CraftData { id = 2, protoId = 5111, isSpace = true, dynamic = true, astroId = 100, unitId = 1 };
            sector.combatSpaceSystem = Uninitialized<CombatSpaceSystem>();
            var fleets = sector.combatSpaceSystem.fleets = new DataPool<FleetComponent>(); fleets.Reset();
            var units = sector.combatSpaceSystem.units = new DataPool<UnitComponent>(); units.Reset();
            fleets.Add(); units.Add();
            fleets.buffer[1] = new FleetComponent { id = 1, craftId = 1, owner = -1, dispatch = true };
            units.buffer[1] = new UnitComponent { id = 1, craftId = 2, protoId = 5111, behavior = EUnitBehavior.SeekForm };
            GameMain.gameTick = 1000;
            var adapter = new CraftSnapshotAdapter(new AuthorityEpoch(1, 2), _ => null!, () => sector, () => GameMain.gameTick);
            var scope = new ScopeKey(PoolKind.SpaceCraft, 0);
            var keys = new List<ObjectKey>(); adapter.TryReadMembers(scope, keys);
            adapter.TryReadState(keys[0], out var initial);
            adapter.TryReadState(keys[1], out var initialUnit);
            var changes = new List<ObjectKey>(); var removed = new List<ObjectKey>();
            GameMain.gameTick++;
            sector.craftPool[1].pos.x = 5;
            TestAssert.IsTrue(adapter.TryReadChanges(scope, GameMain.gameTick, changes, removed));
            TestAssert.HasCount(2, changes);
            adapter.TryReadState(keys[0], out var movement);
            TestAssert.AreSame(initial, movement, "Continuous steering alone must not allocate a state packet each tick.");
            fleets.buffer[1].GiveOrbitingAstroOrder(104);
            adapter.TryReadState(keys[0], out var order);
            TestAssert.AreNotSame(initial, order);
            TestAssert.IsTrue(CraftStateCodec.TryDecode(order, 0, order.Length, out var decoded, out _));
            TestAssert.IsTrue(CraftNativeStateCodec.TryFleet(decoded.NativeFleet, out var fleet));
            TestAssert.AreEqual(104, fleet.target.astroId);
            units.buffer[1].behavior = EUnitBehavior.Engage;
            adapter.TryReadState(keys[1], out var behavior);
            TestAssert.AreNotSame(initialUnit, behavior);
            // Native creation announces the craft before assigning its fleet/unit ID.
            sector.craftPool[1].id = 0; adapter.Removed(sector, 1);
            sector.craftPool[1] = new CraftData { id = 1, protoId = 5100, isSpace = true, dynamic = true, astroId = 100 };
            adapter.Added(sector, 1);
            sector.craftPool[1].fleetId = 1;
            TestAssert.IsTrue(adapter.TryGetSpaceCraftKey(1, out var replacement));
            adapter.TryReadState(replacement, out _); adapter.StateSampled(replacement);
            changes.Clear(); removed.Clear(); GameMain.gameTick++;
            TestAssert.IsTrue(adapter.TryReadChanges(scope, GameMain.gameTick, changes, removed));
            TestAssert.Contains(replacement, changes, "A newly attached native component needs control sampling immediately.");
        }
        finally { GameMain.data = oldGame; Multiplayer.Session = oldSession; GameMain.gameTick = oldTick; }
    }

    [TestMethod]
    [DataRow(false, 1, 0)]
    [DataRow(true, 1, 0)]
    [DataRow(true, 3, 0)]
    [DataRow(false, 3, 1)]
    [DataRow(true, 3, 1)]
    [DataRow(false, 3, 2)]
    [DataRow(true, 3, 2)]
    [DataRow(false, 3, 3)]
    [DataRow(true, 3, 3)]
    [DataRow(false, 3, 4)]
    [DataRow(false, 3, 5)]
    [DataRow(true, 3, 5)]
    public void BulkModeButtonSendsBothDirectionsAndDoesNotDuplicateNestedKeyboardUpdates(bool space, int count, int delayedReplica)
    {
        var oldGame = GameMain.data; var oldSession = Multiplayer.Session;
        var harmony = new Harmony("nebulae.tests.fleet-bulk-modes");
        try
        {
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var sector = Uninitialized<SpaceSector>(); game.spaceSector = sector;
            sector.combatSpaceSystem = Uninitialized<CombatSpaceSystem>();
            var fleets = sector.combatSpaceSystem.fleets = new DataPool<FleetComponent>(); fleets.Reset();
            for (var id = 1; id <= count; id++)
            { fleets.Add(); fleets.buffer[id] = new FleetComponent { id = id, craftId = id, owner = -1, dispatch = true }; }
            var factory = Uninitialized<PlanetFactory>(); factory.combatGroundSystem = Uninitialized<CombatGroundSystem>();
            factory.combatGroundSystem.fleets = fleets;
            var planet = new PlanetData { id = 101, factory = factory, factoryLoaded = true };
            factory.SetHiddenProperty(nameof(PlanetFactory.planet), planet);
            game.galaxy = sector.galaxy = new GalaxyData { stars = [new StarData { id = 1, planets = [planet] }], astrosFactory = new PlanetFactory[102] };
            sector.galaxy.astrosFactory[101] = factory;
            var player = Uninitialized<Player>(); NativeGameAccess.SetSimulationPlanet(player, planet);
            var module = new CombatModuleComponent
            {
                isSpace = space,
                sector = sector,
                moduleEnabled = true,
                fleetCount = count,
                moduleFleets = Enumerable.Range(1, count).Select(id => new ModuleFleet { fleetId = id, fleetAstroId = 101, fleetEnabled = true, fighters = [new ModuleFighter { count = 1 }] }).ToArray()
            };
            var empty = new CombatModuleComponent { moduleFleets = [] };
            var mecha = new Mecha { player = player, groundCombatModule = space ? empty : module, spaceCombatModule = space ? module : empty };
            player.SetHiddenProperty(nameof(Player.mecha), mecha); game.SetHiddenProperty(nameof(GameData.mainPlayer), player);
            var network = new RecordingNetwork();
            var session = Uninitialized<MultiplayerSession>(); Multiplayer.Session = session;
            session.Network = network; session.Client = Uninitialized<global::NebulaeNetwork.Client>();
            session.Authority = new AuthoritySessionState(); session.Authority.BeginAuthorityWorld(new AuthorityEpoch(1, 2), false);
            session.Authority.SetConnection(new ConnectionEpoch(3), 2);
            session.LocalPlayer = new LocalPlayer { Data = new PlayerData(2, 101) };
            session.CombatAuthority = new CombatAuthorityManager(); session.FleetCommands = new FleetCommandPrediction();
            var panel = Uninitialized<UIZS_FleetPanel>(); panel.mecha = mecha; panel.player = player; panel.fleetTypeIndex = space ? 1 : 0;
            panel.SetHiddenProperty(nameof(ManualBehaviour.data), game);
            // RefreshCompletely has Unity input calls: test its outer capture separately,
            // while executing the actual batch handler and its Harmony hooks here.
            var method = AccessTools.Method(typeof(UIZS_FleetPanel), "OnAllCommandButtonClick");
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(FleetPanelCommand_Patch), nameof(FleetPanelCommand_Patch.Before)),
                postfix: new HarmonyMethod(typeof(FleetPanelCommand_Patch), nameof(FleetPanelCommand_Patch.After)));
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(FleetPanelCommand_Patch), nameof(FleetPanelCommand_Patch.Ready)));
            harmony.CreateClassProcessor(typeof(FleetCommandButton_Patch)).Patch();
            if (delayedReplica != 0)
            {
                // Delay the last fleet: a batch must not mutate the earlier valid slots.
                var slot = module.moduleFleets[count - 1];
                var buffer = fleets.buffer;
                var last = buffer[count];
                if (delayedReplica == 1) module.moduleFleets[count - 1].fleetId = buffer.Length + 100;
                if (delayedReplica == 2) module.moduleFleets[count - 1].fleetId = -1;
                if (delayedReplica == 3) buffer[count].id = 0;
                if (delayedReplica == 4) module.moduleFleets[count - 1].fleetAstroId = 99999;
                if (delayedReplica == 5) fleets.buffer = new FleetComponent[1]; // Cursor alone does not guarantee capacity.
                var entry = Uninitialized<UIZS_FleetEntry>();
                entry.mecha = mecha; entry.isSpace = space; entry.fleetIndex = count - 1; entry.gameData = game;
                AccessTools.Method(typeof(UIZS_FleetEntry), "OnCommandButtonClick").Invoke(entry, [0]);
                method.Invoke(panel, [0]);
                TestAssert.HasCount(0, network.Packets, "Wait for replicas before sending any part of the batch.");
                TestAssert.IsTrue(module.moduleFleets.All(slot => !slot.inCommand));
                TestAssert.AreEqual(0, session.FleetCommands.PendingCount);
                TestAssert.AreEqual(delayedReplica == 2 ? -1 : delayedReplica == 1 ? buffer.Length + 100 : slot.fleetId,
                    module.moduleFleets[count - 1].fleetId, "Readiness checks must preserve authoritative slot references.");
                module.moduleFleets[count - 1] = slot;
                fleets.buffer = buffer; buffer[count] = last;
            }
            foreach (var expected in new[] { true, false, true })
            {
                FleetPanelCommand_Patch.Before(mecha, out var outer);
                method.Invoke(panel, [0]);
                var packetCount = network.Packets.Count;
                FleetPanelCommand_Patch.After(mecha, outer);
                TestAssert.HasCount(packetCount, network.Packets, "Nested UI update must not send the mode twice.");
                var packet = (AuthorityCommandPacket)network.Packets.Last();
                TestAssert.IsTrue(CombatIntent.TryDecode(packet.Payload, out var command));
                TestAssert.AreEqual(CombatIntentKind.FleetCommand, command.Kind);
                TestAssert.AreEqual(expected ? 1d : 0d, command.X);
                TestAssert.AreEqual((space ? 100 : 0) + count - 1, command.Argument);
                TestAssert.AreEqual(expected, module.moduleFleets[0].inCommand);
            }
            TestAssert.HasCount(3 * count, network.Packets);
            session.FleetCommands.Dispose();
        }
        finally { harmony.UnpatchSelf(); GameMain.data = oldGame; Multiplayer.Session = oldSession; }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeDisableButtonsPublishImmediatelyWithoutDeletingClientCraft(bool space)
    {
        var oldGame = GameMain.data; var oldSession = Multiplayer.Session;
        var harmony = new Harmony("nebulae.tests.fleet-disable-settings");
        try
        {
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var player = Uninitialized<Player>();
            var mecha = new Mecha { player = player, energyShieldRechargeSpeed = 2 };
            player.SetHiddenProperty(nameof(Player.mecha), mecha); game.SetHiddenProperty(nameof(GameData.mainPlayer), player);
            CombatModuleComponent Module(bool isSpace) => new()
            {
                isSpace = isSpace,
                moduleEnabled = true,
                fleetCount = 1,
                attackBuilding = true,
                moduleFleets = [new ModuleFleet { fleetEnabled = true, fleetId = 99999,
                    fleetAstroId = 104, fighters = [new ModuleFighter { craftId = 88888, count = 1 }] }]
            };
            mecha.groundCombatModule = Module(false); mecha.spaceCombatModule = Module(true);
            var module = space ? mecha.spaceCombatModule : mecha.groundCombatModule;
            var network = new RecordingNetwork();
            var session = Uninitialized<MultiplayerSession>(); Multiplayer.Session = session;
            session.Network = network; session.Client = Uninitialized<global::NebulaeNetwork.Client>();
            session.IsGameLoaded = true;
            session.Authority = new AuthoritySessionState(); session.Authority.BeginAuthorityWorld(new AuthorityEpoch(1, 2), false);
            session.Authority.SetConnection(new ConnectionEpoch(3), 2);
            session.LocalPlayer = new LocalPlayer { Data = new PlayerData(2, 104) };
            session.CombatAuthority = new CombatAuthorityManager();
            harmony.CreateClassProcessor(typeof(FleetSettings_Patch)).Patch();
            harmony.Patch(AccessTools.Method(typeof(CombatModuleComponent), nameof(CombatModuleComponent.RemoveFleetDirectly)),
                prefix: new HarmonyMethod(typeof(PlayerCombatAuthority_Patch), nameof(PlayerCombatAuthority_Patch.RemoveFleetDirectly_Prefix)));
            var panel = Uninitialized<UIZS_FleetPanel>(); panel.mecha = mecha; panel.fleetTypeIndex = space ? 1 : 0;
            var entry = Uninitialized<UIZS_FleetEntry>(); entry.mecha = mecha; entry.isSpace = space; entry.fleetIndex = 0;
            var window = Uninitialized<UIMechaWindow>();
            AccessTools.PropertySetter(typeof(ManualBehaviour), "data").Invoke(window, [mecha]);
            AccessTools.Field(typeof(UIMechaWindow), "fleetTabIndex").SetValue(window, space ? 1 : 0);
            // Establish the baseline mask first; the toggles below are subsequent reports.
            session.CombatAuthority.SendSettings(0);
            network.Packets.Clear();
            foreach (var target in new[] { "OnAttackBuildingButtonClick", "OnAttackBuildingButtonClick",
                         "OnEnableModuleButtonClick", "OnEnableModuleButtonClick", "OnFleetEnableButtonClick", "OnFleetEnableButtonClick",
                         "OnAutoReplenishButtonClick", "OnAutoReplenishButtonClick" })
            {
                var before = network.Packets.Count;
                var owner = target == "OnFleetEnableButtonClick" ? (object)entry : target == "OnAutoReplenishButtonClick" ? window : panel;
                AccessTools.Method(owner.GetType(), target).Invoke(owner, [target == "OnAutoReplenishButtonClick" ? 3 : 0]);
                TestAssert.HasCount(before + 1, network.Packets, "Every toggle must be sent in the same tick, even inside the 15-tick throttle.");
                var packet = (AuthorityCommandPacket)network.Packets.Last();
                TestAssert.IsTrue(CombatIntent.TryDecode(packet.Payload, out var intent));
                if (target == "OnFleetEnableButtonClick" && !space)
                {
                    // Ground fleets have one transmitted bit each; the mask is their own command.
                    TestAssert.AreEqual(CombatIntentKind.GroundFleetMask, intent.Kind);
                    TestAssert.AreEqual(CombatAuthorityManager.GroundFleetMask(module), intent.Argument);
                }
                else
                {
                    TestAssert.AreEqual(CombatIntentKind.Settings, intent.Kind);
                    var ground = new CombatModuleComponent(); var orbital = new CombatModuleComponent();
                    CombatFleetSettings.Apply(intent.Argument, ground, orbital);
                    TestAssert.AreEqual(module.attackBuilding, space ? orbital.attackBuilding : ground.attackBuilding);
                    TestAssert.AreEqual(module.autoReplenishFleet, space ? orbital.autoReplenishFleet : ground.autoReplenishFleet);
                    TestAssert.AreEqual(module.moduleEnabled, (intent.Argument & (space ? 16 : 8)) != 0);
                    if (space)
                        TestAssert.AreEqual(module.moduleFleets[0].fleetEnabled, (intent.Argument & (1 << 8)) != 0);
                }
                TestAssert.AreEqual(99999, module.moduleFleets[0].fleetId);
                TestAssert.AreEqual(88888, module.moduleFleets[0].fighters[0].craftId);
                TestAssert.AreEqual(1, module.moduleFleets[0].fighters[0].count);
            }
        }
        finally { harmony.UnpatchSelf(); GameMain.data = oldGame; Multiplayer.Session = oldSession; }
    }

    [TestMethod]
    public void GroundFleetEnableMaskCoversEveryFleetAndIgnoresUnrelatedSettings()
    {
        var oldGame = GameMain.data; var oldSession = Multiplayer.Session;
        try
        {
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var player = Uninitialized<Player>();
            var mecha = new Mecha { player = player, energyShieldRechargeEnabled = false, energyShieldRechargeSpeed = 2 };
            player.SetHiddenProperty(nameof(Player.mecha), mecha);
            game.SetHiddenProperty(nameof(GameData.mainPlayer), player);
            CombatModuleComponent Module(bool isSpace) => new()
            {
                isSpace = isSpace,
                moduleEnabled = true,
                fleetCount = 4,
                attackRelay = false,
                attackBuilding = false,
                moduleFleets =
                [
                    new ModuleFleet { fleetEnabled = true }, new ModuleFleet { fleetEnabled = true },
                    new ModuleFleet { fleetEnabled = false }, new ModuleFleet { fleetEnabled = true }
                ]
            };
            mecha.groundCombatModule = Module(false);
            mecha.spaceCombatModule = Module(true);
            var network = new RecordingNetwork();
            var session = Uninitialized<MultiplayerSession>(); Multiplayer.Session = session;
            session.Network = network; session.Client = Uninitialized<global::NebulaeNetwork.Client>();
            session.IsGameLoaded = true;
            session.Authority = new AuthoritySessionState(); session.Authority.BeginAuthorityWorld(new AuthorityEpoch(1, 2), false);
            session.Authority.SetConnection(new ConnectionEpoch(3), 2);
            session.LocalPlayer = new LocalPlayer { Data = new PlayerData(2, 104) };
            session.CombatAuthority = new CombatAuthorityManager();

            List<CombatIntent> Decoded()
            {
                var result = new List<CombatIntent>();
                foreach (var packet in network.Packets.OfType<AuthorityCommandPacket>())
                    if (CombatIntent.TryDecode(packet.Payload, out var intent)) result.Add(intent);
                return result;
            }

            var server = Module(false);
            for (var i = 0; i < server.moduleFleets.Length; i++) server.moduleFleets[i].fleetEnabled = false;

            session.CombatAuthority.SendSettings(100);
            var first = Decoded();
            var mask = first.Single(x => x.Kind == CombatIntentKind.GroundFleetMask);
            TestAssert.AreEqual(0b1011, mask.Argument, "A, B and D are enabled; C is not.");
            var settings = first.Single(x => x.Kind == CombatIntentKind.Settings);
            CombatFleetSettings.Apply(settings.Argument, server, Module(true));
            CombatAuthorityManager.ApplyModuleEnabled(server, true);
            TestAssert.IsFalse(server.moduleFleets.Any(f => f.fleetEnabled),
                "The settings word must never invent ground fleet enablement.");
            TestAssert.IsTrue(CombatAuthorityManager.TryApplyGroundFleetMask(server, mask.Argument));
            TestAssert.IsFalse(CombatAuthorityManager.TryApplyGroundFleetMask(server, 1 << 4),
                "A mask naming a fleet beyond the fourth must be rejected.");
            TestAssert.IsFalse(CombatAuthorityManager.TryApplyGroundFleetMask(server, -1));
            TestAssert.IsTrue(server.moduleFleets[0].fleetEnabled);
            TestAssert.IsTrue(server.moduleFleets[1].fleetEnabled);
            TestAssert.IsFalse(server.moduleFleets[2].fleetEnabled);
            TestAssert.IsTrue(server.moduleFleets[3].fleetEnabled);

            // C is disabled by the player; the space attack-building toggle moves independently.
            mecha.groundCombatModule.moduleFleets[2].fleetEnabled = false;
            mecha.groundCombatModule.moduleFleets[3].fleetEnabled = false;
            mecha.spaceCombatModule.attackBuilding = true;
            network.Packets.Clear();
            session.CombatAuthority.SendSettings(101, immediate: true);
            mask = Decoded().Single(x => x.Kind == CombatIntentKind.GroundFleetMask);
            TestAssert.AreEqual(0b0011, mask.Argument);
            CombatAuthorityManager.ApplyFleetMask(server, mask.Argument);
            TestAssert.IsTrue(server.moduleFleets[0].fleetEnabled);
            TestAssert.IsTrue(server.moduleFleets[1].fleetEnabled);
            TestAssert.IsFalse(server.moduleFleets[2].fleetEnabled);
            TestAssert.IsFalse(server.moduleFleets[3].fleetEnabled,
                "The fourth formation must not follow the space attack-building bit.");

            // A space-only settings change must leave the ground mask alone.
            network.Packets.Clear();
            mecha.spaceCombatModule.attackBuilding = false;
            session.CombatAuthority.SendSettings(102, immediate: true);
            var next = Decoded();
            TestAssert.IsFalse(next.Any(x => x.Kind == CombatIntentKind.GroundFleetMask),
                "An unchanged ground mask is not a new report.");
            settings = next.Single(x => x.Kind == CombatIntentKind.Settings);
            var before = server.moduleFleets.Select(f => f.fleetEnabled).ToArray();
            CombatFleetSettings.Apply(settings.Argument, server, Module(true));
            CombatAuthorityManager.ApplyModuleEnabled(server, true);
            for (var i = 0; i < server.fleetCount; i++)
                TestAssert.AreEqual(before[i], server.moduleFleets[i].fleetEnabled);
        }
        finally { GameMain.data = oldGame; Multiplayer.Session = oldSession; }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void HostDisablingASquadOrModuleRunsNativeRemovalAndKeepsFighterStock(bool space, bool disableModule)
    {
        var oldGame = GameMain.data; var oldSession = Multiplayer.Session;
        var harmony = new Harmony("nebulae.tests.fleet-disable-host");
        try
        {
            Multiplayer.Session = null;
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var sector = Uninitialized<SpaceSector>(); game.spaceSector = sector;
            sector.combatSpaceSystem = Uninitialized<CombatSpaceSystem>();
            var fleets = sector.combatSpaceSystem.fleets = new DataPool<FleetComponent>(); fleets.Reset();
            fleets.Add(); fleets.buffer[1] = new FleetComponent { id = 1, craftId = 2, dispatch = true };
            var factory = Uninitialized<PlanetFactory>(); factory.combatGroundSystem = Uninitialized<CombatGroundSystem>();
            factory.combatGroundSystem.fleets = fleets;
            sector.galaxy = new GalaxyData { astrosFactory = new PlanetFactory[105] }; sector.galaxy.astrosFactory[104] = factory;
            var module = new CombatModuleComponent
            {
                sector = sector,
                isSpace = space,
                moduleEnabled = true,
                fleetCount = 1,
                moduleFleets = [new ModuleFleet { fleetId = 1, fleetAstroId = 104, fleetEnabled = true, inCommand = true,
                    fighters = [new ModuleFighter { craftId = 3, itemId = 5101, count = 1 }] }]
            };
            // Resource cleanup needs a scene. Run the real RemoveFleetDirectly body with only
            // its world removal calls shimmed; its port/stock/command writes stay native.
            foreach (var type in new[] { typeof(PlanetFactory), typeof(SpaceSector) })
                harmony.Patch(AccessTools.Method(type, "RemoveCraftWithComponents"),
                    prefix: new HarmonyMethod(typeof(FleetStateSyncTest), nameof(SkipCraftRemoval)));
            CombatAuthorityManager.ApplyFleetEnabled(module, !disableModule, disableModule ? 1 : 0);
            TestAssert.AreEqual(0, module.moduleFleets[0].fleetId);
            TestAssert.AreEqual(0, module.moduleFleets[0].fleetAstroId);
            TestAssert.IsFalse(module.moduleFleets[0].inCommand);
            TestAssert.AreEqual(0, module.moduleFleets[0].fighters[0].craftId);
            TestAssert.AreEqual(1, module.moduleFleets[0].fighters[0].count);
            TestAssert.AreEqual(5101, module.moduleFleets[0].fighters[0].itemId);
            CombatAuthorityManager.ApplyFleetEnabled(module, true, 1);
            TestAssert.IsTrue(module.moduleEnabled); TestAssert.IsTrue(module.moduleFleets[0].fleetEnabled);
        }
        finally { harmony.UnpatchSelf(); GameMain.data = oldGame; Multiplayer.Session = oldSession; }
    }

    [TestMethod]
    public void MaskDisablingTheFourthFleetRemovesOnlyThatFleet()
    {
        var oldGame = GameMain.data; var oldSession = Multiplayer.Session;
        var harmony = new Harmony("nebulae.tests.fleet-mask-removal");
        try
        {
            Multiplayer.Session = null;
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var sector = Uninitialized<SpaceSector>(); game.spaceSector = sector;
            sector.combatSpaceSystem = Uninitialized<CombatSpaceSystem>();
            var fleets = sector.combatSpaceSystem.fleets = new DataPool<FleetComponent>(); fleets.Reset();
            for (var i = 1; i <= 4; i++)
            {
                fleets.Add();
                fleets.buffer[i] = new FleetComponent { id = i, craftId = i + 1, dispatch = true };
            }
            var module = new CombatModuleComponent
            {
                sector = sector,
                isSpace = true,
                moduleEnabled = true,
                fleetCount = 4,
                moduleFleets = Enumerable.Range(0, 4).Select(i => new ModuleFleet
                {
                    fleetId = i + 1,
                    fleetAstroId = 0,
                    fleetEnabled = true,
                    inCommand = true,
                    fighters = [new ModuleFighter { craftId = i + 10, itemId = 5101, count = 1 }]
                }).ToArray()
            };
            harmony.Patch(AccessTools.Method(typeof(SpaceSector), "RemoveCraftWithComponents"),
                prefix: new HarmonyMethod(typeof(FleetStateSyncTest), nameof(SkipCraftRemoval)));
            CombatAuthorityManager.ApplyFleetMask(module, 0b0111);
            for (var i = 0; i < 3; i++)
            {
                TestAssert.IsTrue(module.moduleFleets[i].fleetEnabled);
                TestAssert.AreEqual(i + 1, module.moduleFleets[i].fleetId);
                TestAssert.AreEqual(i + 10, module.moduleFleets[i].fighters[0].craftId);
            }
            TestAssert.IsFalse(module.moduleFleets[3].fleetEnabled);
            TestAssert.AreEqual(0, module.moduleFleets[3].fleetId);
            TestAssert.AreEqual(0, module.moduleFleets[3].fighters[0].craftId);
        }
        finally { harmony.UnpatchSelf(); GameMain.data = oldGame; Multiplayer.Session = oldSession; }
    }

    private static bool SkipCraftRemoval() => false;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DisablingWhileACommandIsPendingCancelsItsPrediction(bool disableModule)
    {
        var oldGame = GameMain.data; var oldSession = Multiplayer.Session;
        try
        {
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var player = Uninitialized<Player>(); player.isAlive = true;
            var module = new CombatModuleComponent
            {
                isSpace = true,
                moduleEnabled = true,
                fleetCount = 1,
                moduleFleets = [new ModuleFleet { fleetEnabled = true }]
            };
            player.SetHiddenProperty(nameof(Player.mecha), new Mecha { spaceCombatModule = module });
            game.SetHiddenProperty(nameof(GameData.mainPlayer), player);
            var session = Uninitialized<MultiplayerSession>(); Multiplayer.Session = session;
            session.Authority = new AuthoritySessionState();
            var prediction = new FleetCommandPrediction();
            prediction.Begin(module, 0, false, 1, true);
            TestAssert.AreEqual(1, prediction.PendingCount);
            if (disableModule) module.moduleEnabled = false;
            else module.moduleFleets[0].fleetEnabled = false;
            prediction.Tick();
            TestAssert.AreEqual(0, prediction.PendingCount);
            TestAssert.IsFalse(module.moduleFleets[0].inCommand);
            prediction.Dispose();
        }
        finally { GameMain.data = oldGame; Multiplayer.Session = oldSession; }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FleetUiWaitsForMatchingPoolsAndRejectsRemovedOrOutOfRangeSlots(bool space)
    {
        var oldGame = GameMain.data; var oldSession = Multiplayer.Session;
        try
        {
            var session = Uninitialized<MultiplayerSession>(); session.Client = Uninitialized<global::NebulaeNetwork.Client>(); Multiplayer.Session = session;
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var sector = Uninitialized<SpaceSector>(); game.spaceSector = sector;
            sector.skillSystem = Uninitialized<SkillSystem>(); sector.skillSystem.astroFactories = new PlanetFactory[105];
            sector.combatSpaceSystem = Uninitialized<CombatSpaceSystem>();
            var fleets = sector.combatSpaceSystem.fleets = new DataPool<FleetComponent>(); fleets.Reset();
            var factory = Uninitialized<PlanetFactory>(); factory.combatGroundSystem = Uninitialized<CombatGroundSystem>(); factory.combatGroundSystem.fleets = fleets;
            sector.skillSystem.astroFactories[104] = factory;
            var module = new CombatModuleComponent
            {
                isSpace = space,
                fleetCount = 1,
                moduleFleets = [new ModuleFleet { fleetId = 99999, fleetAstroId = 104 }]
            };
            var mecha = new Mecha { groundCombatModule = module, spaceCombatModule = module };
            TestAssert.IsFalse(FleetEntryReadiness_Patch.Prefix(game, mecha, space, 0));
            fleets.Add(); module.moduleFleets[0].fleetId = 1;
            TestAssert.IsTrue(FleetEntryReadiness_Patch.Prefix(game, mecha, space, 0));
            fleets.buffer[1].id = 0;
            TestAssert.IsFalse(FleetEntryReadiness_Patch.Prefix(game, mecha, space, 0));
            module.moduleFleets[0].fleetAstroId = 99999;
            TestAssert.IsFalse(FleetEntryReadiness_Patch.Prefix(game, mecha, space, 0));
            module.moduleFleets[0].fleetId = 0;
            TestAssert.IsTrue(FleetEntryReadiness_Patch.Prefix(game, mecha, space, 0));
            TestAssert.IsFalse(FleetEntryReadiness_Patch.Prefix(game, mecha, space, 1));
            TestAssert.IsFalse(FleetEntryReadiness_Patch.Prefix(game, mecha, space, -1));
        }
        finally { GameMain.data = oldGame; Multiplayer.Session = oldSession; }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RespawnFleetClearDropsAKeyTheLocalPoolCannotIndex(bool space)
    {
        var oldGame = GameMain.data; var oldSession = Multiplayer.Session;
        var harmony = new Harmony("nebulae.tests.fleet-order-slot-guard");
        try
        {
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var sector = Uninitialized<SpaceSector>(); game.spaceSector = sector;
            sector.combatSpaceSystem = Uninitialized<CombatSpaceSystem>();
            // Reset() gives the pool eight slots; the host id below is past its capacity, so the
            // native CancelAllFleetsOrder index would throw before the death action could reset.
            var fleets = sector.combatSpaceSystem.fleets = new DataPool<FleetComponent>(); fleets.Reset();
            fleets.Add();
            var factory = Uninitialized<PlanetFactory>();
            factory.combatGroundSystem = Uninitialized<CombatGroundSystem>(); factory.combatGroundSystem.fleets = fleets;
            game.galaxy = sector.galaxy = new GalaxyData { astrosFactory = new PlanetFactory[105] };
            sector.galaxy.astrosFactory[104] = factory;
            var module = new CombatModuleComponent
            {
                isSpace = space,
                sector = sector,
                moduleEnabled = true,
                fleetCount = 2,
                moduleFleets =
                [
                    new ModuleFleet { fleetId = 1, fleetAstroId = 104, fleetEnabled = true, inCommand = true },
                    new ModuleFleet { fleetId = fleets.buffer.Length + 1, fleetAstroId = 104, fleetEnabled = true, inCommand = true }
                ]
            };
            fleets.buffer[1] = new FleetComponent { id = 1, craftId = 1, owner = -1, dispatch = false };
            fleets.buffer[1].GiveOrbitingAstroOrder(104);
            var player = Uninitialized<Player>(); player.isAlive = true;
            player.SetHiddenProperty(nameof(Player.mecha), new Mecha { spaceCombatModule = module, groundCombatModule = module });
            game.SetHiddenProperty(nameof(GameData.mainPlayer), player);
            var session = Uninitialized<MultiplayerSession>(); Multiplayer.Session = session;
            harmony.CreateClassProcessor(typeof(FleetOrderSlotGuard_Patch)).Patch();

            TestAssert.IsGreaterThanOrEqualTo(fleets.buffer.Length, module.moduleFleets[1].fleetId,
                "The replicated key must sit outside the local pool for this regression to mean anything.");
            module.CancelAllFleetsOrder();

            TestAssert.AreEqual(0, fleets.buffer[1].target.astroId, "A live fleet still receives the native order.");
            TestAssert.IsTrue(fleets.buffer[1].dispatch);
            TestAssert.AreEqual(0, module.moduleFleets[1].fleetId, "A key the pool cannot resolve is dropped so a replica can rebind it.");
            TestAssert.AreEqual(0, module.moduleFleets[1].fleetAstroId);
            TestAssert.IsFalse(module.moduleFleets[1].inCommand);
            TestAssert.AreEqual(1, module.moduleFleets[0].fleetId, "Resolution must not rewrite healthy slots.");
            TestAssert.AreEqual(2, module.fleetCount);

            // The single-slot path shares the rule, including an index the module no longer has.
            module.moduleFleets[1].fleetId = fleets.buffer.Length + 1;
            module.moduleFleets[1].fleetAstroId = 104;
            module.CancelFleetOrder(7);
            module.CancelFleetOrder(1);
            TestAssert.AreEqual(0, module.moduleFleets[1].fleetId);
        }
        finally { harmony.UnpatchSelf(); GameMain.data = oldGame; Multiplayer.Session = oldSession; }
    }

}
