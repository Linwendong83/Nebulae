using System.Collections;
using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Factory;
using NebulaeWorld.Factory;
using NebulaeWorld.Player;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class DroneFlightViewTest
{
    internal static DroneFlightState State(int stage = 2, int target = 7) => new()
    {
        Stage = stage,
        Target = target,
        Movement = 4,
        Speed = 10,
        Powered = true,
        Power = 1,
        Position = new Vector3(0, 200, 0),
        EjectPosition = new Vector3(0, 200, 0),
        TargetPosition = new Vector3(20, 200, 0),
        InitialVector = new Vector3(4, 204, 0),
        Rotation = new Quaternion(0, 0, 0, 1)
    };

    [TestMethod]
    public void ContinuousMotionNeverPublishesAPositionCheckpoint()
    {
        var before = State(); var after = before;
        after.Position.x += 100; after.Rotation.w = -1; after.Progress = .7f;
        after.EjectPosition.x += 10; after.AnimationTime = 123;
        TestAssert.IsTrue(before.SameCommand(after));
        before.Stage = after.Stage = 4; after.TargetPosition.x += 100;
        TestAssert.IsTrue(before.SameCommand(after), "Following a moving owner is local flight, not a stream of return-position commands.");
        after.Powered = false;
        TestAssert.IsFalse(before.SameCommand(after));
        after = before; after.Target = 8;
        TestAssert.IsFalse(before.SameCommand(after));
        after = before; after.Speed++;
        TestAssert.IsFalse(before.SameCommand(after));
    }

    [TestMethod]
    public void FlightParametersRoundTripAndRejectMalformedValues()
    {
        var sent = State(); var encoded = sent.Encode();
        TestAssert.HasCount(DroneFlightState.WireSize, encoded);
        TestAssert.IsTrue(DroneFlightState.TryDecode(encoded, out var received));
        TestAssert.AreEqual(sent, received);
        TestAssert.IsFalse(DroneFlightState.TryDecode(new byte[encoded.Length - 1], out _));
        sent.Speed = float.NaN;
        TestAssert.IsFalse(DroneFlightState.TryDecode(sent.Encode(), out _));
        var canceledBeforeStart = DroneFlightState.Capture(default, default, default, 10, false);
        TestAssert.IsTrue(DroneFlightState.TryDecode(canceledBeforeStart.Encode(), out _),
            "A rejected start still needs a valid terminal event to release the client's drone slot.");
        sent = State(); sent.Rotation = default;
        TestAssert.IsFalse(DroneFlightState.TryDecode(sent.Encode(), out _));
    }

    [TestMethod]
    public void TaskAndSupplyEventsPreserveLocalPositionRotationAndTakeoffProgress()
    {
        var state = State(1); var view = new DroneFlightView(state);
        view.Craft.pos.x = 3; view.Drone.progress = .5f;
        state.Stage = 3; state.Position.x = 999; state.Progress = 1;
        state.Rotation = new Quaternion(0, 1, 0, 0);
        view.Apply(state);
        TestAssert.AreEqual(3d, view.Craft.pos.x);
        TestAssert.AreEqual(.5f, view.Drone.progress);
        TestAssert.AreEqual(1, view.Drone.stage, "A phase notification must not teleport a local flight to the working phase.");
        TestAssert.AreEqual(1f, view.Craft.rot.w);
        state.Target = 9; state.TargetPosition.x = 40;
        view.Apply(state);
        TestAssert.AreEqual(2, view.Drone.stage);
        TestAssert.AreEqual(40f, view.Drone.targetPos.x);
        TestAssert.AreEqual(3d, view.Craft.pos.x);
        state.Stage = 0; view.Apply(state);
        TestAssert.AreEqual(4, view.Drone.stage);
        TestAssert.IsFalse(view.Finished, "A terminal host event still allows the local return animation to finish.");
    }

    [TestMethod]
    public void RemoteConstructionWaitsUntilTheActualSiteDisappears()
    {
        var factory = Uninitialized<PlanetFactory>(); factory.prebuildPool = new PrebuildData[3];
        factory.prebuildPool[1] = new PrebuildData { id = 1 };
        var drones = new DroneManager();
        var drone = new DroneComponent { stage = 3, targetObjectId = -1, movement = 4 };
        for (var i = 0; i < 300; i++) drones.UpdateDroneStageAndTarget(factory, ref drone, 0);
        TestAssert.AreEqual(3, drone.stage);
        TestAssert.AreEqual(4, drone.movement);
        factory.prebuildPool[1].id = 0;
        drones.UpdateDroneStageAndTarget(factory, ref drone, 0);
        TestAssert.AreEqual(4, drone.stage);
        TestAssert.AreEqual(3, drone.movement);
    }

    [TestMethod]
    public void ReusedBaseSlotTurnsTowardTheNextFlightWithoutTeleportingHome()
    {
        var returning = new DroneFlightView(State(4, 0)); returning.Craft.pos.x = 8;
        var next = State(1, -9); next.Position.x = 999;
        var view = new DroneFlightView(next, returning);
        TestAssert.AreEqual(8d, view.Craft.pos.x);
        TestAssert.AreEqual(2, view.Drone.stage);
        TestAssert.AreEqual(-9, view.Drone.targetObjectId);
        TestAssert.IsFalse(view.Complete);
        returning.Craft.pos.x = 0;
        view = new DroneFlightView(next, returning);
        TestAssert.AreEqual(0d, view.Craft.pos.x);
        TestAssert.AreEqual(1, view.Drone.stage);
    }

    [TestMethod]
    public void BaseTaskDeltasPreserveFlightAndStaleSnapshotsCannotEraseIt()
    {
        var previous = GameMain.data;
        try
        {
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var factory = Uninitialized<PlanetFactory>();
            var planet = new PlanetData { id = 101, factory = factory };
            var star = new StarData { id = 1, planets = [planet], planetCount = 1 }; planet.star = star;
            game.galaxy = new GalaxyData { stars = [star], starCount = 1 };
            var dispatch = new BuildDispatchManager();
            var state = State();
            dispatch.ReceiveBaseDrones(BasePacket(state, 1));
            var views = (IDictionary)AccessTools.Field(typeof(BuildDispatchManager), "baseViews").GetValue(dispatch);
            var view = views[101]!;
            var flights = (IDictionary)AccessTools.Field(view.GetType(), "Flights").GetValue(view);
            var entry = flights[(7, 0)]!;
            var local = new DroneFlightView(state); local.Craft.pos.x = 5;
            SetField(entry, "View", local);
            state.Stage = 3; state.Position.x = 999;
            dispatch.ReceiveBaseDrones(BasePacket(state, 2));
            TestAssert.AreSame(local, AccessTools.Field(entry.GetType(), "View").GetValue(flights[(7, 0)]));
            TestAssert.AreEqual(5d, local.Craft.pos.x);
            dispatch.ReceiveBaseDrones(new BaseDroneStatePacket { PlanetId = 101, Sequence = 1, Full = true, State = new byte[4] });
            TestAssert.HasCount(1, flights);
            dispatch.ReceiveBaseDrones(new BaseDroneStatePacket { PlanetId = 101, Sequence = 3, Full = true, State = new byte[4] });
            TestAssert.IsEmpty(flights);
        }
        finally { GameMain.data = previous; }
    }

    private static BaseDroneStatePacket BasePacket(DroneFlightState state, long sequence)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(1); writer.Write(7); writer.Write(9L); writer.Write(0); writer.Write(42L); state.Write(writer);
        return RoundTrip(new BaseDroneStatePacket { PlanetId = 101, Sequence = sequence, State = stream.ToArray() });
    }
}
