#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Belt;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Belt;
internal class ConnectToSpraycoaterProcessor : PacketProcessor<ConnectToSpraycoaterPacket>
{
    protected override void ProcessPacket(ConnectToSpraycoaterPacket packet, NebulaeConnection conn)
    {
        using (Multiplayer.Session.Factories.IsIncomingRequest.On())
        {
            var cargoTraffic = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.cargoTraffic;
            if (cargoTraffic == null)
            {
                return;
            }
            cargoTraffic.ConnectToSpraycoater(packet.SpraycoaterId, packet.CargoBeltId, packet.IncBeltId);
        }
    }
}
