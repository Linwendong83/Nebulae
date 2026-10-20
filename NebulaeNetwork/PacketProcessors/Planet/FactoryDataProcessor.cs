
#region
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Planet;
using NebulaeWorld;
using NebulaeWorld.GameStates;

#endregion

namespace NebulaeNetwork.PacketProcessors.Planet;
internal class FactoryDataProcessor : PacketProcessor<FactoryData>
{
    protected override void ProcessPacket(FactoryData packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            return;
        }
        var manager = Multiplayer.Session.Planets;
        Multiplayer.Session.JoinSync.ReceivedData(packet.BinaryData?.Length ?? 0);
        var planet = GameMain.galaxy.PlanetById(packet.PlanetId);
        GameStatesManager.FragmentSize = 0;
        var requestAgain = false;
        if (planet == null || !manager.Loads.Receive(packet.PlanetId, out requestAgain))
        {
            if (planet != null && requestAgain) Multiplayer.Session.Network.SendPacket(new FactoryLoadRequest(packet.PlanetId));
            return;
        }
        try
        {
            if (packet.BinaryData == null || packet.BinaryData.Length == 0 || packet.TerrainModData == null)
                throw new System.IO.InvalidDataException("Incomplete factory snapshot");
            manager.BufferFactory(packet);
        }
        catch (System.Exception error)
        {
            manager.FailFactory(planet, -1, error);
        }
    }
}
