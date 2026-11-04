using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Planet;
using NebulaeModel.Packets.Session;
using NebulaeWorld.Planet;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class LoadingOptimizationTest
{
    [TestMethod]
    [DataRow(80)]
    [DataRow(200)]
    public void RuntimeSnapshotMatchesNativeExportWithoutGeneratingTerrain(int precision)
    {
        var untouched = new PlanetData { id = 101, precision = precision };
        var bytes = PlanetRuntimeSnapshot.Export(untouched);
        TestAssert.IsNull(untouched.data);
        TestAssert.IsNull(untouched.modData);
        var expected = new PlanetData { precision = precision, modData = new byte[(precision + 1) * (precision + 1) * 2] };
        AssertRuntimeEqual(expected, bytes);
        expected.modData[17] = 31;
        AssertRuntimeEqual(expected, PlanetRuntimeSnapshot.Export(expected));
        var restored = new PlanetData();
        using var reader = new BinaryUtils.Reader(PlanetRuntimeSnapshot.Export(expected));
        restored.ImportRuntime(reader.BinaryReader);
        CollectionAssert.AreEqual(expected.modData, restored.modData);
    }

    private static void AssertRuntimeEqual(PlanetData expected, byte[] bytes)
    {
        using var writer = new BinaryUtils.Writer();
        expected.ExportRuntime(writer.BinaryWriter);
        using var native = new BinaryUtils.Reader(writer.CloseAndGetBytes());
        using var current = new BinaryUtils.Reader(bytes);
        using var a = new MemoryStream(); using var b = new MemoryStream();
        native.BinaryReader.BaseStream.CopyTo(a); current.BinaryReader.BaseStream.CopyTo(b);
        CollectionAssert.AreEqual(a.ToArray(), b.ToArray());
    }

    [TestMethod]
    public void CompressionPolicyDistinguishesCompressedAndMixedPayloads()
    {
        var serializer = new NebulaeNetPacketProcessor();
        foreach (GlobalGameDataResponse.EDataType type in Enum.GetValues(typeof(GlobalGameDataResponse.EDataType)))
        {
            var bytes = serializer.Write(new GlobalGameDataResponse(type, new byte[200000]));
            var compressed = type is not (GlobalGameDataResponse.EDataType.Goals or GlobalGameDataResponse.EDataType.KillStatistics);
            TestAssert.AreEqual(compressed, PacketCompression.IsPrecompressed(bytes), type.ToString());
            var framed = PacketCompression.WrapForSend(bytes);
            TestAssert.AreEqual(compressed ? PacketCompression.FlagRaw : PacketCompression.FlagDeflate, framed[PacketCompression.FlagOffset]);
            CollectionAssert.AreEqual(bytes, PacketCompression.UnwrapForReceive(framed));
        }
        TestAssert.IsTrue(PacketCompression.IsPrecompressed(serializer.Write(new PlanetDataResponse(101, new byte[200000]))));
        TestAssert.IsFalse(PacketCompression.IsPrecompressed(serializer.Write(new FactoryData(101, new byte[200000], new byte[200000]))));
    }

    [TestMethod]
    public void SenderKeepsReadyBehindDataAndDoesNotCompressOnProducer()
    {
        var work = new Queue<Action>();
        var sent = new List<byte>();
        Action<bool>? complete = null;
        var prepared = 0;
        var queue = new OrderedSendQueue(bytes => { prepared++; return bytes; },
            (bytes, done) => { sent.Add(bytes[0]); complete = done; }, work.Enqueue);
        queue.Enqueue(new byte[] { 1 }); queue.Enqueue(new byte[] { 2 }); queue.Enqueue(new byte[] { 3 });
        TestAssert.AreEqual(0, prepared);
        TestAssert.HasCount(1, work);
        work.Dequeue()();
        TestAssert.HasCount(1, sent);
        TestAssert.IsFalse(queue.Flush(1));
        complete!(true); while (work.Count > 0) work.Dequeue()();
        complete!(true); while (work.Count > 0) work.Dequeue()();
        complete!(true);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, sent.ToArray());
        TestAssert.IsTrue(queue.Flush(1));
    }

    [TestMethod]
    public void SynchronousSendCompletionCannotWedgeOrReorderTheQueue()
    {
        var work = new Queue<Action>(); var sent = new List<byte>();
        var queue = new OrderedSendQueue(bytes => bytes, (bytes, done) => { sent.Add(bytes[0]); done(true); }, work.Enqueue);
        queue.Enqueue(new byte[] { 1 }); queue.Enqueue(new byte[] { 2 });
        while (work.Count > 0) work.Dequeue()();
        TestAssert.IsTrue(queue.Flush(1));
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, sent.ToArray());
    }

    [TestMethod]
    public void FailedSendAndDisconnectReleaseQueuedBuffers()
    {
        var work = new Queue<Action>(); var sends = 0;
        var queue = new OrderedSendQueue(bytes => bytes, (_, done) => { sends++; done(false); }, work.Enqueue);
        queue.Enqueue(new byte[] { 1 }); queue.Enqueue(new byte[] { 2 });
        work.Dequeue()();
        TestAssert.IsFalse(queue.Flush(1)); TestAssert.AreEqual(1, sends); TestAssert.IsEmpty(work);
        queue.Enqueue(new byte[] { 3 }); TestAssert.IsEmpty(work);
        queue = new OrderedSendQueue(bytes => bytes, (_, done) => sends++, work.Enqueue);
        queue.Enqueue(new byte[] { 4 }); queue.Stop(); work.Dequeue()();
        TestAssert.AreEqual(1, sends);
    }

    [TestMethod]
    public void PacketBudgetPreservesFifoAndPauseWithoutHoldingTheProducerLock()
    {
        var processor = new NebulaeNetPacketProcessor(); var received = new List<int>();
        var producerWasNotBlocked = false;
        processor.SubscribeReusable<QueueProbe>(p =>
        {
            received.Add(p.Value);
            if (p.Value == 1)
            {
                var bytes = processor.Write(new QueueProbe { Value = 3 });
                var producer = Task.Run(() => processor.EnqueuePacketForProcessing(bytes, null));
                producerWasNotBlocked = producer.Wait(1000);
                processor.EnablePacketProcessing = false;
            }
        });
        processor.EnqueuePacketForProcessing(processor.Write(new QueueProbe { Value = 1 }), null);
        processor.EnqueuePacketForProcessing(processor.Write(new QueueProbe { Value = 2 }), null);
        processor.ProcessPacketQueue(); TestAssert.AreEqual(2, processor.PendingCount);
        TestAssert.IsTrue(producerWasNotBlocked, "Handlers must not hold the incoming queue lock.");
        processor.ProcessPacketQueue(); TestAssert.HasCount(1, received);
        processor.EnablePacketProcessing = true;
        long ticks = 0;
        processor.ProcessPacketQueue(new WorkBudget(1, () => ticks++, 1000));
        CollectionAssert.AreEqual(new[] { 1, 2 }, received.ToArray());
        processor.ProcessPacketQueue();
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, received.ToArray());
    }

    [TestMethod]
    public void UnchangedTerrainAvoidsRebuildAndChangedTerrainKeepsBothReferences()
    {
        var planet = new PlanetData { id = 101, type = EPlanetType.Desert, data = new PlanetRawData(80), dirtyFlags = new bool[4] };
        planet.modData = planet.data.InitModData(null);
        var manager = new PlanetManager();
        var equal = (byte[])planet.modData.Clone(); manager.PendingTerrainData[101] = equal;
        manager.ApplyTerrain(planet);
        TestAssert.IsFalse(planet.dirtyFlags.Any(x => x)); TestAssert.AreSame(equal, planet.modData); TestAssert.AreSame(equal, planet.data.modData);
        var changed = (byte[])equal.Clone(); changed[5] = 42;
        manager.PendingTerrainData[101] = changed; manager.ApplyTerrain(planet);
        TestAssert.IsTrue(planet.dirtyFlags.All(x => x)); TestAssert.AreSame(changed, planet.modData); TestAssert.AreSame(changed, planet.data.modData);
        TestAssert.IsEmpty(manager.PendingTerrainData);
    }

    [TestMethod]
    public void ModelStageYieldsResumesAndCancellationResetsItsCursor()
    {
        var manager = new PlanetManager();
        var visited = new List<int>();
        var loader = new FactoryModelLoader(manager, (_, _, id) => visited.Add(id));
        var planet = new PlanetData { id = 102, factory = new PlanetFactory { entityCursor = 5, entityPool = new EntityData[5] } };
        var oldStage = PlanetManager.LoadingStage;
        var oldLimit = PlanetModelingManager.Debug_LoadingPlanetFactoryMain_MaxStage;
        try
        {
            PlanetManager.LoadingStage = 4;
            for (var i = 0; i < 3; i++)
            {
                long ticks = 0;
                loader.Step(planet, new WorkBudget(1, () => ticks++, 1000));
                TestAssert.AreEqual(4, PlanetManager.LoadingStage);
            }
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, visited.ToArray());
            loader.Cancel(102);
            long tick = 0;
            loader.Step(planet, new WorkBudget(1, () => tick++, 1000));
            TestAssert.AreEqual(4, PlanetManager.LoadingStage, "A cancelled cursor must start again.");
            PlanetModelingManager.Debug_LoadingPlanetFactoryMain_MaxStage = 4;
            loader.Step(planet, new WorkBudget(0));
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 1, 2, 3, 4 }, visited.ToArray());
            TestAssert.AreEqual(5, PlanetManager.LoadingStage); TestAssert.AreEqual(4, planet.factingCompletedStage);
        }
        finally { PlanetManager.LoadingStage = oldStage; PlanetModelingManager.Debug_LoadingPlanetFactoryMain_MaxStage = oldLimit; }
    }

    [TestMethod]
    public void ModelStagesShareOneBudgetAndDoNotRequireSeparateUnityFrames()
    {
        var visited = new List<(int Stage, int Id)>();
        var loader = new FactoryModelLoader(new PlanetManager(), (_, stage, id) => visited.Add((stage, id)));
        var planet = new PlanetData
        {
            id = 101,
            factory = new PlanetFactory
            { entityCursor = 3, craftCursor = 2, enemyCursor = 3 }
        };
        var oldStage = PlanetManager.LoadingStage;
        var oldLimit = PlanetModelingManager.Debug_LoadingPlanetFactoryMain_MaxStage;
        try
        {
            PlanetManager.LoadingStage = 4;
            PlanetModelingManager.Debug_LoadingPlanetFactoryMain_MaxStage = 6;
            loader.Step(planet, new WorkBudget(0));
            CollectionAssert.AreEqual(new[] { (4, 1), (4, 2), (5, 1), (6, 1), (6, 2) }, visited.ToArray());
            TestAssert.AreEqual(7, PlanetManager.LoadingStage);
            TestAssert.AreEqual(6, planet.factingCompletedStage);
        }
        finally { PlanetManager.LoadingStage = oldStage; PlanetModelingManager.Debug_LoadingPlanetFactoryMain_MaxStage = oldLimit; }
    }

    [TestMethod]
    public void PrefetchKeepsPreparedTerrainUntilTheFactoryRequestAndCoalescesOtherPlayers()
    {
        var started = 0; var exports = 0;
        var coordinator = new HostPlanetPreparation(_ => started++);
        var planet = new PlanetData { id = 103, wanted = true };
        var first = new TestConnection(); var second = new TestConnection();
        coordinator.Prepare(planet, first); coordinator.Prepare(planet, second);
        planet.loaded = true; planet.data = new PlanetRawData(80);
        coordinator.Update();
        TestAssert.AreEqual(0, exports, "Preparation must not export before a snapshot is requested.");
        coordinator.RequestFactory(planet, first, () => exports++, _ => TestAssert.Fail());
        coordinator.RequestFactory(planet, second, () => exports++, _ => TestAssert.Fail());
        coordinator.Update();
        TestAssert.AreEqual(1, started);
        TestAssert.AreEqual(2, exports);
    }

    [TestMethod]
    public void DisconnectAndClearCancelUnownedTerrainPreparation()
    {
        var coordinator = new HostPlanetPreparation(planet => { planet.wanted = true; planet.loading = true; });
        var planet = new PlanetData { id = 103, star = new StarData() };
        var first = new TestConnection(); var second = new TestConnection();
        coordinator.Prepare(planet, first); coordinator.Prepare(planet, second);
        coordinator.Forget(first);
        TestAssert.IsTrue(planet.wanted);
        coordinator.Forget(second);
        TestAssert.IsFalse(planet.wanted);
        var another = new PlanetData { id = 104, star = planet.star };
        coordinator.Prepare(another, first); coordinator.Clear();
        TestAssert.IsFalse(another.wanted);
    }

    [TestMethod]
    public void ASecondJoinRetainsAPrefetchThatBecameReadyBeforeItsHandshake()
    {
        var coordinator = new HostPlanetPreparation(planet => { planet.wanted = true; planet.loading = true; });
        var star = new StarData();
        var planet = new PlanetData { id = 103, star = star };
        var first = new TestConnection(); var second = new TestConnection();
        coordinator.Prepare(planet, first);
        planet.loaded = true; planet.loading = false; planet.data = new PlanetRawData(80);
        coordinator.Update(); coordinator.Prepare(planet, second); coordinator.Forget(first);
        TestAssert.IsTrue(planet.wanted, "The later join must own a lease even when preparation is already ready.");
        // Avoid Unity cleanup in the test host; the last lease still releases in-flight ownership.
        planet.loading = true; coordinator.Forget(second);
        TestAssert.IsFalse(planet.wanted);
    }

    [TestMethod]
    public void PreparationTimeoutReleasesItsLeaseAndTheNextRequestCanRetry()
    {
        var starts = 0; var failures = 0;
        var coordinator = new HostPlanetPreparation(planet => { starts++; planet.wanted = true; planet.loading = true; });
        var planet = new PlanetData { id = 103, star = new StarData() };
        var connection = new TestConnection();
        coordinator.Prepare(planet, connection);
        coordinator.RequestFactory(planet, connection, () => TestAssert.Fail(), _ => failures++);
        var requests = (System.Collections.IDictionary)global::HarmonyLib.AccessTools.Field(typeof(HostPlanetPreparation), "requests").GetValue(coordinator);
        var request = requests[103]!;
        global::HarmonyLib.AccessTools.Field(request.GetType(), "Started").SetValue(request,
            System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency * 121);
        coordinator.Update();
        TestAssert.AreEqual(1, failures);
        TestAssert.IsFalse(planet.wanted);
        coordinator.RequestFactory(planet, connection, () => { }, _ => TestAssert.Fail());
        TestAssert.AreEqual(2, starts);
        coordinator.Clear();
    }

    [PacketId(0xff0010aaUL)]
    internal class QueueProbe { [WireOrder(0)] public int Value { get; set; } }

    [TestMethod]
    public void HostPreparationCoalescesRequestsAndDropsDisconnectedWaiters()
    {
        var started = 0; var completed = 0;
        var coordinator = new HostPlanetPreparation(_ => started++);
        var planet = new PlanetData { id = 103, wanted = true };
        var first = new TestConnection(); var second = new TestConnection();
        coordinator.RequestFactory(planet, first, () => completed++, _ => TestAssert.Fail());
        coordinator.RequestFactory(planet, first, () => completed++, _ => TestAssert.Fail());
        coordinator.RequestFactory(planet, second, () => completed++, _ => TestAssert.Fail());
        TestAssert.AreEqual(1, started);
        coordinator.Forget(first);
        planet.loaded = true; planet.data = new PlanetRawData(80);
        coordinator.Update(); coordinator.Update();
        TestAssert.AreEqual(1, completed);
    }

    [TestMethod]
    public void PreparationFailureCanRetryAndSessionClearPreventsLateExports()
    {
        var failed = 0; var ready = 0; var attempts = 0;
        var coordinator = new HostPlanetPreparation(_ => { if (++attempts == 1) throw new IOException(); });
        var planet = new PlanetData { id = 103, wanted = true };
        var connection = new TestConnection();
        coordinator.RequestFactory(planet, connection, () => ready++, _ => failed++);
        TestAssert.AreEqual(1, failed);
        coordinator.RequestFactory(planet, connection, () => ready++, _ => failed++);
        TestAssert.AreEqual(2, attempts);
        coordinator.Clear();
        planet.loaded = true; planet.data = new PlanetRawData(80);
        coordinator.Update();
        TestAssert.AreEqual(0, ready);
    }

    private sealed class TestConnection : INebulaeConnection
    {
        public bool IsAlive => true;
        public int Id => 1;
        public EConnectionStatus ConnectionStatus { get; set; }
        public bool Equals(INebulaeConnection? other) => ReferenceEquals(this, other);
        public void SendPacket<T>(T packet) where T : class, new() { }
        public void SendRawPacket(byte[] rawData) { }
    }

    [TestMethod]
    public void APrecompressedRawFrameStillHonorsTheReceiverSizeLimit()
    {
        var length = PacketCompression.MaxDecompressedMessageBytes + 1;
        var bytes = new byte[length + PacketCompression.HeaderBytes];
        BitConverter.GetBytes(PacketCompression.Signature).CopyTo(bytes, 0);
        bytes[PacketCompression.FlagOffset] = PacketCompression.FlagRaw;
        BitConverter.GetBytes(length).CopyTo(bytes, PacketCompression.LengthOffset);
        TestAssert.Throws<InvalidDataException>(() => PacketCompression.UnwrapForReceive(bytes));
    }

    [TestMethod]
    public void CompressionFailureTerminatesTheQueueAndReportsOnce()
    {
        var work = new Queue<Action>(); var failures = 0; var sends = 0;
        var queue = new OrderedSendQueue(_ => throw new IOException("fixture"), (_, _) => sends++, work.Enqueue,
            _ => failures++);
        queue.Enqueue(new byte[] { 1 }); queue.Enqueue(new byte[] { 2 });
        work.Dequeue()(); queue.Enqueue(new byte[] { 3 });
        TestAssert.AreEqual(1, failures); TestAssert.AreEqual(0, sends); TestAssert.IsEmpty(work);
        TestAssert.IsFalse(queue.Flush(1));
    }

    [TestMethod]
    public void TerrainResponseMustBeRequestedWantedAndNotAlreadyReceived()
    {
        var manager = new PlanetManager();
        var planet = new PlanetData { id = 103, wanted = true, loading = true };
        TestAssert.IsFalse(manager.AcceptTerrainResponse(planet));
        manager.BeginTerrainLoad(103);
        TestAssert.IsTrue(manager.AcceptTerrainResponse(planet));
        TestAssert.IsFalse(manager.AcceptTerrainResponse(planet));
        manager.ClearTerrainLoads();
        TestAssert.IsFalse(manager.AcceptTerrainResponse(planet));
        manager.BeginTerrainLoad(103); planet.wanted = false;
        TestAssert.IsFalse(manager.AcceptTerrainResponse(planet));
    }

    [TestMethod]
    public void LeavingAStarDoesNotDiscardAnAlreadyRequestedDestination()
    {
        var manager = new PlanetManager();
        var source = new PlanetData { id = 101, wanted = true, loading = true };
        var destination = new PlanetData { id = 202, wanted = true, loading = true };
        manager.BeginTerrainLoad(101); manager.BeginTerrainLoad(202);
        manager.CancelTerrainLoads(new StarData { planets = new[] { source } });
        TestAssert.IsFalse(manager.AcceptTerrainResponse(source));
        TestAssert.IsTrue(manager.AcceptTerrainResponse(destination));
    }
}
