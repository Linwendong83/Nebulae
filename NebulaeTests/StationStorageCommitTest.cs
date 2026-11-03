using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Logistics;
using NebulaeWorld.Logistics;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class StationStorageCommitTest
{
    private static StationComponent Station(int count = 3, int inc = 6) => new()
    { gid = 1, planetId = 101, storage = [new StationStore { itemId = 1001, count = count, inc = inc }] };

    private static ILSStorageCommitPacket Flush(StationStorageJournal journal, StationComponent station)
    {
        ILSStorageCommitPacket sent = null!;
        journal.Flush([null!, station], packet => { TestAssert.IsNull(sent); sent = packet; });
        TestAssert.IsNotNull(sent);
        var serializer = new NebulaeNetPacketProcessor();
        ILSStorageCommitPacket received = null!;
        serializer.SubscribeReusable<ILSStorageCommitPacket>(packet => received = packet);
        serializer.ReadPacket(new NetDataReader(serializer.Write(sent)));
        return received;
    }

    [TestMethod]
    public void CommitUsesActualNativeTakeAndPreservesLocallyProducedItems()
    {
        var host = Station(); var client = Station(5, 8);
        var journal = new StationStorageJournal(); var replica = new StationStorageJournal();
        replica.AcceptSnapshot(1, journal.SnapshotRevision(host));
        var item = 1001; var count = 99;
        host.TakeItem(ref item, ref count, out var inc);
        TestAssert.AreEqual(3, count);
        journal.Record(host, 0, item, -count, -inc);
        var commit = Flush(journal, host);
        TestAssert.IsTrue(replica.Receive(commit, client));
        TestAssert.AreEqual(2, client.storage[0].count);
        TestAssert.AreEqual(2, client.storage[0].inc);
        TestAssert.IsTrue(replica.Receive(commit, client));
        TestAssert.AreEqual(2, client.storage[0].count);
        journal.Flush([null!, host], _ => TestAssert.Fail("Unchanged inventory emitted traffic."));
    }

    [TestMethod]
    public void MultipleNativeFieldDebitsAreOneCommitAndMalformedRowsAreAtomic()
    {
        var host = Station(10, 20); var client = Station(10, 20);
        var journal = new StationStorageJournal(); var replica = new StationStorageJournal();
        replica.AcceptSnapshot(1, journal.SnapshotRevision(host));
        journal.Record(host, 0, 1001, -1, 0); journal.Record(host, 0, 1001, 0, -2);
        journal.Record(host, 0, 1001, -3, -6);
        var commit = Flush(journal, host);
        TestAssert.HasCount(1, commit.Index);
        TestAssert.AreEqual(-4L, commit.CountDelta[0]); TestAssert.AreEqual(-8L, commit.IncDelta[0]);
        commit.Index = [0, 1]; commit.ItemId = [1001, 1001]; commit.CountDelta = [-4, -1]; commit.IncDelta = [-8, 0];
        TestAssert.IsFalse(replica.Receive(commit, client));
        TestAssert.AreEqual(10, client.storage[0].count); TestAssert.AreEqual(20, client.storage[0].inc);
    }

    [TestMethod]
    public void MissingCommitAndChangedItemRequireBaselineWithoutClampingOrReselection()
    {
        var host = Station(); var client = Station();
        var journal = new StationStorageJournal(); var replica = new StationStorageJournal();
        replica.AcceptSnapshot(1, journal.SnapshotRevision(host));
        journal.Record(host, 0, 1001, 1, 2); var missed = Flush(journal, host);
        journal.Record(host, 0, 1001, -1, -2); var next = Flush(journal, host);
        TestAssert.IsFalse(replica.Receive(next, client));
        TestAssert.IsTrue(replica.NeedsRepair(1)); TestAssert.IsFalse(replica.NeedsRepair(1));
        TestAssert.IsTrue(replica.AcceptSnapshot(1, next.Revision));
        TestAssert.IsTrue(replica.Receive(missed, client));
        journal.Record(host, 0, 1001, -2, -4); var changed = Flush(journal, host);
        client.storage[0].itemId = 1210;
        TestAssert.IsFalse(replica.Receive(changed, client));
        TestAssert.AreEqual(3, client.storage[0].count);
        client.storage[0].itemId = 1001; client.storage[0].count = 1;
        TestAssert.IsFalse(replica.Receive(changed, client));
        TestAssert.AreEqual(1, client.storage[0].count);
    }
}
