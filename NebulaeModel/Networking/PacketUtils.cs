using NebulaeModel.DataStructures;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Factory.Belt;

namespace NebulaeModel.Networking;

internal static class PacketUtils
{
    internal static void RegisterNestedTypes(NetPacketProcessor processor)
    {
        processor.RegisterNestedType<Float3>();
        processor.RegisterNestedType<Float4>();
        processor.RegisterNestedType<Double3>();
        processor.RegisterNestedType<BeltUpdate>();
        // byte[] already has a top-level serializer; registering it as a property type is what lets a
        // packet carry an array of blobs, which the star and planet envelopes use to bundle several
        // inner packets into one message.
        processor.RegisterNestedType<byte[]>((writer, value) => writer.PutBytesWithLength(value),
            reader => reader.GetBytesWithLength());
        processor.RegisterNestedType(() => new DysonLaunchData());
        processor.RegisterNestedType(() => new PlayerData());
        processor.RegisterNestedType(() => new MechaData());
        processor.RegisterNestedType(() => new PlayerLifeData());
        processor.RegisterNestedType<PlayerTechBonuses>((writer, value) => value.Serialize(writer), reader =>
        {
            var value = new PlayerTechBonuses();
            value.Deserialize(reader);
            return value;
        });
    }
}
