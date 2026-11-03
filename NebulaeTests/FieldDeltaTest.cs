using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Factory.Tank;
using NebulaeModel.Packets.Logistics;
using NebulaeModel.Packets.Players;
using NebulaeModel.Packets.Routers;
using NebulaeModel.Packets.Session;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class FieldDeltaTest
{

    private static NebulaeNetPacketProcessor Writer()
    {
        var writer = new NebulaeNetPacketProcessor(); PacketUtils.RegisterNestedTypes(writer);
        writer.SubscribeReusable<PlayerMovement>(_ => { });
        writer.SubscribeReusable<PlayerDashboardPacket>(_ => { });
        writer.SubscribeReusable<DispenserStorePacket>(_ => { });
        writer.SubscribeReusable<TankStorageUpdatePacket>(_ => { });
        writer.SubscribeReusable<PlayerGiveItemPacket>(_ => { });
        writer.SubscribeReusable<PlanetBroadcastPacket>(_ => { });
        writer.SubscribeReusable<StarBroadcastPacket>(_ => { });
        writer.SubscribeReusable<JoinSyncBatch>(_ => { });
        writer.SubscribeReusable<StateDeltaPacket>(_ => { });
        return writer;
    }

    private static PlayerMovement Movement(ushort player, float x) => new()
    {
        PlayerId = player,
        LocalPlanetId = 101,
        LocalPlanetPosition = new Float3(x, 0, 0),
        UPosition = new Double3(1, 2, 3),
        BuildArea = 80,
        ConstructionDroneCount = 10,
        ConstructionDronesEnabled = true,
        Rotation = new Float3(0, 0, 0),
        BodyRotation = new Float3(0, 0, 0)
    };

    [TestMethod]
    public void PacketInventoryCoversEveryProductionTypeWithoutApplyingStateRulesToOperations()
    {
        foreach (var type in typeof(PlayerMovement).Assembly.GetTypes().Where(t => t.IsDefined(typeof(PacketIdAttribute), false)))
            TestAssert.IsTrue(PacketStateRegistry.Contains(type), type.FullName);
        TestAssert.AreEqual(PacketSyncKind.Operation, PacketStateRegistry.Kind(typeof(PlayerGiveItemPacket)));
        TestAssert.AreEqual(PacketSyncKind.Operation, PacketStateRegistry.Kind(typeof(ILSStorageCommitPacket)));
        TestAssert.AreEqual(PacketSyncKind.State, PacketStateRegistry.Kind(typeof(PlayerMovement)));
    }

    [TestMethod]
    public void SingleMovementFieldUsesTheRecipientsBaselineAndRestoresExactBytes()
    {
        var writer = Writer(); var host = new StateDeltaChannel(); var a = new StateDeltaChannel(); var b = new StateDeltaChannel();
        var otherHost = new StateDeltaChannel();
        var initial = writer.Write(Movement(7, 1)); var changed = writer.Write(Movement(7, 2));
        var immutableCopy = initial.ToArray();
        CollectionAssert.AreEqual(initial, a.Receive(host.Prepare(initial)));
        var patch = host.Prepare(changed);
        TestAssert.IsLessThan(changed.Length, patch.Length);
        TestAssert.AreEqual(WireContract.PacketId(typeof(StateDeltaPacket)), BitConverter.ToUInt64(patch, 0));
        CollectionAssert.AreEqual(changed, a.Receive(patch));
        var otherFull = otherHost.Prepare(changed);
        CollectionAssert.AreEqual(changed, otherFull, "A second recipient starts with a full state.");
        CollectionAssert.AreEqual(changed, b.Receive(otherFull));
        CollectionAssert.AreEqual(immutableCopy, initial, "Fan-out must not mutate shared data.");
    }

    [TestMethod]
    public void InterleavedPlayersAndDirectionsNeverShareAFieldBaseline()
    {
        var writer = Writer(); var send = new StateDeltaChannel(); var receive = new StateDeltaChannel();
        foreach (var pair in new[] { (7, 1f), (8, 100f), (7, 2f), (8, 99f), (7, 0f) })
        {
            var bytes = writer.Write(Movement((ushort)pair.Item1, pair.Item2));
            CollectionAssert.AreEqual(bytes, receive.Receive(send.Prepare(bytes)));
        }
        var reverse = writer.Write(Movement(7, -20));
        CollectionAssert.AreEqual(reverse, receive.Prepare(reverse), "Incoming state does not seed the reverse direction.");
    }

    [TestMethod]
    public void FloatBitPatternsClearingAndArrayLengthChangesAreLossless()
    {
        var writer = Writer(); var send = new StateDeltaChannel(); var receive = new StateDeltaChannel();
        var random = new Random(73);
        foreach (var length in new[] { 256, 256, 256, 0, 1, 256 })
        {
            var ids = Enumerable.Range(1, length).ToArray();
            var count = new int[length]; if (length > 0) count[length / 2] = random.Next();
            var packet = new DispenserStorePacket
            {
                PlanetId = 101,
                DispenserId = 7,
                HoldupItemCount = length,
                ItemIds = ids,
                Counts = count,
                Incs = new int[length]
            };
            var bytes = writer.Write(packet); CollectionAssert.AreEqual(bytes, receive.Receive(send.Prepare(bytes)));
        }
        foreach (var bits in new[] { 0, int.MinValue, 0x3f800001, 0x7fc00001, unchecked((int)0xff800000) })
        {
            var packet = Movement(1, BitConverter.ToSingle(BitConverter.GetBytes(bits), 0));
            var bytes = writer.Write(packet); CollectionAssert.AreEqual(bytes, receive.Receive(send.Prepare(bytes)));
        }
    }

    [TestMethod]
    public void SparseArrayCarriesOnlyChangedElementsAndDenseChangeUsesFullState()
    {
        var writer = Writer(); var sender = new StateDeltaChannel(); var receiver = new StateDeltaChannel();
        var first = new PlayerDashboardPacket(7, new byte[4096]);
        var bytes = writer.Write(first); receiver.Receive(sender.Prepare(bytes));
        first.DashboardData[203] = 17;
        var changed = writer.Write(first); var sparse = sender.Prepare(changed);
        TestAssert.IsLessThan(100, sparse.Length, "One byte array element must not resend the blob.");
        CollectionAssert.AreEqual(changed, receiver.Receive(sparse));
        new Random(17).NextBytes(first.DashboardData);
        var dense = writer.Write(first); CollectionAssert.AreEqual(dense, sender.Prepare(dense));
        CollectionAssert.AreEqual(dense, receiver.Receive(dense));
    }

    [TestMethod]
    public void RepeatedOperationsAndSmallFullFallbacksRetainEveryMessage()
    {
        var writer = Writer(); var sender = new StateDeltaChannel(); var receiver = new StateDeltaChannel();
        var operation = writer.Write(new PlayerGiveItemPacket());
        for (var i = 0; i < 3; i++) CollectionAssert.AreEqual(operation, receiver.Receive(sender.Prepare(operation)));
        var tiny = writer.Write(new TankStorageUpdatePacket { PlanetId = 101, TankIndex = 1, FluidCount = 42 });
        for (var i = 0; i < 3; i++) CollectionAssert.AreEqual(tiny, receiver.Receive(sender.Prepare(tiny)));
        TestAssert.AreEqual(0L, sender.OutgoingCacheBytes, "Fixed small states need no delta metadata or cache.");
        TestAssert.AreEqual(0L, receiver.IncomingCacheBytes);
    }

    [TestMethod]
    public void PlanetAndStarEnvelopesReconstructChildrenBeforeRouting()
    {
        var writer = Writer(); var sender = new StateDeltaChannel(); var receiver = new StateDeltaChannel();
        for (var i = 0; i < 3; i++)
        {
            var child = writer.Write(Movement(7, i));
            var operation = writer.Write(new PlayerGiveItemPacket());
            var bytes = writer.Write(new StarBroadcastPacket(new[] {
                writer.Write(new PlanetBroadcastPacket(new[] { child, operation, operation }, 101)) }, 1));
            CollectionAssert.AreEqual(bytes, receiver.Receive(sender.Prepare(bytes)));
        }
        var join = writer.Write(new JoinSyncBatch
        {
            Generation = 1,
            FirstSequence = 1,
            Scopes = new[] { 101 },
            Packets = new[] { writer.Write(Movement(7, 5)) }
        });
        CollectionAssert.AreEqual(join, receiver.Receive(sender.Prepare(join)), "Join replay retains full data and sequence positions.");
    }

    [TestMethod]
    public void MissingBaselineRequestsOneRepairWhoseValueIsResolvedAtItsSendPosition()
    {
        var writer = Writer(); var controls = new List<byte[]>(); var resend = new List<Func<byte[]>>();
        var sender = new StateDeltaChannel(sendLatest: resend.Add);
        var fresh = new StateDeltaChannel(sendControl: controls.Add);
        sender.Prepare(writer.Write(Movement(7, 0)));
        var patch = sender.Prepare(writer.Write(Movement(7, 1)));
        TestAssert.IsNull(fresh.Receive(patch)); TestAssert.IsNull(fresh.Receive(patch));
        TestAssert.HasCount(1, controls); TestAssert.AreEqual(1, fresh.PendingRepairs);
        TestAssert.IsNull(sender.Receive(controls[0]));
        var newest = writer.Write(Movement(7, 99)); sender.Prepare(newest);
        var repaired = sender.Prepare(resend.Single()());
        CollectionAssert.AreEqual(newest, fresh.Receive(repaired));
        TestAssert.AreEqual(0, fresh.PendingRepairs);
        var next = writer.Write(Movement(7, 100));
        CollectionAssert.AreEqual(next, fresh.Receive(sender.Prepare(next)));
    }

    [TestMethod]
    public void RepairOfARoutedStateRetainsItsPlanetAndStarWithoutReplayingNeighborOperations()
    {
        var writer = Writer(); var controls = new List<byte[]>(); var repairs = new List<Func<byte[]>>();
        var sender = new StateDeltaChannel(sendLatest: repairs.Add);
        var receiver = new StateDeltaChannel(sendControl: controls.Add);
        byte[] Envelope(byte[] child, bool withOperation) => writer.Write(new StarBroadcastPacket(new[] {
            writer.Write(new PlanetBroadcastPacket(withOperation ? new[] { child, writer.Write(new PlayerGiveItemPacket()) } : new[] { child }, 101)) }, 1));
        sender.Prepare(Envelope(writer.Write(Movement(7, 0)), true));
        receiver.Receive(sender.Prepare(Envelope(writer.Write(Movement(7, 1)), true)));
        TestAssert.HasCount(1, controls); sender.Receive(controls[0]);
        sender.Prepare(Envelope(writer.Write(Movement(7, 2)), true));
        var repaired = sender.Prepare(repairs.Single()());
        CollectionAssert.AreEqual(Envelope(writer.Write(Movement(7, 2)), false), receiver.Receive(repaired));
    }

    [TestMethod]
    public void CacheEvictionForcesFullStateAndDisconnectReleasesBothDirections()
    {
        var writer = Writer(); var sender = new StateDeltaChannel(maxCacheBytes: 1000); var receive = new StateDeltaChannel(maxCacheBytes: 1000);
        var a = writer.Write(Movement(7, 0)); var b = writer.Write(Movement(8, 0));
        receive.Receive(sender.Prepare(a)); receive.Receive(sender.Prepare(b));
        var next = writer.Write(Movement(7, 1)); CollectionAssert.AreEqual(next, sender.Prepare(next));
        TestAssert.IsLessThanOrEqualTo(1000, sender.OutgoingCacheBytes); TestAssert.IsLessThanOrEqualTo(1000, receive.IncomingCacheBytes);
        sender.Clear(); receive.Clear();
        TestAssert.AreEqual(0L, sender.OutgoingCacheBytes); TestAssert.AreEqual(0L, receive.IncomingCacheBytes);
    }

    [TestMethod]
    public void TruncatedPatchesCannotPoisonThePreviousBaseline()
    {
        var writer = Writer(); var sender = new StateDeltaChannel(); var initial = writer.Write(Movement(7, 0));
        sender.Prepare(initial); var changed = writer.Write(Movement(7, 1)); var patch = sender.Prepare(changed);
        for (var length = 0; length < patch.Length; length++)
        {
            var receive = new StateDeltaChannel(); receive.Receive(initial);
            TestAssert.Throws<InvalidDataException>(() => receive.Receive(patch.Take(length).ToArray()), "length=" + length);
            CollectionAssert.AreEqual(changed, receive.Receive(patch));
        }
    }

    [TestMethod]
    public void PatchesCannotTargetCommandsOrChangeTheBusinessIdentity()
    {
        var writer = Writer(); var sender = new StateDeltaChannel(); var receiver = new StateDeltaChannel();
        var initial = writer.Write(Movement(7, 0)); receiver.Receive(sender.Prepare(initial));
        var patch = sender.Prepare(writer.Write(Movement(7, 1)));
        BitConverter.GetBytes(WireContract.PacketId(typeof(PlayerGiveItemPacket))).CopyTo(patch, 8);
        TestAssert.Throws<InvalidDataException>(() => receiver.Receive(patch));
    }

    [TestMethod]
    public void FieldMaskRejectsUnknownBitsAndArrayIndexesWithoutPartialMutation()
    {
        var before = new byte[128]; var after = (byte[])before.Clone(); after[90] = 3;
        var fields = new[] { new WireField(0, 128, 1) };
        TestAssert.IsTrue(FieldDeltaCodec.TryEncode(before, fields, after, fields, out var patch));
        CollectionAssert.AreEqual(after, FieldDeltaCodec.Apply(before, fields, patch));
        var badMask = (byte[])patch.Clone(); badMask[1] |= 128;
        TestAssert.Throws<InvalidDataException>(() => FieldDeltaCodec.Apply(before, fields, badMask));
        var badIndex = (byte[])patch.Clone(); badIndex[4] = 128; badIndex[5] = 1;
        TestAssert.Throws<InvalidDataException>(() => FieldDeltaCodec.Apply(before, fields, badIndex));
        TestAssert.IsTrue(before.All(value => value == 0));
    }
}
