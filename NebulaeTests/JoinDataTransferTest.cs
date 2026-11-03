using System.Globalization;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Planet;
using NebulaeModel.Packets.Session;
using NebulaeModel.Utils;
using NebulaeWorld.GameStates;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class JoinDataTransferTest
{
    [TestMethod]
    [DataRow(false, 12)]
    [DataRow(false, 220000)]
    [DataRow(true, 220000)]
    public void FactoryTransferCountsPreparedBytesAndRestoresTheOriginalSnapshot(bool compressible, int size)
    {
        var data = new byte[size];
        if (!compressible) new Random(17).NextBytes(data);
        var codec = new NebulaeNetPacketProcessor();
        var original = codec.Write(new FactoryData(101, data, new byte[12])
        { EnemyGenerations = new byte[9], BuildAssignments = new byte[21] });
        var prepared = PacketCompression.WrapForSend(original);
        if (compressible) TestAssert.AreEqual(PacketCompression.FlagDeflate, prepared[PacketCompression.FlagOffset]);
        if (size < 128 * 1024) TestAssert.AreEqual(PacketCompression.FlagRaw, prepared[PacketCompression.FlagOffset]);
        var receiver = new JoinDataTransfer();
        byte[]? restored = null;
        long previous = 0;
        var chunks = 0;
        codec.SubscribeReusable<JoinDataTransferBegin>(begin =>
        {
            receiver.Begin(begin, 17);
            TestAssert.AreEqual((long)prepared.Length, receiver.TotalBytes);
            TestAssert.AreEqual(0L, receiver.ReceivedBytes);
        });
        codec.SubscribeReusable<JoinDataTransferChunk>(chunk =>
        {
            TestAssert.IsLessThanOrEqualTo(JoinDataTransfer.ChunkBytes, chunk.Data.Length);
            var complete = receiver.Receive(chunk);
            TestAssert.IsGreaterThan(previous, receiver.ReceivedBytes);
            TestAssert.IsLessThanOrEqualTo(receiver.TotalBytes, receiver.ReceivedBytes);
            previous = receiver.ReceivedBytes;
            if (complete != null) restored = receiver.Decode(complete);
            chunks++;
        });
        foreach (var framed in JoinDataTransfer.Encode(17, 1, JoinDataStage.Factory, 101, new[] { prepared }))
        {
            // The outer chunk envelope must never recompress an already prepared message.
            TestAssert.AreEqual(PacketCompression.FlagRaw, framed[PacketCompression.FlagOffset]);
            codec.ReadPacket(new NetDataReader(PacketCompression.UnwrapForReceive(framed)));
        }
        CollectionAssert.AreEqual(original, restored);
        TestAssert.AreEqual(receiver.TotalBytes, receiver.ReceivedBytes);
        TestAssert.IsFalse(receiver.Active);
        TestAssert.AreEqual((prepared.Length + JoinDataTransfer.ChunkBytes - 1) / JoinDataTransfer.ChunkBytes, chunks);
    }

    [TestMethod]
    public void GroupPreparationRunsOnTheWorkerAndReadyCannotOvertakeEarlierChunks()
    {
        var codec = new NebulaeNetPacketProcessor();
        var random = new byte[180000]; new Random(8).NextBytes(random);
        var originals = new[]
        {
            codec.Write(new GlobalGameDataResponse(GlobalGameDataResponse.EDataType.History, random)),
            codec.Write(new GlobalGameDataResponse(GlobalGameDataResponse.EDataType.Ready, new byte[] { 1 }))
        };
        var expected = originals.ToArray();
        var scheduled = new Queue<Action>();
        var sent = new List<byte[]>();
        var preparedCount = 0;
        var queue = new OrderedSendQueue(bytes => { preparedCount++; return PacketCompression.WrapForSend(bytes); },
            (bytes, done) => { sent.Add(bytes); done(true); }, scheduled.Enqueue);
        var before = codec.Write(new GlobalGameDataRequest());
        var after = codec.Write(new ServerStatusRequest());
        queue.Enqueue(before);
        queue.EnqueueBatch(originals, prepared => JoinDataTransfer.Encode(17, 1, JoinDataStage.World, 0, prepared));
        queue.Enqueue(after);
        TestAssert.AreEqual(0, preparedCount);
        while (scheduled.Count > 0) scheduled.Dequeue()();
        TestAssert.AreEqual(4, preparedCount, "Each original is prepared once; wrappers are already framed.");
        TestAssert.IsTrue(queue.Flush(1));
        CollectionAssert.AreEqual(before, PacketCompression.UnwrapForReceive(sent.First()));
        CollectionAssert.AreEqual(after, PacketCompression.UnwrapForReceive(sent.Last()));

        var receiver = new JoinDataTransfer();
        var restored = new List<byte[]>();
        codec.SubscribeReusable<JoinDataTransferBegin>(begin => receiver.Begin(begin, 17));
        codec.SubscribeReusable<JoinDataTransferChunk>(chunk =>
        {
            var complete = receiver.Receive(chunk);
            if (complete != null) restored.Add(receiver.Decode(complete));
        });
        foreach (var framed in sent.Skip(1).Take(sent.Count - 2))
            codec.ReadPacket(new NetDataReader(PacketCompression.UnwrapForReceive(framed)));
        TestAssert.HasCount(2, restored);
        CollectionAssert.AreEqual(expected[0], restored[0]);
        CollectionAssert.AreEqual(expected[1], restored[1]);
        TestAssert.AreEqual(receiver.TotalBytes, receiver.ReceivedBytes);
    }

    [TestMethod]
    public void InvalidDescriptionsAndChunksCannotAdvanceProgress()
    {
        var receiver = new JoinDataTransfer();
        var begin = Description();
        TestAssert.Throws<InvalidDataException>(() => receiver.Begin(begin, 18));
        begin.MessageLengths = new[] { int.MaxValue };
        TestAssert.Throws<InvalidDataException>(() => receiver.Begin(begin, 17));
        begin.MessageLengths = new int[JoinDataTransfer.MaxMessages + 1];
        TestAssert.Throws<InvalidDataException>(() => receiver.Begin(begin, 17));
        receiver.Begin(Description(), 17);
        TestAssert.Throws<InvalidDataException>(() => receiver.Begin(Description(), 17));
        foreach (var chunk in new[]
        {
            Chunk(generation: 18), Chunk(id: 2), Chunk(index: 1), Chunk(offset: -1), Chunk(offset: int.MaxValue),
            Chunk(data: Array.Empty<byte>()), Chunk(data: new byte[13]), Chunk(data: new byte[JoinDataTransfer.ChunkBytes + 1])
        }) TestAssert.Throws<InvalidDataException>(() => receiver.Receive(chunk));
        TestAssert.AreEqual(0L, receiver.ReceivedBytes);
        receiver.Receive(Chunk());
        TestAssert.Throws<InvalidDataException>(() => receiver.Receive(Chunk()));
        TestAssert.AreEqual(12L, receiver.ReceivedBytes);
    }

    [TestMethod]
    public void ClearingAnIncompleteTransferAllowsANewGenerationAndRejectsOldChunks()
    {
        var receiver = new JoinDataTransfer();
        receiver.Begin(Description(), 17);
        receiver.Receive(Chunk(data: new byte[6]));
        receiver.Clear();
        TestAssert.IsFalse(receiver.Active);
        TestAssert.AreEqual(0L, receiver.TotalBytes);
        TestAssert.AreEqual(0L, receiver.ReceivedBytes);
        var next = Description(); next.Generation = 18;
        receiver.Begin(next, 18);
        TestAssert.Throws<InvalidDataException>(() => receiver.Receive(Chunk(offset: 6, data: new byte[6])));
        TestAssert.AreEqual(0L, receiver.ReceivedBytes);
        receiver.Receive(Chunk(generation: 18));
        TestAssert.AreEqual(receiver.TotalBytes, receiver.ReceivedBytes);
        TestAssert.Throws<InvalidDataException>(() => receiver.Begin(next, 18));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StoppingOrFailingABatchDropsItsRemainingChunksAndLaterPackets(bool failure)
    {
        var scheduled = new Queue<Action>();
        var sends = 0;
        var errors = 0;
        OrderedSendQueue? queue = null;
        queue = new OrderedSendQueue(bytes => bytes, (_, done) =>
        {
            sends++;
            if (failure) done(false);
            else { queue!.Stop(); done(true); }
        }, scheduled.Enqueue, _ => errors++);
        queue.EnqueueBatch(new[] { new byte[] { 1 } }, _ => new[] { new byte[] { 2 }, new byte[] { 3 } });
        queue.Enqueue(new byte[] { 4 });
        while (scheduled.Count > 0) scheduled.Dequeue()();
        TestAssert.AreEqual(1, sends);
        TestAssert.AreEqual(failure ? 1 : 0, errors);
        TestAssert.IsFalse(queue.Flush(1));
    }

    [TestMethod]
    public void DuplicateOrOversizedPartialChunksLeaveTheCompletedPrefixUnchanged()
    {
        var receiver = new JoinDataTransfer();
        receiver.Begin(Description(), 17);
        receiver.Receive(Chunk(data: new byte[6]));
        TestAssert.Throws<InvalidDataException>(() => receiver.Receive(Chunk(data: new byte[6])));
        TestAssert.Throws<InvalidDataException>(() => receiver.Receive(Chunk(offset: 6, data: new byte[7])));
        TestAssert.AreEqual(6L, receiver.ReceivedBytes);
        TestAssert.IsNotNull(receiver.Receive(Chunk(offset: 6, data: new byte[6])));
        TestAssert.AreEqual(receiver.TotalBytes, receiver.ReceivedBytes);
        var next = Description(); next.TransferId = 2;
        receiver.Begin(next, 17);
        TestAssert.AreEqual(0L, receiver.ReceivedBytes);
        TestAssert.AreEqual(12L, receiver.TotalBytes);
    }

    [TestMethod]
    public void TransferDecoderRejectsNestedEnvelopesAndTheWrongFactoryPlanet()
    {
        var receiver = new JoinDataTransfer();
        receiver.Begin(Description(), 17);
        var codec = new NebulaeNetPacketProcessor();
        var nested = PacketCompression.WrapForSend(codec.Write(Description()));
        TestAssert.Throws<InvalidDataException>(() => receiver.Decode(nested));
        var wrongPlanet = PacketCompression.WrapForSend(codec.Write(new FactoryData(201, new byte[1], new byte[1])));
        TestAssert.Throws<InvalidDataException>(() => receiver.Decode(wrongPlanet));
    }

    [TestMethod]
    public void LoadingCopyUsesStageTotalsAndCompletedStepsWithoutEllipses()
    {
        string Chinese(string key) => NebulaeLocalization.TryTranslate(key, true, out var text) ? text : key;
        TestAssert.AreEqual("正在接收星区数据 2.4/8.0 MB",
            new JoinLoadingProgress(JoinLoadingStage.ReceivingWorld, 2400000, 8000000).Format(Chinese));
        TestAssert.AreEqual("正在接收工厂数据 12.3/48.0 MB",
            new JoinLoadingProgress(JoinLoadingStage.ReceivingFactory, 12300000, 48000000).Format(Chinese));
        TestAssert.AreEqual("正在加载工厂模型 3/8",
            new JoinLoadingProgress(JoinLoadingStage.LoadingModels, completedSteps: 3).Format(Chinese));
        foreach (JoinLoadingStage stage in Enum.GetValues(typeof(JoinLoadingStage)))
        {
            var progress = new JoinLoadingProgress(stage);
            foreach (var text in new[] { progress.Format(Chinese), progress.Format(key => key) })
            {
                TestAssert.DoesNotContain("...", text);
                TestAssert.DoesNotContain("…", text);
                TestAssert.DoesNotContain("NaN", text);
                TestAssert.DoesNotContain("MB", text, "Unknown totals must not show fabricated amounts.");
            }
        }
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            TestAssert.AreEqual("Receiving factory data 12.3/48.0 MB",
                new JoinLoadingProgress(JoinLoadingStage.ReceivingFactory, 12300000, 48000000).Format(key => key));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static JoinDataTransferBegin Description() => new()
    { Generation = 17, TransferId = 1, Stage = (byte)JoinDataStage.Factory, PlanetId = 101, MessageLengths = new[] { 12 } };

    private static JoinDataTransferChunk Chunk(long generation = 17, long id = 1, int index = 0, int offset = 0, byte[]? data = null) => new()
    { Generation = generation, TransferId = id, MessageIndex = index, Offset = offset, Data = data ?? new byte[12] };
}
