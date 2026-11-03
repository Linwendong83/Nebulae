using System.Net;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Session;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class ProtocolAdmissionTest
{
    private sealed class RecordingProcessor<T> : PacketProcessor<T> where T : class, new()
    {
        internal int Calls;
        protected override void ProcessPacket(T packet, NebulaeConnection conn) => Calls++;
    }

    [TestMethod]
    public void EachPeerMustPassItsOwnHandshakeBeforeSendingBusinessPackets()
    {
        var processor = new NebulaeNetPacketProcessor();
        var start = new RecordingProcessor<GlobalGameDataRequest>();
        start.Register(processor, true);
        var bytes = processor.Write(new GlobalGameDataRequest());
        var accepted = Connection(processor);
        var pending = Connection(processor);
        try
        {
            accepted.CompleteHandshake();
            processor.DispatchInline(bytes, accepted);
            TestAssert.AreEqual(1, start.Calls);
            TestAssert.Throws<ParseException>(() => processor.DispatchInline(bytes, pending));
            TestAssert.AreEqual(1, start.Calls);
            pending.CompleteHandshake();
            processor.DispatchInline(bytes, pending);
            TestAssert.AreEqual(2, start.Calls);
        }
        finally { accepted.StopSending(); pending.StopSending(); }
    }

    [TestMethod]
    public void OnlyStatusAndLobbyDeclarationsAreAdmittedBeforeTheHandshake()
    {
        var processor = new NebulaeNetPacketProcessor();
        var lobby = new RecordingProcessor<LobbyRequest>();
        var status = new RecordingProcessor<ServerStatusRequest>();
        var start = new RecordingProcessor<StartGameMessage>();
        PacketUtils.RegisterNestedTypes(processor);
        lobby.Register(processor, true);
        status.Register(processor, true);
        start.Register(processor, true);
        var connection = Connection(processor);
        try
        {
            var declaration = processor.Write(new LobbyRequest([1], "player"));
            processor.DispatchInline(declaration, connection);
            processor.DispatchInline(processor.Write(new ServerStatusRequest()), connection);
            TestAssert.AreEqual(1, lobby.Calls);
            TestAssert.AreEqual(1, status.Calls);
            TestAssert.Throws<ParseException>(() => processor.DispatchInline(
                processor.Write(new StartGameMessage(false, null)), connection));
            TestAssert.AreEqual(0, start.Calls);
            connection.CompleteHandshake();
            TestAssert.Throws<ParseException>(() => processor.DispatchInline(declaration, connection));
            TestAssert.AreEqual(1, lobby.Calls);
        }
        finally { connection.StopSending(); }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void RejectedPeerCannotRunLaterQueuedOrInlinePackets(int failure)
    {
        var processor = new NebulaeNetPacketProcessor();
        var handler = new RecordingProcessor<GlobalGameDataRequest>();
        handler.Register(processor, true);
        var rejected = Connection(processor);
        var healthy = Connection(processor);
        healthy.CompleteHandshake();
        var bytes = processor.Write(new GlobalGameDataRequest());
        try
        {
            if (failure == 0) processor.EnqueuePacketForProcessing(bytes, rejected); // No handshake.
            else
            {
                rejected.CompleteHandshake();
                processor.EnqueuePacketForProcessing(failure == 1 ? new byte[8] : bytes.Concat(new byte[] { 0 }).ToArray(), rejected);
            }
            processor.EnqueuePacketForProcessing(bytes, rejected);
            processor.EnqueuePacketForProcessing(bytes, healthy);
            processor.ProcessPacketQueue();
            TestAssert.IsTrue(rejected.IsStopped);
            TestAssert.AreEqual(1, handler.Calls);
            processor.DispatchInline(bytes, rejected);
            processor.EnqueuePacketForProcessing(bytes, rejected);
            TestAssert.AreEqual(0, processor.PendingCount);
            TestAssert.IsNull(rejected.ReceivePacket(bytes));
            TestAssert.AreEqual(1, handler.Calls);
        }
        finally { rejected.StopSending(); healthy.StopSending(); }
    }

    private static NebulaeConnection Connection(NebulaeNetPacketProcessor processor) =>
        new(null, new IPEndPoint(IPAddress.Loopback, 8469), processor);
}
