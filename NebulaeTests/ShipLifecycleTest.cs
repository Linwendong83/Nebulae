using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Logistics;
using NebulaeWorld.Logistics;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class ShipLifecycleTest
{
    private static StationComponent Station() => new()
    {
        gid = 1,
        planetId = 101,
        workShipDatas = new ShipData[2],
        idleShipCount = 2,
        idleShipIndices = 3,
        energy = 1000,
        storage = [new StationStore { itemId = 1210, count = 10, inc = 20 }]
    };

    private static ShipData Ship(int slot, int planet = 102) => new()
    { shipIndex = slot, planetA = 101, planetB = planet, otherGId = 2, itemId = 1001, itemCount = 100, inc = 200, warperCnt = 2, gene = 17, stage = -2, direction = 1 };

    private static void Depart(StationComponent station, ShipData ship)
    {
        station.workShipDatas[station.workShipCount++] = ship;
        station.idleShipCount--;
        station.IdleShipGetToWork(ship.shipIndex);
    }

    private static void Return(StationComponent station, int slot)
    {
        var index = Array.FindIndex(station.workShipDatas, 0, station.workShipCount, ship => ship.shipIndex == slot);
        Array.Copy(station.workShipDatas, index + 1, station.workShipDatas, index, station.workShipCount - index - 1);
        station.workShipDatas[--station.workShipCount] = default;
        station.idleShipCount++;
        station.WorkShipBackToIdle(slot);
    }

    private static ILSShipLifecyclePacket Flush(ShipLifecycle journal, StationComponent station)
    {
        var packets = new List<ILSShipLifecyclePacket>();
        journal.Flush([null!, station], packets.Add);
        TestAssert.HasCount(1, packets);
        var serializer = new NebulaeNetPacketProcessor();
        ILSShipLifecyclePacket result = null!;
        serializer.SubscribeReusable<ILSShipLifecyclePacket>(packet => result = packet);
        serializer.ReadPacket(new NetDataReader(serializer.Write(packets[0])));
        return result;
    }

    [TestMethod]
    public void ReturnAndRedispatchOfFullStationRetainOrderAndServerSettlement()
    {
        var host = Station(); var client = Station();
        Depart(host, Ship(0)); Depart(host, Ship(1));
        Depart(client, Ship(0)); Depart(client, Ship(1));
        client.workShipDatas[1].uPos.x = 1234;
        var journal = new ShipLifecycle(); var replica = new ShipLifecycle();
        replica.AcceptSnapshot(1, journal.SnapshotRevision(host));
        Return(host, 0); journal.Return(host, 0);
        var next = Ship(0, 103);
        Depart(host, next); journal.Depart(host, next);
        host.energy = 731; host.warperCount = 7; host.warpEnableDist = 480000;
        var packet = Flush(journal, host);
        TestAssert.IsTrue(replica.Receive(packet, client));
        TestAssert.AreEqual(2, client.workShipCount);
        TestAssert.AreEqual(1, client.workShipDatas[0].shipIndex);
        TestAssert.AreEqual(1234d, client.workShipDatas[0].uPos.x);
        TestAssert.AreEqual(103, client.workShipDatas[1].planetB);
        TestAssert.AreEqual(17, client.workShipDatas[1].gene);
        TestAssert.AreEqual(731L, client.energy);
        TestAssert.AreEqual(7, client.warperCount);
        TestAssert.AreEqual(480000d, client.warpEnableDist);
        TestAssert.AreEqual(10, client.storage[0].count);
        // Duplicate delivery cannot remove the replacement trip or debit energy again.
        client.energy = 800;
        TestAssert.IsTrue(replica.Receive(packet, client));
        TestAssert.AreEqual(800L, client.energy);
        journal.Flush([null!, host], _ => TestAssert.Fail("Unchanged station emitted traffic."));
    }

    [TestMethod]
    public void MissedBatchRequiresSnapshotAndRejectsOldDeltasAfterRepair()
    {
        var host = Station(); var client = Station();
        var journal = new ShipLifecycle(); var replica = new ShipLifecycle();
        replica.AcceptSnapshot(1, journal.SnapshotRevision(host));
        Depart(host, Ship(0)); journal.Depart(host, Ship(0));
        var missed = Flush(journal, host);
        Return(host, 0); journal.Return(host, 0);
        var next = Flush(journal, host);
        TestAssert.IsFalse(replica.Receive(next, client));
        TestAssert.IsTrue(replica.NeedsRepair(1));
        TestAssert.IsFalse(replica.NeedsRepair(1));
        TestAssert.IsTrue(replica.AcceptSnapshot(1, journal.SnapshotRevision(host)));
        TestAssert.IsTrue(replica.Receive(missed, client));
        TestAssert.IsTrue(replica.Receive(next, client));
        TestAssert.AreEqual(0, client.workShipCount);
        TestAssert.IsFalse(replica.AcceptSnapshot(1, missed.Revision));
    }

    [TestMethod]
    public void InvalidTailCannotPartiallyRemoveAValidShip()
    {
        var host = Station(); var client = Station();
        Depart(host, Ship(0)); Depart(client, Ship(0));
        var journal = new ShipLifecycle(); var replica = new ShipLifecycle();
        replica.AcceptSnapshot(1, journal.SnapshotRevision(host));
        Return(host, 0); journal.Return(host, 0);
        Depart(host, Ship(0, 103)); journal.Depart(host, Ship(0, 103));
        var packet = Flush(journal, host);
        var complete = packet.Changes;
        for (var length = 1; length < complete.Length; length++)
        {
            packet.Changes = complete.Take(length).ToArray();
            TestAssert.IsFalse(replica.Receive(packet, client));
            TestAssert.AreEqual(1, client.workShipCount);
            TestAssert.AreEqual(102, client.workShipDatas[0].planetB);
        }
        packet.Changes = complete;
        TestAssert.IsTrue(replica.Receive(packet, client));
    }

    [TestMethod]
    public void StationSlotReuseStartsNewChainAndNeedsANewBaseline()
    {
        var old = Station(); var journal = new ShipLifecycle(); var replica = new ShipLifecycle();
        replica.AcceptSnapshot(1, journal.SnapshotRevision(old));
        journal.Changed(old);
        var retired = Flush(journal, old);
        var replacement = Station();
        Depart(replacement, Ship(1)); journal.Depart(replacement, Ship(1));
        var fresh = Flush(journal, replacement);
        TestAssert.AreEqual(0L, fresh.PreviousRevision);
        TestAssert.IsGreaterThan(retired.Revision, fresh.Revision);
        TestAssert.IsFalse(replica.Receive(fresh, old));
        TestAssert.IsTrue(replica.AcceptSnapshot(1, fresh.Revision));
        TestAssert.IsTrue(replica.Receive(retired, replacement));
        TestAssert.AreEqual(1, replacement.workShipCount);
    }

    [TestMethod]
    public void SnapshotDuringPendingBatchFencesThatBatchAndKeepsNextContinuous()
    {
        var station = Station(); var journal = new ShipLifecycle(); var replica = new ShipLifecycle();
        journal.Changed(station);
        replica.AcceptSnapshot(1, journal.SnapshotRevision(station));
        var alreadyIncluded = Flush(journal, station);
        TestAssert.IsTrue(replica.Receive(alreadyIncluded, station));
        journal.Changed(station); station.warperCount = 3;
        var next = Flush(journal, station);
        TestAssert.AreEqual(alreadyIncluded.Revision, next.PreviousRevision);
        TestAssert.IsTrue(replica.Receive(next, station));
    }

    [TestMethod]
    public void NativeMotionIsSilentAndReroutingUsesPhysicalSlotAfterArrayShift()
    {
        var host = Station(); var client = Station();
        Depart(host, Ship(0)); Depart(host, Ship(1)); Depart(client, Ship(0)); Depart(client, Ship(1));
        var journal = new ShipLifecycle(); var replica = new ShipLifecycle();
        replica.AcceptSnapshot(1, journal.SnapshotRevision(host));
        journal.ObserveBefore(host, [null!, host]);
        host.workShipDatas[0].uPos.x = 200; host.workShipDatas[0].t = 0.5f;
        journal.ObserveAfter(host, [null!, host]);
        journal.Flush([null!, host], _ => TestAssert.Fail("Native flight emitted a pose mirror."));
        Return(host, 0); journal.Return(host, 0);
        host.workShipDatas[0].otherGId = 0; host.workShipDatas[0].direction = -1;
        journal.ObserveAfter(host, [null!, host]);
        TestAssert.IsTrue(replica.Receive(Flush(journal, host), client));
        TestAssert.AreEqual(1, client.workShipDatas[0].shipIndex);
        TestAssert.AreEqual(0, client.workShipDatas[0].otherGId);
        TestAssert.AreEqual(-1, client.workShipDatas[0].direction);
        var returning = client.workShipDatas[0];
        TestAssert.IsTrue(NebulaePatcher.Patches.Transpilers.StationComponent_Transpiler.CanAdvanceShip([null!, client], ref returning));
    }

    [TestMethod]
    public void AShipWaitsForItsDestinationDockPoseInsteadOfFlyingIntoThePlanet()
    {
        // A destination the client has never loaded is a fake station whose dock pose is still the
        // zero placeholder. Native flight aims at that pose (the vanilla approach adds
        // 25 * shipDockPos.normalized to it), so advancing would fly the ship into the planet's
        // centre. Waiting for the gStationPoolSync that carries the real pose is the only behaviour
        // that keeps the arrival correct, and it is the same treatment a destination that is still
        // null already receives.
        var destination = Station();
        destination.shipDockPos = Vector3.zero;
        var pool = new[] { null!, destination };
        var ship = Ship(0);
        ship.otherGId = 1;

        TestAssert.IsFalse(NebulaePatcher.Patches.Transpilers.StationComponent_Transpiler.CanAdvanceShip(pool, ref ship),
            "A fake destination without its dock pose must hold the ship.");

        destination.shipDockPos = new Vector3(0f, 180f, 0f);
        TestAssert.IsTrue(NebulaePatcher.Patches.Transpilers.StationComponent_Transpiler.CanAdvanceShip(pool, ref ship),
            "Once the real dock pose arrives the ship may fly.");

        // A destination that left the pool entirely is still answered by the null branch, so a
        // deleted destination never becomes a stall.
        var gone = Ship(0);
        gone.otherGId = 5;
        TestAssert.IsFalse(NebulaePatcher.Patches.Transpilers.StationComponent_Transpiler.CanAdvanceShip(pool, ref gone),
            "An out-of-range destination is still refused.");
        var home = Ship(0);
        home.otherGId = 0;
        TestAssert.IsTrue(NebulaePatcher.Patches.Transpilers.StationComponent_Transpiler.CanAdvanceShip(pool, ref home),
            "A ship with no destination is not gated by the dock pose.");
    }
}
