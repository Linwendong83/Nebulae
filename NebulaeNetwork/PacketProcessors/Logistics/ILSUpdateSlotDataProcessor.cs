#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics;
using NebulaeWorld.Logistics;

#endregion

namespace NebulaeNetwork.PacketProcessors.Logistics;
internal class ILSUpdateSlotDataProcessor : PacketProcessor<ILSUpdateSlotData>
{
    protected override void ProcessPacket(ILSUpdateSlotData packet, NebulaeConnection conn)
    {
        ILSShipManager.UpdateSlotData(packet);
    }
}
