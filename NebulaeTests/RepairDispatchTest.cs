using System.Collections;
using System.Reflection;
using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Factory;
using NebulaeWorld;
using NebulaeWorld.Factory;
using NebulaeWorld.Player;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class RepairDispatchTest
{
    [TestMethod]
    [DataRow(90, 1.7f, 1, 1)]
    [DataRow(78, 1.7f, 1, 2)]
    [DataRow(60, 1.1f, 1, 2)]
    [DataRow(90, -2f, 0, 1)]
    [DataRow(90, -2f, 1, 0)]
    public void NativeHealthBandsControlRepairDemand(int hp, float demand, int existing, int expected) =>
        TestAssert.AreEqual(expected, RepairDemand.Count(hp, 100, demand, existing));

    [TestMethod]
    public void RemoteReservationsAndNativeDronesAreCountedTogetherWithoutIdleReset()
    {
        var previous = Multiplayer.Session;
        try
        {
            var session = Uninitialized<MultiplayerSession>(); session.Server = Uninitialized<global::NebulaeNetwork.Server>();
            var repairs = session.Repairs = new RepairDispatchManager(); Multiplayer.Session = session;
            var factory = Uninitialized<PlanetFactory>();
            factory.constructionSystem = Uninitialized<ConstructionSystem>(); var system = factory.constructionSystem;
            system.factory = factory;
            system.constructStats = new DataPool<ConstructStat>(); system.constructStats.buffer = new ConstructStat[2]; system.constructStats.cursor = 2;
            system.constructStats.buffer[1] = new ConstructStat { id = 1, entityId = 7, repairerCount = 99 };
            system.drones = new DataPool<DroneComponent>(); system.drones.buffer = new DroneComponent[3]; system.drones.cursor = 3;
            system.drones.buffer[1] = new DroneComponent { id = 1, owner = 0, stage = 3, targetObjectId = 7 };
            system.drones.buffer[2] = new DroneComponent { id = 2, owner = 2, stage = 2, targetObjectId = 7 };
            var flightType = typeof(RepairDispatchManager).GetNestedType("Flight", BindingFlags.NonPublic)!;
            var flight = Activator.CreateInstance(flightType, true)!;
            SetField(flight, "Id", 11L); SetField(flight, "Factory", factory); SetField(flight, "Entity", 7);
            var flights = (IDictionary)AccessTools.Field(typeof(RepairDispatchManager), "flights").GetValue(repairs); flights.Add(11L, flight);
            repairs.Recount(factory);
            TestAssert.AreEqual(3, system.constructStats.buffer[1].repairerCount);
            var module = new ConstructionModuleComponent { droneCount = 3, droneIdleCount = 3 };
            TestAssert.IsFalse(ConstructionSystem_Patch.AllDronesIdle(module, system));
            SetField(flight, "Returning", true); repairs.Recount(factory);
            TestAssert.AreEqual(2, system.constructStats.buffer[1].repairerCount);
            TestAssert.IsTrue(ConstructionSystem_Patch.AllDronesIdle(module, system));
        }
        finally { Multiplayer.Session = previous; }
    }
    [TestMethod]
    public void RepairStatesAndCancellationRetainFlightIdentityOnTheWire()
    {
        var state = RoundTrip(new RepairDroneStatePacket { Flight = 23, PlanetId = 104, PlayerId = 2, Stage = 3, EntityId = 7, Generation = 9, Tick = 45 });
        var cancel = RoundTrip(new RepairControlPacket { Message = RepairControl.Cancel, Flight = state.Flight, PlanetId = state.PlanetId });
        TestAssert.AreEqual(23L, cancel.Flight); TestAssert.AreEqual(RepairControl.Cancel, cancel.Message); TestAssert.AreEqual(9L, state.Generation);
    }
    [TestMethod]
    public void NativeRepairIdleTransformationEmitsAnExecutableMethod()
    {
        var harmony = new Harmony("nebulae-tests.repair-dispatch");
        try { harmony.Patch(AccessTools.Method(typeof(ConstructionSystem), "UpdateModules"), transpiler: new HarmonyMethod(typeof(ConstructionSystem_Patch), "UpdateModules_Transpiler")); }
        finally { harmony.UnpatchSelf(); }
    }

    [TestMethod]
    public void TerminalRepairPoseClosesAVisualEvenInTheSameHostTick()
    {
        var previous = GameMain.data;
        try
        {
            var game = Uninitialized<GameData>(); GameMain.data = game;
            game.history = new GameHistoryData { constructionDroneSpeed = 10 };
            var factory = Uninitialized<PlanetFactory>();
            SetBackingField(factory, "gameData", game);
            factory.prebuildPool = new PrebuildData[1]; factory.entityPool = new EntityData[1];
            var planet = new PlanetData { id = 101, factory = factory };
            var star = new StarData { id = 1, planets = [planet], planetCount = 1 }; planet.star = star;
            game.galaxy = new GalaxyData { stars = [star], starCount = 1 };
            SetBackingField(factory, "planet", planet);
            var drones = new DroneManager();
            var state = DroneFlightViewTest.State(1);
            drones.ApplyRepairPose(new RepairDroneStatePacket { Flight = 1, PlanetId = 101, PlayerId = 2, Tick = 9, Stage = 1, Qw = 1, Sequence = 1, FlightState = state.Encode() });
            state.Stage = 0;
            drones.ApplyRepairPose(new RepairDroneStatePacket { Flight = 1, PlanetId = 101, PlayerId = 2, Tick = 9, Stage = 0, Sequence = 2, FlightState = state.Encode() });
            drones.UpdateDrones(factory, null, false, 1f / 60f);
            var pool = (DataPool<DroneComponent>)AccessTools.Field(typeof(DroneManager), "drones").GetValue(drones);
            TestAssert.AreEqual(0, pool.count);
        }
        finally { GameMain.data = previous; }
    }
}
