using NebulaeModel.Networking.Serialization;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel.Networking.Serialization;

[global::NebulaeModel.Networking.PacketId(4599520865776997475UL)]
internal class SampleDictionaryPacket
{
    [global::NebulaeModel.Networking.WireOrder(0)]
    public Dictionary<int, int> SampleInts { get; set; } = new();

    [global::NebulaeModel.Networking.WireOrder(1)]
    public Dictionary<SampleCustomClass, SampleCustomClass> SampleCustomTypes { get; set; } = new();
    [global::NebulaeModel.Networking.WireOrder(2)]
    public Dictionary<SampleNetSerializableClass, SampleNetSerializableStruct> SampleNetSerializables { get; set; } = new();
}

[TestClass]
public class DictionarySerializationTest
{
    private readonly SampleDictionaryPacket basePacket = new()
    {
        SampleInts = new() { { 1, 1 }, { 2, 2 }, { 3, 3 } },
        SampleCustomTypes =
            new()
            {
                { new SampleCustomClass { Value = 1 }, new SampleCustomClass() { Value = 2 } },
                { new SampleCustomClass { Value = 3 }, new SampleCustomClass() { Value = 4 } },
                { new SampleCustomClass { Value = 5 }, new SampleCustomClass() { Value = 6 } }
            },
        SampleNetSerializables =
            new()
            {
                { new SampleNetSerializableClass { value = 1 }, new SampleNetSerializableStruct { value = 1 } },
                { new SampleNetSerializableClass { value = 2 }, new SampleNetSerializableStruct { value = 2 } },
                { new SampleNetSerializableClass { value = 3 }, new SampleNetSerializableStruct { value = 3 } },
            }
    };

    [TestMethod, Timeout(1000, CooperativeCancellation = true)]
    public void SerializeDeserialize_DictionaryPackets_AreEqual()
    {
        var packetProcessor = new NebulaeNetPacketProcessor();
        packetProcessor.RegisterNestedType<SampleNetSerializableClass>(() => new SampleNetSerializableClass());

        // Subscribe first to force a packet type register call
        SampleDictionaryPacket? receivedPacket = null;
        packetProcessor.SubscribeReusable<SampleDictionaryPacket>(
            packet =>
            {
                receivedPacket = packet;
            });

        var writer = new NetDataWriter();
        packetProcessor.Write(writer, basePacket);

        var reader = new NetDataReader(writer);
        packetProcessor.ReadPacket(reader);

        TestAssert.IsNotNull(receivedPacket);
        CollectionAssert.AreEquivalent(basePacket.SampleInts.ToArray(), receivedPacket.SampleInts.ToArray());
        CollectionAssert.AreEquivalent(basePacket.SampleCustomTypes.ToArray(), receivedPacket.SampleCustomTypes.ToArray());
        CollectionAssert.AreEquivalent(basePacket.SampleNetSerializables.ToArray(), receivedPacket.SampleNetSerializables.ToArray());
    }
}
