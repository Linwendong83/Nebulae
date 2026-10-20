#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Foundation;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory.Foundation;
internal class PlanetReformProcessor : PacketProcessor<PlanetReformPacket>
{
    protected override void ProcessPacket(PlanetReformPacket packet, NebulaeConnection conn)
    {
        using (Multiplayer.Session.Planets.IsIncomingRequest.On())
        {
            var planet = GameMain.galaxy.PlanetById(packet.PlanetId);
            if (planet?.factory == null)
            {
                return;
            }
            Multiplayer.Session.Factories.TargetPlanet = packet.PlanetId;
            Multiplayer.Session.Factories.AddPlanetTimer(packet.PlanetId);
            var pData = GameMain.gpuiManager.specifyPlanet;
            GameMain.gpuiManager.specifyPlanet = planet;

            if (packet.IsReform)
            {
                // Reform whole planet
                planet.factory.PlanetReformAll(packet.Type, packet.Color, packet.Bury);
            }
            else
            {
                // Revert whole planet
                planet.factory.PlanetReformRevert();
            }

            GameMain.gpuiManager.specifyPlanet = pData;
            Multiplayer.Session.Factories.TargetPlanet = SessionSentinels.PLANET_NONE;
        }
    }
}
