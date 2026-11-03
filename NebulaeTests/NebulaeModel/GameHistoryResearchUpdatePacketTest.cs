using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.GameHistory;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class GameHistoryResearchUpdatePacketTest
{
    [TestMethod]
    public void ExistingConstructorDefaultsAutomaticResearchStateToFalse()
    {
        var packet = new GameHistoryResearchUpdatePacket(1001, 12, 34, 56, 7);

        TestAssert.IsFalse(packet.HasActiveAutomaticResearch);
    }

    [TestMethod]
    public void AutomaticResearchStateRoundTrips()
    {
        var processor = new NebulaeNetPacketProcessor();
        GameHistoryResearchUpdatePacket? received = null;
        processor.SubscribeReusable<GameHistoryResearchUpdatePacket>(packet => received = packet);
        var writer = new NetDataWriter();
        var packet = new GameHistoryResearchUpdatePacket(1001, 12, 34, 56, 7, true);

        processor.Write(writer, packet);
        processor.ReadPacket(new NetDataReader(writer));

        TestAssert.IsNotNull(received);
        TestAssert.AreEqual(packet.TechId, received.TechId);
        TestAssert.AreEqual(packet.HashUploaded, received.HashUploaded);
        TestAssert.AreEqual(packet.HashNeeded, received.HashNeeded);
        TestAssert.AreEqual(packet.TechHashedFor10Frames, received.TechHashedFor10Frames);
        TestAssert.AreEqual(packet.TechQueueLength, received.TechQueueLength);
        TestAssert.IsTrue(received.HasActiveAutomaticResearch);
    }
}
