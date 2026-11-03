using System.Collections;
using System.Reflection;
using HarmonyLib;
using NebulaeModel.Contracts;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Factory;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;
using NebulaeWorld.Combat;
using NebulaeWorld.Factory;
using NebulaeWorld.Player;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class DroneDeathTest
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoteDeathImmediatelyRemovesOnlyItsOwnViewsWithoutTouchingNativeDrones(bool server)
    {
        using var f = new Fixture(server);
        f.AddRepair(11, 2); f.AddRepair(12, 3);
        f.AddBuild(2); f.AddBuild(3);
        f.Planet.factoryLoaded = true;
        f.Remote.isAlive = false;
        f.Remote.mecha.Kill();
        f.Remote.mecha.Kill(); // The life presentation can follow an already applied combat death.
        TestAssert.AreEqual(3, f.System.drones.buffer[1].stage);
        TestAssert.AreEqual(2, f.Main.mecha.constructionModule.droneIdleCount);
        TestAssert.AreEqual(3, f.Remote.mecha.constructionModule.droneIdleCount);
        TestAssert.AreEqual(2, f.Views.count, "The other pilot's build and repair views must remain.");
        TestAssert.AreEqual(2, f.System.constructStats.buffer[1].repairerCount,
            "Display-only drones never own or release a native repair reservation.");

        // Both known and previously unseen late repair packets must stay retired after respawn.
        f.Session.Drones.ApplyRepairPose(f.RepairPacket(11, 2));
        f.Session.Drones.ApplyRepairPose(f.RepairPacket(13, 2));
        f.Session.Drones.EjectMechaDroneFromOtherPlayer(2, 101, -1, 0, 0, 0, 0);
        f.Planet.factoryLoaded = false;
        f.Remote.isAlive = true;
        f.Session.Drones.ApplyRepairPose(f.RepairPacket(11, 2));
        f.Session.Drones.ApplyRepairPose(f.RepairPacket(13, 2));
        TestAssert.AreEqual(2, f.Views.count);
        f.Session.Drones.ApplyRepairPose(f.RepairPacket(14, 2));
        TestAssert.AreEqual(3, f.Views.count, "A new flight after respawn must still work.");
    }

    [TestMethod]
    public void LocalDeathUsesNativeRecyclingForWorkingLaunchingAndReturningDrones()
    {
        using var f = new Fixture(false);
        f.AddRepair(11, 3);
        ref var launching = ref f.System.drones.Add();
        launching.owner = 0; launching.craftId = 2; launching.stage = 1; launching.targetObjectId = -1;
        ref var returning = ref f.System.drones.Add();
        returning.owner = 0; returning.craftId = 3; returning.stage = 4;
        ref var baseDrone = ref f.System.drones.Add();
        baseDrone.owner = 7; baseDrone.craftId = 4; baseDrone.stage = 3;
        f.Main.mecha.constructionModule.droneIdleCount = 0;
        f.System.constructStats.buffer[1].repairerCount = 1;
        f.Planet.factoryLoaded = true;
        f.Main.isAlive = false;
        f.Main.mecha.Kill();
        for (var id = 1; id <= 3; id++) TestAssert.AreEqual(0, f.System.drones.buffer[id].stage);
        TestAssert.AreEqual(3, f.Main.mecha.constructionModule.droneIdleCount);
        TestAssert.AreEqual(0, f.System.constructStats.buffer[1].repairerCount);
        TestAssert.AreEqual(3, f.System.drones.buffer[4].stage, "The battle base is independent of player death.");
        TestAssert.AreEqual(1, f.Views.count, "Another player's repair view must remain.");
        TestAssert.HasCount(3, (HashSet<int>)AccessTools.Field(typeof(PlanetFactory), "_rmv_id_list").GetValue(f.Factory));
    }

    [TestMethod]
    public void HostDeathCleanupReleasesOnlyTheDeadPlayersClaimsAndRepairReservations()
    {
        using var f = new Fixture(true);
        var claims = (BuildTargetClaims)AccessTools.Field(typeof(BuildDispatchManager), "claims").GetValue(f.Session.BuildDispatch);
        claims.Assign(101, 1, BuildOwnerKind.Player, 2);
        claims.Assign(101, 2, BuildOwnerKind.Player, 3);
        claims.Assign(101, 3, BuildOwnerKind.Base, 7);
        for (var i = 1; i <= 3; i++)
            f.Factory.prebuildPool[i] = new PrebuildData { id = i, builderLaunched = true };
        f.AddHostRepair(11, 2); f.AddHostRepair(12, 3);
        f.AddRepair(11, 2); f.AddRepair(12, 3);
        f.System.constructStats.buffer[1].repairerCount = 3;
        f.Planet.factoryLoaded = true;
        f.Remote.isAlive = false;
        f.Remote.mecha.Kill();
        TestAssert.IsFalse(f.Session.BuildDispatch.TryGet(101, 1, out _));
        TestAssert.IsTrue(f.Session.BuildDispatch.TryGet(101, 2, out _));
        TestAssert.IsTrue(f.Session.BuildDispatch.TryGet(101, 3, out _));
        TestAssert.IsFalse(f.Factory.prebuildPool[1].builderLaunched);
        TestAssert.AreEqual(1, f.Session.Repairs.ActiveRemoteFlights);
        TestAssert.AreEqual(2, f.System.constructStats.buffer[1].repairerCount);
        TestAssert.AreEqual(1, f.Views.count);
        TestAssert.HasCount(1, f.Network.Packets.OfType<RepairDroneStatePacket>().Where(p => p.Flight == 11 && p.Stage == 0));
        TestAssert.HasCount(1, f.Network.Packets.OfType<BuildTargetAssignmentPacket>().Where(p => p.PrebuildId == 1 && p.Canceled));
    }

    [TestMethod]
    public void LocalDeathCancelsPendingConstructionAndBoundRepairWithoutDoubleRecycling()
    {
        using var f = new Fixture(false);
        var claims = (BuildTargetClaims)AccessTools.Field(typeof(BuildDispatchManager), "claims").GetValue(f.Session.BuildDispatch);
        var claim = claims.Assign(101, 1, BuildOwnerKind.Player, 1);
        var type = typeof(RepairDispatchManager).GetNestedType("Binding", BindingFlags.NonPublic)!;
        var binding = Activator.CreateInstance(type, true)!;
        SetField(binding, "Id", 11L); SetField(binding, "Factory", f.Factory); SetField(binding, "Drone", 1);
        ((IDictionary)AccessTools.Field(typeof(RepairDispatchManager), "bindings").GetValue(f.Session.Repairs)).Add(11L, binding);
        var native = (Dictionary<(PlanetFactory, int), long>)AccessTools.Field(typeof(RepairDispatchManager), "nativeBindings").GetValue(f.Session.Repairs);
        native[(f.Factory, 1)] = 11;
        f.System.constructStats.buffer[1].repairerCount = 1;
        f.Planet.factoryLoaded = true;
        f.Main.isAlive = false;
        f.Main.mecha.Kill();
        TestAssert.IsFalse(f.Session.Repairs.IsReplica(f.Factory, 1));
        TestAssert.AreEqual(3, f.Main.mecha.constructionModule.droneIdleCount);
        TestAssert.AreEqual(0, f.System.constructStats.buffer[1].repairerCount);
        TestAssert.HasCount(1, f.Network.Packets.OfType<RepairControlPacket>().Where(p => p.Flight == 11 && p.Message == RepairControl.Cancel));
        TestAssert.HasCount(1, f.Network.Packets.OfType<BuildTargetAssignmentReplyPacket>().Where(p =>
            p.PrebuildId == 1 && p.Generation == claim.Generation && !p.Accepted));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CombatDeathPreservesGroundEnergyFailureAndSpaceEnergyExemption(bool space)
    {
        using var f = new Fixture(false);
        f.Harmony.CreateClassProcessor(typeof(FleetOwnerEnergyLife_Patch)).Patch();
        var unit = new UnitComponent();
        var desc = new PrefabDesc { craftUnitEnergyPerTick = 10 };
        f.Main.isAlive = false; f.Main.mecha.coreEnergy = 0;
        TestAssert.AreEqual(space, unit.UpdateMechaEnergy(f.Main.mecha, desc, space),
            "Only space craft retain vanilla's exemption when their pilot is dead.");
        f.Main.isAlive = true; f.Main.mecha.coreEnergy = 20;
        TestAssert.IsTrue(unit.UpdateMechaEnergy(f.Main.mecha, desc, space));
        TestAssert.AreEqual(10d, f.Main.mecha.coreEnergy);
        f.Main.mecha.coreEnergy = 0;
        TestAssert.IsFalse(unit.UpdateMechaEnergy(f.Main.mecha, desc, space));
        Multiplayer.Session = null;
        f.Main.isAlive = false;
        TestAssert.IsFalse(unit.UpdateMechaEnergy(f.Main.mecha, desc, space), "Single-player native energy checking stays untouched.");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void CombatEnergyUsesTheUnitsPilotEvenWhenTheCameraPlayersLifeDiffers(bool space, bool pilotAlive)
    {
        using var f = new Fixture(false);
        f.Main.isAlive = !pilotAlive; f.Remote.isAlive = pilotAlive;
        f.Remote.mecha.coreEnergy = 20;
        var pool = f.Factory.craftPool;
        pool[1] = new CraftData { id = 1, owner = -1, fleetId = 1 };
        pool[2] = new CraftData { id = 2, owner = 1 };
        if (space)
        {
            var sector = Uninitialized<SpaceSector>(); f.Game.spaceSector = sector; sector.craftPool = pool;
        }
        var state = new CraftState(false, true, space, 5111, 60, 0,
            (byte)ECraftProto.Item, 0, space ? 100 : 101, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, playerOwner: 2);
        NativeCraftSimulation.Install(space ? f.Game.spaceSector : f.Factory, pool, 2, 2, state);
        f.Session.Combat = new CombatManager
        {
            Players = [new CombatManager.PlayerPosition { id = 2, mecha = f.Remote.mecha }],
            IndexByPlayerId = new() { [2] = 0 }
        };
        f.Session.IsGameLoaded = true;
        var method = AccessTools.Method(typeof(UnitComponent), nameof(UnitComponent.UpdateMechaEnergy));
        f.Harmony.Patch(method, prefix: new HarmonyMethod(typeof(UnitCombatAuthority_Patch), "Prefix"));
        f.Harmony.CreateClassProcessor(typeof(FleetOwnerEnergyLife_Patch)).Patch();
        FleetTickContext.Planet = space ? 0 : 101;
        if (!pilotAlive) f.Remote.mecha.coreEnergy = 0;
        var unit = new UnitComponent { id = 1, craftId = 2 };
        TestAssert.AreEqual(pilotAlive || space,
            unit.UpdateMechaEnergy(f.Main.mecha, new PrefabDesc { craftUnitEnergyPerTick = 10 }, space));
        TestAssert.AreEqual(pilotAlive ? 10d : 0d, f.Remote.mecha.coreEnergy);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly GameData oldGame = GameMain.data;
        private readonly MultiplayerSession oldSession = Multiplayer.Session;
        private readonly int oldPlanet = FleetTickContext.Planet;
        public readonly Harmony Harmony = new("nebulae.tests.drone-death");
        public readonly GameData Game = Uninitialized<GameData>();
        public readonly PlanetFactory Factory = Uninitialized<PlanetFactory>();
        public readonly Player Main, Remote;
        public readonly PlanetData Planet;
        public readonly MultiplayerSession Session = Uninitialized<MultiplayerSession>();
        public readonly RecordingNetwork Network = new();
        public ConstructionSystem System => Factory.constructionSystem;
        public DataPool<DroneComponent> Views => (DataPool<DroneComponent>)AccessTools.Field(typeof(DroneManager), "drones").GetValue(Session.Drones);

        public Fixture(bool server)
        {
            GameMain.data = Game; Multiplayer.Session = Session;
            Session.LocalPlayer = new LocalPlayer { Data = new PlayerData(1, 101) };
            Session.Network = Network; Session.World = new SimulatedWorld();
            Session.BuildDispatch = new BuildDispatchManager(); Session.Repairs = new RepairDispatchManager();
            Session.Drones = new DroneManager(); Session.CombatAuthority = new CombatAuthorityManager();
            if (server)
            {
                Session.Server = Uninitialized<global::NebulaeNetwork.Server>();
                SetBackingField(Session.Server, "Players", new ConcurrentPlayerCollection());
            }
            else Session.Client = Uninitialized<global::NebulaeNetwork.Client>();
            Game.history = new GameHistoryData { constructionDroneSpeed = 10, constructionDroneMovement = 4 };
            Planet = new PlanetData { id = 101, factory = Factory };
            var star = new StarData { id = 1, planets = [Planet], planetCount = 1 }; Planet.star = star;
            Game.galaxy = new GalaxyData { stars = [star], starCount = 1 };
            SetBackingField(Factory, "planet", Planet); SetBackingField(Factory, "gameData", Game);
            Main = CreatePlayer(); Remote = CreatePlayer();
            SetBackingField(Game, "mainPlayer", Main);
            AddModel(2, Remote); AddModel(3, CreatePlayer());
            Factory.prebuildPool = new PrebuildData[5]; Factory.prebuildCursor = 5;
            Factory.entityPool = new EntityData[2]; Factory.entityCursor = 2;
            Factory.entityPool[1] = new EntityData { id = 1, constructStatId = 1 };
            Factory.craftPool = new CraftData[8]; Factory.craftAnimPool = new AnimData[8];
            Factory.constructionSystem = new ConstructionSystem(Planet);
            System.constructStats.Add() = new ConstructStat { id = 1, entityId = 1, repairerCount = 2 };
            ref var drone = ref System.drones.Add();
            drone.owner = 0; drone.stage = 3; drone.targetObjectId = 1; drone.craftId = 1;
            Main.mecha.constructionModule.droneIdleCount = 2;
            Harmony.Patch(AccessTools.PropertyGetter(typeof(Player), nameof(Player.position)),
                prefix: new HarmonyMethod(typeof(DroneDeathTest), nameof(Position)));
            Harmony.Patch(AccessTools.Method(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.RecycleAllDrones)),
                prefix: new HarmonyMethod(typeof(ConstructionModuleComponent_Patch), nameof(ConstructionModuleComponent_Patch.RecycleAllDrones_Prefix)));
            Harmony.Patch(AccessTools.Method(typeof(Mecha), nameof(Mecha.Kill)),
                postfix: new HarmonyMethod(typeof(Mecha_Patch), nameof(Mecha_Patch.Kill_Postfix)));
            Harmony.Patch(AccessTools.Method(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.RecycleDrone)),
                postfix: new HarmonyMethod(typeof(ConstructionModuleComponent_Patch), nameof(ConstructionModuleComponent_Patch.Recycled)));
        }

        private Player CreatePlayer()
        {
            var player = Uninitialized<Player>(); player.isAlive = true;
            SetBackingField(player, "planetData", Planet);
            SetBackingField(player, "mecha", new Mecha
            {
                player = player,
                energyChanges = new double[32],
                constructionModule = new ConstructionModuleComponent { droneCount = 3, droneIdleCount = 3 }
            });
            return player;
        }
        private void AddModel(ushort id, Player player)
        {
            var model = Uninitialized<RemotePlayerModel>(); model.PlayerInstance = player; model.MechaInstance = player.mecha;
            SetBackingField(model, "PlayerId", id);
            using (Session.World.GetRemotePlayersModels(out var models)) models[id] = model;
        }
        public RepairDroneStatePacket RepairPacket(long flight, ushort player)
        {
            var state = DroneFlightViewTest.State(3); state.Position.x = 40;
            return new RepairDroneStatePacket { Flight = flight, PlanetId = 101, PlayerId = player, Stage = 3, Sequence = 1, FlightState = state.Encode() };
        }
        public void AddRepair(long flight, ushort player) => Session.Drones.ApplyRepairPose(RepairPacket(flight, player));
        public void AddBuild(ushort player)
        {
            var crafts = (List<CraftData>)AccessTools.Field(typeof(DroneManager), "crafts").GetValue(Session.Drones);
            var craft = crafts.Count; crafts.Add(new CraftData { owner = player });
            ref var drone = ref Views.Add(); drone.owner = 101; drone.stage = 2; drone.craftId = craft; drone.targetObjectId = -1;
        }
        public void AddHostRepair(long token, ushort player)
        {
            var type = typeof(RepairDispatchManager).GetNestedType("Flight", BindingFlags.NonPublic)!;
            var flight = Activator.CreateInstance(type, true)!;
            SetField(flight, "Id", token); SetField(flight, "Factory", Factory); SetField(flight, "Player", player);
            SetField(flight, "Entity", 1); SetField(flight, "Started", true);
            var state = DroneFlightViewTest.State(3);
            SetField(flight, "Drone", new DroneComponent { stage = 3, targetObjectId = 1 });
            SetField(flight, "Craft", new CraftData { pos = state.Position, rot = state.Rotation });
            ((IDictionary)AccessTools.Field(typeof(RepairDispatchManager), "flights").GetValue(Session.Repairs)).Add(token, flight);
        }
        public void Dispose()
        {
            Harmony.UnpatchSelf(); FleetTickContext.Planet = oldPlanet;
            GameMain.data = oldGame; Multiplayer.Session = oldSession;
        }
    }

    private static bool Position(ref Vector3 __result) { __result = new Vector3(0, 200, 0); return false; }

    private sealed class RecordingNetwork : INetworkProvider
    {
        public List<object> Packets { get; } = new();
        public INetPacketProcessor PacketProcessor => throw new NotSupportedException();
        public byte[] WriteOnce<T>(T packet) where T : class, new() => throw new NotSupportedException();
        public void SendPacket<T>(T packet) where T : class, new() => Packets.Add(packet);
        public void SendPacketToStar<T>(T packet, int starId) where T : class, new() => Packets.Add(packet);
        public void SendToMatching<T>(T packet, Predicate<INebulaePlayer> condition) where T : class, new() => throw new NotSupportedException();
        public void SendPacketToLocalStar<T>(T packet) where T : class, new() => throw new NotSupportedException();
        public void SendPacketToLocalPlanet<T>(T packet) where T : class, new() => throw new NotSupportedException();
        public void SendPacketToPlanet<T>(T packet, int planetId) where T : class, new() => throw new NotSupportedException();
        public void SendPacketExclude<T>(T packet, INebulaeConnection exclude) where T : class, new() => throw new NotSupportedException();
        public void SendPacketToStarExclude<T>(T packet, int starId, INebulaeConnection exclude) where T : class, new() => throw new NotSupportedException();
        public void Dispose() { }
    }
}
