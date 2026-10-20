#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Warning;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Warning;
internal class WarningDataProcessor : PacketProcessor<WarningDataPacket>
{
    protected override void ProcessPacket(WarningDataPacket packet, NebulaeConnection conn)
    {
        Multiplayer.Session.Warning.TickData = packet.Tick;
        using var reader = new BinaryUtils.Reader(packet.BinaryData);
        Multiplayer.Session.Warning.ImportBinaryData(reader.BinaryReader, packet.ActiveWarningCount);
    }
}
