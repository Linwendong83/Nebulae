using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Session;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
[DoNotParallelize]
internal class WireContractTest
{
    [PacketId(0xff000001UL)]
    internal class OrderedPacket
    {
        [WireOrder(2)] public int AlphabeticallyFirst { get; set; }
        [WireOrder(0)] public int RenamedLater { get; set; }
        [WireOrder(1)] public byte[] Bytes { get; set; } = Array.Empty<byte>();
    }

    internal class MissingId { }
    internal class MissingOrder { public int Value { get; set; } }
    internal class DuplicateOrder
    {
        [WireOrder(0)] public int A { get; set; }
        [WireOrder(0)] public int B { get; set; }
    }
    [PacketId(0xff000002UL)] internal class DuplicateIdA { }
    [PacketId(0xff000002UL)] internal class DuplicateIdB { }

    [PacketId(0xff000003UL)]
    internal class PlayerCheckpoint
    {
        [WireOrder(0)] public global::NebulaeModel.DataStructures.PlayerData Data { get; set; } = new(1, 101);
    }

    [TestMethod]
    public void PlayerCheckpointsRequireCompleteFieldsAndAValidMovementState()
    {
        var processor = new NebulaeNetPacketProcessor();
        PacketUtils.RegisterNestedTypes(processor);
        var calls = 0;
        processor.SubscribeReusable<PlayerCheckpoint>(_ => calls++);
        var packet = new PlayerCheckpoint();
        var bytes = processor.Write(packet);
        for (var length = 0; length < bytes.Length; length++)
            TestAssert.Throws<ParseException>(() => processor.ReadPacket(new NetDataReader(bytes.Take(length).ToArray())));
        packet.Data.AcceptedMovementState = (EMovementState)255;
        TestAssert.Throws<ParseException>(() => processor.ReadPacket(new NetDataReader(processor.Write(packet))));
        TestAssert.AreEqual(0, calls);
        processor.ReadPacket(new NetDataReader(bytes));
        TestAssert.AreEqual(1, calls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AppearancePayloadRequiresItsHeaderAndExactBoundary(bool diy)
    {
        var harmony = new Harmony("nebulae.tests.appearance-payload");
        harmony.Patch(AccessTools.Method(typeof(MechaMaterial), nameof(MechaMaterial.SetProto)),
            prefix: new HarmonyMethod(typeof(WireContractTest), nameof(InitializeTestMaterial)));
        try
        {
            var processor = new NebulaeNetPacketProcessor();
            PacketUtils.RegisterNestedTypes(processor);
            var calls = 0;
            processor.SubscribeReusable<PlayerCheckpoint>(_ => calls++);
            var packet = new PlayerCheckpoint();
            var appearance = new MechaAppearance(); appearance.Init();
            if (diy) packet.Data.DIYAppearance = appearance;
            else packet.Data.Appearance = appearance;
            var bytes = processor.Write(packet);
            processor.ReadPacket(new NetDataReader(bytes));
            TestAssert.AreEqual(1, calls);

            var reader = new NetDataReader(bytes);
            reader.GetULong(); reader.GetString(); reader.GetUShort(); reader.GetInt();
            reader.GetFloat3(); reader.GetDouble3(); reader.GetFloat3(); reader.GetFloat3();
            new global::NebulaeModel.DataStructures.MechaData().Deserialize(reader);
            reader.GetBool();
            if (diy) reader.GetBool();
            var lengthOffset = reader.Position;
            var length = reader.GetInt();
            var payloadOffset = reader.Position;
            var payload = bytes.Skip(payloadOffset).Take(length).ToArray();
            MechaAppearanceCodec.ReadCompressed(Compress(payload)).Free();
            foreach (var header in new[] { -1, 0, 2, int.MaxValue })
            {
                var invalid = bytes.ToArray(); BitConverter.GetBytes(header).CopyTo(invalid, payloadOffset);
                TestAssert.Throws<ParseException>(() => processor.ReadPacket(new NetDataReader(invalid)));
                var invalidPayload = payload.ToArray(); BitConverter.GetBytes(header).CopyTo(invalidPayload, 0);
                TestAssert.Throws<ParseException>(() => MechaAppearanceCodec.ReadCompressed(Compress(invalidPayload)));
            }
            var padded = bytes.Take(payloadOffset + length).Concat(new byte[] { 0 }).Concat(bytes.Skip(payloadOffset + length)).ToArray();
            BitConverter.GetBytes(length + 1).CopyTo(padded, lengthOffset);
            TestAssert.Throws<ParseException>(() => processor.ReadPacket(new NetDataReader(padded)));
            TestAssert.Throws<ParseException>(() => MechaAppearanceCodec.ReadCompressed(Compress(payload.Concat(new byte[] { 0 }).ToArray())));
            TestAssert.AreEqual(1, calls);
            processor.ReadPacket(new NetDataReader(bytes));
            TestAssert.AreEqual(2, calls);
        }
        finally { harmony.UnpatchSelf(); }
    }

    private static bool InitializeTestMaterial(MechaMaterial __instance)
    {
        __instance.colors = Array.Empty<UnityEngine.Color32>();
        return false;
    }

    [TestMethod]
    public void MissingAndDuplicateDeclarationsAreRejected()
    {
        TestAssert.Throws<InvalidTypeException>(() => WireContract.PacketId(typeof(MissingId)));
        TestAssert.Throws<InvalidTypeException>(() => WireContract.Properties(typeof(MissingOrder)));
        TestAssert.Throws<InvalidTypeException>(() => WireContract.Properties(typeof(DuplicateOrder)));
        WireContract.PacketId(typeof(DuplicateIdA));
        TestAssert.Throws<InvalidTypeException>(() => WireContract.PacketId(typeof(DuplicateIdB)));
    }

    [TestMethod]
    public void ByteLayoutUsesDeclaredOrderAndRoundTripsArrays()
    {
        var processor = new NebulaeNetPacketProcessor();
        OrderedPacket? received = null;
        processor.SubscribeReusable<OrderedPacket>(p => received = p);
        var bytes = processor.Write(new OrderedPacket { RenamedLater = 123, AlphabeticallyFirst = 456, Bytes = new byte[] { 7, 8 } });
        var reader = new NetDataReader(bytes);
        TestAssert.AreEqual(0xff000001UL, reader.GetULong());
        TestAssert.AreEqual(123, reader.GetInt());
        TestAssert.AreEqual(2, reader.GetInt());
        TestAssert.AreEqual((byte)7, reader.GetByte());
        TestAssert.AreEqual((byte)8, reader.GetByte());
        TestAssert.AreEqual(456, reader.GetInt());
        TestAssert.AreEqual(0, reader.AvailableBytes);
        processor.ReadPacket(new NetDataReader(bytes));
        TestAssert.IsNotNull(received);
        TestAssert.AreEqual(456, received.AlphabeticallyFirst);
        CollectionAssert.AreEqual(new byte[] { 7, 8 }, received.Bytes);
    }

    [TestMethod]
    public void EveryTruncatedPayloadRefusesTheBusinessCallback()
    {
        var processor = new NebulaeNetPacketProcessor();
        var calls = 0;
        processor.SubscribeReusable<OrderedPacket>(_ => calls++);
        var bytes = processor.Write(new OrderedPacket { Bytes = new byte[] { 1, 2, 3 } });
        for (var length = 0; length < bytes.Length; length++)
            TestAssert.Throws<ParseException>(() => processor.ReadPacket(new NetDataReader(bytes.Take(length).ToArray())));
        TestAssert.AreEqual(0, calls);
        processor.ReadPacket(new NetDataReader(bytes));
        TestAssert.AreEqual(1, calls);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void EverySubscriptionRequiresExactlyOneCompletePayload(int subscription)
    {
        var processor = new NebulaeNetPacketProcessor();
        var calls = 0;
        switch (subscription)
        {
            case 0: processor.Subscribe<OrderedPacket>(_ => calls++, () => new OrderedPacket()); break;
            case 1: processor.Subscribe<OrderedPacket, object>((_, _) => calls++, () => new OrderedPacket()); break;
            case 2: processor.SubscribeReusable<OrderedPacket>(_ => calls++); break;
            case 3: processor.SubscribeReusable<OrderedPacket, object>((_, _) => calls++); break;
        }
        var bytes = processor.Write(new OrderedPacket { Bytes = new byte[] { 1, 2 } });
        TestAssert.Throws<ParseException>(() => processor.ReadPacket(new NetDataReader(bytes.Concat(new byte[] { 0 }).ToArray())));
        TestAssert.Throws<ParseException>(() => processor.ReadPacket(new NetDataReader(bytes.Concat(bytes).ToArray())));
        TestAssert.AreEqual(0, calls);
        processor.ReadPacket(new NetDataReader(bytes));
        TestAssert.AreEqual(1, calls);
    }

    [TestMethod]
    public void ReaderBoundsAndCanonicalBooleanValuesAreRequired()
    {
        var writer = new NetDataWriter();
        writer.Put(123);
        var reader = new NetDataReader(writer.Data, 0, 3);
        TestAssert.Throws<ParseException>(() => reader.GetInt());
        TestAssert.Throws<ParseException>(() => reader.GetBytesSegment(4));
        TestAssert.Throws<ParseException>(() => reader.SkipBytes(4));
        TestAssert.AreEqual(0, reader.Position);
        foreach (var value in new byte[] { 2, 255 })
        {
            TestAssert.Throws<ParseException>(() => new NetDataReader(new[] { value }).GetBool());
            writer.Reset(); writer.Put(1); writer.Put(value);
            TestAssert.Throws<ParseException>(() => new NetDataReader(writer).GetBoolArray());
        }
        writer.Reset(); writer.Put("long");
        TestAssert.Throws<ParseException>(() => new NetDataReader(writer).GetString(3));
    }

    [TestMethod]
    public void DuplicateHandlerRegistrationIsRejected()
    {
        var processor = new NebulaeNetPacketProcessor();
        processor.SubscribeReusable<OrderedPacket>(_ => { });
        TestAssert.Throws<ArgumentException>(() => processor.SubscribeReusable<OrderedPacket>(_ => { }));
    }

    [TestMethod]
    public void AllBuiltInProcessorsRegisterOnBothSidesWithoutNameLookups()
    {
        TestAssert.IsGreaterThan(200, RuntimePacketRegistry.ProcessorTypes.Length);
        RuntimePacketRegistry.Register(new NebulaeNetPacketProcessor(), true);
        RuntimePacketRegistry.Register(new NebulaeNetPacketProcessor(), false);
        var packets = typeof(LobbyRequest).Assembly.GetTypes().Where(t => t.IsDefined(typeof(PacketIdAttribute), false)).ToArray();
        var ids = packets.Select(WireContract.PacketId).ToArray();
        TestAssert.AreEqual(ids.Length, ids.Distinct().Count());
        foreach (var packet in packets) WireContract.Properties(packet);
        TestAssert.AreEqual(3015417578455077658UL, WireContract.PacketId(typeof(HandshakeResponse)));
    }

    [TestMethod]
    public void InheritedEnvelopeMembersUseTheirDeclaredOrder()
    {
        var props = WireContract.Properties(typeof(AuthorityWelcomePacket));
        var inherited = props.Where(p => p.DeclaringType == typeof(AuthorityEnvelopePacket)).ToArray();
        TestAssert.HasCount(7, inherited);
        TestAssert.AreEqual(nameof(AuthorityEnvelopePacket.EpochHigh), inherited[0].Name);
        TestAssert.AreEqual(nameof(AuthorityEnvelopePacket.SubscriptionEpoch), inherited[6].Name);
        TestAssert.IsTrue(props.Take(props.Length - 7).All(p => p.DeclaringType != typeof(AuthorityEnvelopePacket)));
    }

    [TestMethod]
    public void ContractsAreInternal()
    {
        TestAssert.IsFalse(typeof(LobbyRequest).IsVisible);
        TestAssert.IsFalse(typeof(global::NebulaeModel.Contracts.INetSerializable).IsVisible);
    }
}
