#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Belt;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Belt;
internal class ConnectToMonitorProcessor : PacketProcessor<ConnectToMonitorPacket>
{
    protected override void ProcessPacket(ConnectToMonitorPacket packet, NebulaeConnection conn)
    {
        using (Multiplayer.Session.Factories.IsIncomingRequest.On())
        {
            var cargoTraffic = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.cargoTraffic;
            if (cargoTraffic == null)
            {
                return;
            }
            cargoTraffic.ConnectToMonitor(packet.MonitorId, packet.BeltId, packet.Offset);
        }
    }
}
