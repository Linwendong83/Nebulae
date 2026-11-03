using global::NebulaeModel.DataStructures;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Factory.PowerTower;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class PowerTowerChargingStateTest
{
    [TestMethod]
    public void SnapshotsRoundTripIncludingEmptyState()
    {
        foreach (var snapshot in new[]
        {
            new PowerTowerChargerUpdate(12, 101, [3, 7]),
            new PowerTowerChargerUpdate(12, -1, [])
        })
        {
            var processor = new NebulaeNetPacketProcessor();
            PowerTowerChargerUpdate? received = null;
            processor.SubscribeReusable<PowerTowerChargerUpdate>(packet => received = packet);
            var writer = new NetDataWriter();
            processor.Write(writer, snapshot);
            processor.ReadPacket(new NetDataReader(writer));
            TestAssert.IsNotNull(received);
            TestAssert.AreEqual(snapshot.PlayerId, received.PlayerId);
            TestAssert.AreEqual(snapshot.PlanetId, received.PlanetId);
            CollectionAssert.AreEqual(snapshot.NodeIds, received.NodeIds);
        }
    }

    [TestMethod]
    public void DuplicateAndReorderedSnapshotsAreIdempotent()
    {
        var state = new PowerTowerChargingState();
        TestAssert.IsTrue(state.Replace(1, 101, [7, 3, 3, 0, -1]));
        TestAssert.IsFalse(state.Replace(1, 101, [3, 7]));
        TestAssert.AreEqual(1, state.GetChargerCount(101, 3));
        TestAssert.AreEqual(1, state.GetChargerCount(101, 7));
        TestAssert.AreEqual(0, state.GetChargerCount(101, 0));
    }

    [TestMethod]
    public void OnePlayerLeavingSharedTowerDoesNotStopOtherPlayers()
    {
        var state = new PowerTowerChargingState();
        state.Replace(1, 101, [3]);
        state.Replace(2, 101, [3]);
        TestAssert.AreEqual(2, state.GetChargerCount(101, 3));
        state.Replace(1, -1, []); // Leave planet, die, or finish charging.
        TestAssert.AreEqual(1, state.GetChargerCount(101, 3));
        TestAssert.IsTrue(state.IsCharging(2, 101, 3));
        TestAssert.IsFalse(state.IsCharging(1, 101, 3));
        state.RemovePlayer(2); // Disconnect.
        TestAssert.AreEqual(0, state.GetChargerCount(101, 3));
        TestAssert.IsFalse(state.RemovePlayer(2));
    }

    [TestMethod]
    public void PlanetChangeAtomicallyReplacesOldMembership()
    {
        var state = new PowerTowerChargingState();
        state.Replace(1, 101, [3, 4]);
        state.Replace(2, 102, [3]);
        state.Replace(1, 102, [3]);
        TestAssert.AreEqual(0, state.GetChargerCount(101, 3));
        TestAssert.AreEqual(0, state.GetChargerCount(101, 4));
        TestAssert.AreEqual(2, state.GetChargerCount(102, 3));
        TestAssert.IsFalse(state.IsCharging(1, 101, 3));
    }

    [TestMethod]
    public void DemolitionClearsAllUsersOnlyOnTheMatchingPlanet()
    {
        var state = new PowerTowerChargingState();
        state.Replace(1, 101, [3, 4]);
        state.Replace(2, 101, [3]);
        state.Replace(3, 102, [3]);
        var changes = state.RemoveNode(101, 3);
        TestAssert.HasCount(2, changes);
        TestAssert.AreEqual(0, state.GetChargerCount(101, 3));
        TestAssert.AreEqual(1, state.GetChargerCount(101, 4));
        TestAssert.AreEqual(1, state.GetChargerCount(102, 3));
        TestAssert.AreEqual(-1, state.GetPlayerState(2).PlanetId);
        // Reusing the node ID must not recover the demolished tower's users.
        state.Replace(4, 101, [3]);
        TestAssert.AreEqual(1, state.GetChargerCount(101, 3));
        TestAssert.IsEmpty(state.RemoveNode(999, 3));
    }

    [TestMethod]
    public void JoiningClientRetainsOtherPlanetsAndConvergesAfterDisconnect()
    {
        var host = new PowerTowerChargingState();
        var client = new PowerTowerChargingState();
        var newcomer = new PowerTowerChargingState();
        host.Replace(1, 101, [3]);
        host.Replace(2, 102, [3, 4]);
        foreach (var target in new[] { client, newcomer })
            foreach (var snapshot in host.GetSnapshot())
                target.Replace(snapshot.PlayerId, snapshot.PlanetId, snapshot.NodeIds);

        host.RemovePlayer(2);
        var disconnected = host.GetPlayerState(2);
        foreach (var target in new[] { client, newcomer })
            target.Replace(disconnected.PlayerId, disconnected.PlanetId, disconnected.NodeIds);

        foreach (var target in new[] { host, client, newcomer })
        {
            TestAssert.AreEqual(1, target.GetChargerCount(101, 3));
            TestAssert.AreEqual(0, target.GetChargerCount(102, 3));
        }
        newcomer.Replace(2, 102, [4]); // Reconnected player can acquire a fresh membership.
        TestAssert.AreEqual(0, newcomer.GetChargerCount(102, 3));
        TestAssert.AreEqual(1, newcomer.GetChargerCount(102, 4));
    }

    [TestMethod]
    public void SnapshotArraysCannotMutateStoredMembership()
    {
        var state = new PowerTowerChargingState();
        int[] input = [3];
        state.Replace(1, 101, input);
        input[0] = 4;
        state.GetPlayerState(1).NodeIds[0] = 5;
        state.GetSnapshot()[0].NodeIds[0] = 6;
        TestAssert.IsTrue(state.IsCharging(1, 101, 3));
        TestAssert.AreEqual(1, state.GetChargerCount(101, 3));
        state.Clear();
        TestAssert.IsEmpty(state.GetSnapshot());
        TestAssert.AreEqual(0, state.GetChargerCount(101, 3));
    }

    [TestMethod]
    public void SimulationAndNetworkReadersCanRunConcurrently()
    {
        var state = new PowerTowerChargingState();
        Parallel.For(1, 9, player =>
        {
            for (var tick = 0; tick < 100; tick++)
            {
                state.Replace((ushort)player, 101, [3]);
                state.GetSnapshot();
                state.GetChargerCount(101, 3);
                state.RemovePlayer((ushort)player);
            }
            state.Replace((ushort)player, 101, [3]);
        });
        TestAssert.AreEqual(8, state.GetChargerCount(101, 3));
    }
}
