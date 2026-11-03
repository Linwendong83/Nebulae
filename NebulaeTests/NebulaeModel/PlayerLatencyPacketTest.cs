using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Players;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class PlayerLatencyPacketTest
{
    [TestMethod]
    public void ProbeRoundTripsWithoutSnapshotArrays()
    {
        var processor = new NebulaeNetPacketProcessor();
        PlayerLatencyPacket received = null!;
        processor.SubscribeReusable<PlayerLatencyPacket>(packet => received = packet);
        var sent = new PlayerLatencyPacket { Kind = 0, SentTicks = 123456789, PlayerId = 7 };

        var writer = new NetDataWriter();
        processor.Write(writer, sent);
        processor.ReadPacket(new NetDataReader(writer));

        TestAssert.AreEqual(sent.Kind, received.Kind);
        TestAssert.AreEqual(sent.SentTicks, received.SentTicks);
        TestAssert.AreEqual(sent.PlayerId, received.PlayerId);
    }

    [TestMethod]
    public void SnapshotRoundTripsPlayerIdsAndLatencies()
    {
        var processor = new NebulaeNetPacketProcessor();
        PlayerLatencyPacket received = null!;
        processor.SubscribeReusable<PlayerLatencyPacket>(packet => received = packet);
        var sent = new PlayerLatencyPacket
        {
            Kind = 2,
            PlayerIds = [2, 14, 65535],
            Milliseconds = [42, 181, 340]
        };

        var writer = new NetDataWriter();
        processor.Write(writer, sent);
        processor.ReadPacket(new NetDataReader(writer));

        TestAssert.AreEqual(sent.Kind, received.Kind);
        CollectionAssert.AreEqual(sent.PlayerIds, received.PlayerIds);
        CollectionAssert.AreEqual(sent.Milliseconds, received.Milliseconds);
    }
}
