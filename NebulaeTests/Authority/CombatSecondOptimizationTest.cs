using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Routers;
using NebulaeModel.Packets.GameStates;
using HarmonyLib;
using WebSocketSharp;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class CombatSecondOptimizationTest
{
    private static readonly AuthorityEpoch Epoch = new(31, 47);
    private static readonly ScopeKey Scope = new(PoolKind.Entity, 101);
    private static readonly AuthoritySessionContext Context = new(true, Epoch, new ConnectionEpoch(1), 1, false);
    private static ObjectKey Key(long generation = 1) => ObjectKey.Create(Epoch, Scope.Kind, Scope.Scope, 7, generation);
    private static byte[] State(int hp)
    {
        TestAssert.IsTrue(FactoryCombatStateCodec.TryEncode(new FactoryCombatState(true, hp, 1000, 2, 0, true, .5f, 1, 7, .25f), out var bytes));
        return bytes;
    }
    private sealed class World : IHostWorldView
    {
        internal readonly Dictionary<ObjectKey, byte[]> Values = new() { [Key()] = State(900) };
        private readonly int owner = Thread.CurrentThread.ManagedThreadId;
        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
        {
            TestAssert.AreEqual(owner, Thread.CurrentThread.ManagedThreadId);
            members.AddRange(Values.Keys); return true;
        }
        public bool TryReadState(ObjectKey key, out byte[] state)
        {
            TestAssert.AreEqual(owner, Thread.CurrentThread.ManagedThreadId);
            return Values.TryGetValue(key, out state!);
        }
    }
    private sealed class Sink : IDeferredReplicationSink
    {
        internal readonly Queue<Func<AuthorityEnvelopePacket>> Queue = new();
        internal readonly List<AuthorityEnvelopePacket> Sent = new();
        internal bool Suspended;
        internal int DeferredBuilds;
        public void Send(ushort subscriber, AuthorityEnvelopePacket packet) { Queue.Enqueue(() => packet); if (!Suspended) Drain(); }
        public void SendWorldStates(ushort subscriber, Func<AuthorityWorldStatePacket> factory)
        {
            Queue.Enqueue(() => { DeferredBuilds++; return factory(); });
            if (!Suspended) Drain();
        }
        internal void Drain() { while (Queue.Count > 0) Sent.Add(Queue.Dequeue()()); }
    }

    [TestMethod]
    public void DeferredStatesFreezeRevisionAndBaselineBeforeLaterFramesAndPoolReuse()
    {
        var world = new World(); var sink = new Sink(); var host = new HostWorldReplicator(Epoch, world, sink);
        var client = new ClientWorldReplica(Epoch); client.Subscribe(Scope);
        host.Subscribe(1, Scope);
        var begin = sink.Sent.OfType<AuthoritySnapshotBeginPacket>().Single();
        TestAssert.IsTrue(client.BeginSnapshot(Scope, begin.SubscriptionEpoch, begin.BaselineId, begin.ChunkCount, begin.TotalBytes, begin.SnapshotHash));
        foreach (var chunk in sink.Sent.OfType<AuthoritySnapshotChunkPacket>())
            TestAssert.IsTrue(client.ReceiveSnapshotChunk(Scope, chunk.SubscriptionEpoch, chunk.BaselineId, chunk.ChunkIndex, chunk.Data));
        var commit = sink.Sent.OfType<AuthoritySnapshotCommitPacket>().Single();
        TestAssert.IsTrue(client.CommitSnapshot(Scope, commit.SubscriptionEpoch, commit.BaselineId, commit.ChunkCount, commit.SnapshotHash));
        host.OnSnapshotAck(1, begin.BaselineId, 0, true);
        sink.Suspended = true; var position = sink.Sent.Count;
        // The empty resume marker already sent by ack is accepted before deferred increments.
        foreach (var resume in sink.Sent.OfType<AuthorityWorldStatePacket>())
            TestAssert.IsTrue(client.ApplyWorldState(Scope, resume.SubscriptionEpoch, resume.Sequence, resume.DeclaredBaselineId, Array.Empty<AuthorityWorldStateRecord>()));
        var builds = sink.DeferredBuilds;
        world.Values[Key()] = State(800); host.CaptureFrame(1);
        world.Values[Key()] = State(700); host.CaptureFrame(2);
        world.Values.Clear(); host.CaptureFrame(3);
        world.Values[Key(2)] = State(600); host.CaptureFrame(4);
        TestAssert.AreEqual(builds, sink.DeferredBuilds, "Capture must not execute packet factories.");
        Task.Run(sink.Drain).GetAwaiter().GetResult();
        var sequence = 0L;
        foreach (var packet in sink.Sent.Skip(position))
        {
            TestAssert.IsGreaterThan(sequence, packet.Sequence); sequence = packet.Sequence;
            if (packet is AuthorityWorldStatePacket states)
            {
                TestAssert.IsTrue(AuthorityWorldStateCodec.TryDecode(states.Data, 0, states.Data.Length, states.RecordCount, Context, out var records, out _));
                TestAssert.IsTrue(client.ApplyWorldState(Scope, states.SubscriptionEpoch, states.Sequence, states.DeclaredBaselineId, records));
            }
            else if (packet is AuthorityLifecyclePacket lifecycle)
            {
                TestAssert.IsTrue(AuthorityLifecycleCodec.TryDecode(lifecycle.Data, 0, lifecycle.Data.Length, lifecycle.RecordCount, Context, out var records, out _));
                TestAssert.IsTrue(client.ApplyLifecycle(Scope, lifecycle.SubscriptionEpoch, lifecycle.Sequence, records));
            }
        }
        TestAssert.IsTrue(client.Mirrors.TryGetValue(Key(2), out var mirror));
        CollectionAssert.AreEqual(State(600), mirror.CanonicalState);
        TestAssert.IsFalse(client.Mirrors.ContainsKey(Key()));
        TestAssert.IsEmpty(client.ScopesNeedingResync);
    }

    [TestMethod]
    public void SharedDeltaEncodingRemainsExactUnderConcurrentSubscribers()
    {
        var cache = new AuthorityDeltaCache();
        var previous = new HostMemberRecord(1, State(900));
        var change = new ScopeEvent(1, ScopeEventKind.State, Key(), 2, 5, State(800));
        var record = new DeferredAuthorityState(change, previous);
        Parallel.For(0, 32, worker =>
        {
            var header = new AuthorityEnvelopeHeader(AuthorityFamily.WorldState, Epoch, default, 1, 5, 0, 0);
            var packet = DeferredAuthorityStates.Build(new[] { record }, header, Scope, 9, 17, cache);
            TestAssert.AreEqual(17L, packet.SubscriptionEpoch);
            TestAssert.AreEqual(9L, packet.DeclaredBaselineId);
            TestAssert.IsTrue(AuthorityWorldStateCodec.TryDecode(packet.Data, 0, packet.Data.Length, 1, Context, out var decoded, out _));
            TestAssert.IsTrue(decoded[0].IsDelta);
            TestAssert.AreEqual(1L, decoded[0].BaseRevision);
        });
    }

    private static EnemyUnitComponent Unit() => new() { id = 9, enemyId = 3, protoId = 8128, planetId = 101, level = 8, behavior = EEnemyBehavior.Engage };

    [TestMethod]
    public void CapturedNativeEncodingMatchesRawWireAndRetainsStrictRawValidation()
    {
        var unit = Unit();
        var ground = new GroundEnemyState(GroundEnemyKind.GroundUnit, true, true, 8128, 45, 2, 3, 0,
            101, 101, 7, 0, 8, 10, 0, 20, 0, 0, 0, 1, 1, 2, 3, 500, 900, 2, 0);
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncodeCaptured(ground, unit, out var bytes, out var canonical));
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(canonical, out var raw));
        CollectionAssert.AreEqual(raw, bytes);
        TestAssert.IsTrue(GroundEnemyStateCodec.TryDecode(bytes, 0, bytes.Length, out _, out _));
        var space = new SpaceEnemyState(SpaceEnemyKind.SpaceUnit, true, true, 8128, 45, 2, 3, 0,
            101, 1000001, 7, 0, 8, 10, 0, 20, 0, 0, 0, 1, 1, 2, 3, 500, 900, 2, 0);
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncodeCaptured(space, unit, out bytes, out var spaceCanonical));
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(spaceCanonical, out raw));
        CollectionAssert.AreEqual(raw, bytes);
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryDecode(bytes, 0, bytes.Length, out _, out _));
        Array.Clear(canonical.NativeUnit, 0, canonical.NativeUnit.Length);
        TestAssert.IsFalse(GroundEnemyStateCodec.TryEncode(canonical, out _));
        unit.disturbValue = float.NaN;
        TestAssert.IsFalse(GroundEnemyStateCodec.TryEncodeCaptured(ground, unit, out _, out _));
        TestAssert.IsFalse(SpaceEnemyStateCodec.TryEncodeCaptured(space, unit, out _, out _));
    }

    [TestMethod]
    public void AcceptedSocketOptionsApplyToTheActualClientRatherThanTheListener()
    {
        using var client = new System.Net.Sockets.TcpClient();
        client.NoDelay = false;
        TestAssert.IsTrue(TcpSocketOptions.TryEnableNoDelay(new SocketOwner(client), out var previous));
        TestAssert.IsFalse(previous); TestAssert.IsTrue(client.NoDelay);
        TestAssert.IsFalse(TcpSocketOptions.TryEnableNoDelay(new object(), out _));
    }
    private sealed class SocketOwner
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0052:Remove unread private members",
            Justification = "TcpSocketOptions reads this native socket field by reflection; its name and visibility are part of the fixture.")]
        private readonly System.Net.Sockets.TcpClient _tcpClient;
        public SocketOwner(System.Net.Sockets.TcpClient client) => _tcpClient = client;
    }

    [TestMethod]
    public void BackgroundSocketSendUsesTheNativeFrameAndRetainsWriteFailureResults()
    {
        var socket = new WebSocket("ws://localhost:19869/");
        AccessTools.Field(typeof(WebSocket), "_client").SetValue(socket, false);
        AccessTools.Field(typeof(WebSocket), "_readyState").SetValue(socket, WebSocketState.Open);
        using var output = new MemoryStream();
        AccessTools.Field(typeof(WebSocket), "_stream").SetValue(socket, output);
        TestAssert.IsTrue(BlockingWebSocketSender.Send(socket, new byte[] { 7, 8, 9 }));
        CollectionAssert.AreEqual(new byte[] { 0x82, 3, 7, 8, 9 }, output.ToArray());
        AccessTools.Field(typeof(WebSocket), "_stream").SetValue(socket, new BrokenStream());
        TestAssert.IsFalse(BlockingWebSocketSender.Send(socket, new byte[] { 10 }));
    }


    [TestMethod]
    public void CombinedRealtimeEnvelopesCompressOnceEvenWhenPreparationIsBacklogged()
    {
        var writer = new NebulaeNetPacketProcessor(); PacketUtils.RegisterNestedTypes(writer);
        var inner = writer.Write(new AuthorityWorldStatePacket { Data = new byte[4096] });
        var batch = RealtimePacketBatch.Encode(Enumerable.Repeat(inner, 16).ToArray(), writer);
        var framed = PacketCompression.WrapForSend(batch, true);
        TestAssert.AreEqual(PacketCompression.FlagDeflate, framed[PacketCompression.FlagOffset]);
        TestAssert.IsLessThan(batch.Length / 4, framed.Length);
        CollectionAssert.AreEqual(batch, PacketCompression.UnwrapForReceive(framed));
    }

    [TestMethod]
    public void RealtimeTransportBatchPreservesChildOrderAndCannotCrossJoinBoundaries()
    {
        var writer = new NebulaeNetPacketProcessor(); PacketUtils.RegisterNestedTypes(writer);
        var packets = Enumerable.Range(1, 36).Select(i => writer.Write(new GameStateUpdate(i, i, 60, 1))).ToArray();
        var control = writer.Write(new GameStateRequest());
        TestAssert.IsFalse(RealtimePacketBatch.CanInclude(control));
        TestAssert.IsFalse(RealtimePacketBatch.CanInclude(writer.Write(new AuthoritySnapshotChunkPacket { Data = new byte[8] })));
        var jobs = new Queue<Action>(); var sent = new List<byte[]>(); var callback = new Queue<Action>();
        var queue = new OrderedSendQueue(b => b, (bytes, done) => { sent.Add(bytes); callback.Enqueue(() => done(true)); }, jobs.Enqueue,
            combine: children => RealtimePacketBatch.Encode(children, writer), canCombine: bytes => RealtimePacketBatch.CanInclude(bytes));
        for (var i = 0; i < 18; i++) queue.Enqueue(packets[i]);
        queue.EnqueueBatch(new[] { control }, items => items); // transfer boundary stays an independent operation
        for (var i = 18; i < packets.Length; i++) queue.Enqueue(packets[i]);
        while (jobs.Count > 0) jobs.Dequeue()();
        while (callback.Count > 0) { callback.Dequeue()(); while (jobs.Count > 0) jobs.Dequeue()(); }
        TestAssert.IsTrue(queue.Flush(1)); TestAssert.IsLessThan(10, sent.Count, "Reduce socket operations, not logical records.");
        var decoded = new List<byte[]>();
        var reader = new NebulaeNetPacketProcessor(); PacketUtils.RegisterNestedTypes(reader);
        reader.SubscribeReusable<StarBroadcastPacket>(p =>
        {
            TestAssert.IsLessThanOrEqualTo(RealtimePacketBatch.MaxPackets, p.PacketObjects.Length);
            decoded.AddRange(p.PacketObjects);
        });
        foreach (var bytes in sent)
        {
            if (BitConverter.ToUInt64(bytes, 0) == WireContract.PacketId(typeof(StarBroadcastPacket))) reader.ReadPacket(new NetDataReader(bytes));
            else decoded.Add(bytes);
        }
        TestAssert.HasCount(37, decoded);
        for (var i = 0; i < 18; i++) CollectionAssert.AreEqual(packets[i], decoded[i]);
        CollectionAssert.AreEqual(control, decoded[18]);
        for (var i = 18; i < 36; i++) CollectionAssert.AreEqual(packets[i], decoded[i + 1]);
    }

    [TestMethod]
    public void BackloggedRealtimePacketsUseCompatibleRawFramesWhileSnapshotsKeepCompression()
    {
        var processor = new NebulaeNetPacketProcessor(); PacketUtils.RegisterNestedTypes(processor);
        var realtime = processor.Write(new AuthorityWorldStatePacket { Data = new byte[4096] });
        var framed = PacketCompression.WrapForSend(realtime, true);
        TestAssert.AreEqual(PacketCompression.FlagRaw, framed[PacketCompression.FlagOffset]);
        CollectionAssert.AreEqual(realtime, PacketCompression.UnwrapForReceive(framed));
        TestAssert.AreEqual(PacketCompression.FlagDeflate, PacketCompression.WrapForSend(realtime)[PacketCompression.FlagOffset]);
        var snapshot = processor.Write(new AuthoritySnapshotChunkPacket { Data = new byte[4096] });
        var compressed = PacketCompression.WrapForSend(snapshot, true);
        TestAssert.AreEqual(PacketCompression.FlagDeflate, compressed[PacketCompression.FlagOffset]);
        CollectionAssert.AreEqual(snapshot, PacketCompression.UnwrapForReceive(compressed));
    }

    [TestMethod]
    public void BackgroundEncodingIsBoundedAndOutOfOrderCompletionKeepsWireOrder()
    {
        var jobs = new Queue<Action>(); var encoder = new BackgroundPacketEncoder(2, jobs.Enqueue);
        var computed = new List<byte>();
        Func<byte[]> Encode(byte value) => () => { computed.Add(value); return new[] { value }; };
        var first = encoder.Prepare(Encode(1)); var second = encoder.Prepare(Encode(2));
        var third = encoder.Prepare(Encode(3)); // full reservation pool: bounded synchronous fallback
        TestAssert.AreEqual(2, encoder.Active); TestAssert.HasCount(2, jobs);
        CollectionAssert.AreEqual(new byte[] { 3 }, computed);
        var earlier = jobs.Dequeue(); jobs.Dequeue()(); earlier();
        TestAssert.AreEqual(0, encoder.Active);
        var senderJobs = new Queue<Action>(); var wire = new List<byte>();
        var queue = new OrderedSendQueue(b => b, (b, done) => { wire.Add(b[0]); done(true); }, senderJobs.Enqueue);
        queue.Enqueue(first); queue.Enqueue(second); queue.Enqueue(third);
        while (senderJobs.Count > 0) senderJobs.Dequeue()();
        CollectionAssert.AreEqual(new byte[] { 3, 2, 1 }, computed);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, wire);
        TestAssert.IsTrue(queue.Flush(1));
    }

    [TestMethod]
    public void BackgroundEncodingAndSchedulingFailuresAreDeliveredToTheSenderWithoutLeakingSlots()
    {
        var encoder = new BackgroundPacketEncoder(1, work => work());
        var failed = encoder.Prepare(() => throw new InvalidOperationException("encode"));
        TestAssert.ThrowsExactly<InvalidOperationException>(() => failed());
        TestAssert.AreEqual(0, encoder.Active);
        encoder = new BackgroundPacketEncoder(1, work => throw new System.IO.IOException("schedule"));
        failed = encoder.Prepare(() => new byte[] { 1 });
        TestAssert.ThrowsExactly<System.IO.IOException>(() => failed());
        TestAssert.AreEqual(0, encoder.Active);
    }

    [TestMethod]
    public void SenderPreparesAheadOfSlowIoWithOnlyTwoReadyPacketsAndPreservesOrder()
    {
        var work = new Queue<Action>(); var completions = new Queue<Action>();
        var sent = new List<byte>(); var prepared = 0; var inFlight = false;
        var queue = new OrderedSendQueue(bytes => { prepared++; return bytes; }, (bytes, done) =>
        {
            TestAssert.IsFalse(inFlight, "Only one socket operation can own the connection.");
            inFlight = true; sent.Add(bytes[0]); completions.Enqueue(() => { inFlight = false; done(true); });
        }, work.Enqueue);
        for (byte i = 1; i <= 10; i++) queue.Enqueue(new[] { i });
        work.Dequeue()();
        TestAssert.AreEqual(3, prepared, "One in flight plus two ready; later packets stay unprepared.");
        TestAssert.HasCount(1, sent);
        TestAssert.IsFalse(queue.Flush(1));
        while (completions.Count > 0)
        {
            completions.Dequeue()();
            while (work.Count > 0) work.Dequeue()();
        }
        CollectionAssert.AreEqual(Enumerable.Range(1, 10).Select(i => (byte)i).ToArray(), sent);
        TestAssert.AreEqual(10, prepared);
        TestAssert.IsTrue(queue.Flush(1));
    }

    [TestMethod]
    public void DisconnectDuringOverlappedPreparationCannotSendPreparedOrLaterPackets()
    {
        using var secondStarted = new ManualResetEventSlim(); using var releaseSecond = new ManualResetEventSlim();
        var work = new Queue<Action>(); var sent = new List<byte>(); Action<bool>? firstComplete = null;
        var queue = new OrderedSendQueue(bytes =>
        {
            if (bytes[0] == 2) { secondStarted.Set(); TestAssert.IsTrue(releaseSecond.Wait(5000)); }
            return bytes;
        }, (bytes, done) => { sent.Add(bytes[0]); firstComplete = done; }, work.Enqueue);
        queue.Enqueue(new byte[] { 1 }); queue.Enqueue(new byte[] { 2 }); queue.Enqueue(new byte[] { 3 });
        var preparation = Task.Run(work.Dequeue());
        try
        {
            TestAssert.IsTrue(secondStarted.Wait(5000));
            queue.Stop(); firstComplete!(true);
        }
        finally { releaseSecond.Set(); }
        preparation.GetAwaiter().GetResult();
        CollectionAssert.AreEqual(new byte[] { 1 }, sent);
        TestAssert.IsFalse(queue.Flush(1));
    }

    [TestMethod]
    public void IndependentWorkerSerializersKeepTheSameAuthorityWireBytes()
    {
        var record = new DeferredAuthorityState(new ScopeEvent(1, ScopeEventKind.State, Key(), 2, 5, State(800)), new HostMemberRecord(1, State(900)));
        var packet = DeferredAuthorityStates.Build(new[] { record }, new AuthorityEnvelopeHeader(AuthorityFamily.WorldState, Epoch, default, 2, 5, 0, 0), Scope, 9, 17, null);
        var main = new NebulaeNetPacketProcessor(); PacketUtils.RegisterNestedTypes(main);
        var expected = main.Write(packet);
        Parallel.For(0, 32, worker =>
        {
            var isolated = new NebulaeNetPacketProcessor(); PacketUtils.RegisterNestedTypes(isolated);
            CollectionAssert.AreEqual(expected, isolated.Write(packet));
        });
    }

    [TestMethod]
    public void PresentationWritesMergeUntilFrameEndAndFlushBeforeRemovalAndReuse()
    {
        var batch = new ReplicaPresentationWrites();
        var written = new List<(int, bool, bool)>();
        void Write(int id, bool moved, bool rotated) => written.Add((id, moved, rotated));
        batch.Add(7, true, false); batch.Add(7, false, true); batch.Add(7, false, false);
        TestAssert.IsEmpty(written); TestAssert.AreEqual(1, batch.Count);
        batch.FlushOne(7, Write); // before death/removal
        CollectionAssert.AreEqual(new[] { (7, true, true) }, written);
        batch.Add(7, false, false); // a fresh shell reused the old slot
        batch.Add(8, true, false); batch.Flush(Write);
        TestAssert.AreEqual((7, false, false), written[1]);
        TestAssert.AreEqual((8, true, false), written[2]);
        TestAssert.AreEqual(0, batch.Count);
        batch.Add(9, true, true);
        TestAssert.ThrowsExactly<InvalidOperationException>(() => batch.Flush((_, _, _) => throw new InvalidOperationException()));
        TestAssert.AreEqual(0, batch.Count, "A failed display write cannot leak a batch into another frame.");
    }
}
