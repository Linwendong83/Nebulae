using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Planet;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Planet;
internal class PlanetDataResponseProcessor : PacketProcessor<PlanetDataResponse>
{
    protected override void ProcessPacket(PlanetDataResponse packet, NebulaeConnection conn)
    {
        if (IsHost || packet.PlanetDataByte == null) return;
        var planet = Multiplayer.Session.IsInLobby
            ? UIRoot.instance.galaxySelect.starmap._galaxyData.PlanetById(packet.PlanetDataID)
            : GameMain.galaxy.PlanetById(packet.PlanetDataID);
        if (planet == null) return;
        if (Multiplayer.Session.IsInLobby)
        {
            using var reader = new BinaryUtils.Reader(packet.PlanetDataByte);
            planet.ImportRuntime(reader.BinaryReader);
            return;
        }
        // The generator may still be using modData. Install the response at the modeling boundary.
        Multiplayer.Session.Planets.ReceiveRuntime(planet, packet.PlanetDataByte);
    }
}
