using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Planet;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Planet;
internal class PlanetDetailRequestProcessor : PacketProcessor<PlanetDetailRequest>
{
    protected override void ProcessPacket(PlanetDetailRequest packet, NebulaeConnection conn)
    {
        if (IsClient) return;
        var planet = GameMain.galaxy.PlanetById(packet.PlanetID);
        if (planet != null) Multiplayer.Session.Planets.RequestDetails(planet, conn);
    }
}
