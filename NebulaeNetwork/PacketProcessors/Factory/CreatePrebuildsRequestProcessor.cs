#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Factory;
internal class CreatePrebuildsRequestProcessor : PacketProcessor<CreatePrebuildsRequest>
{
    protected override void ProcessPacket(CreatePrebuildsRequest packet, NebulaeConnection conn)
    {
        using (Multiplayer.Session.Factories.IsIncomingRequest.On())
        {
            // setting specifyPlanet here to avoid accessing a null object (see GPUInstancingManager activePlanet getter)
            var pData = GameMain.gpuiManager.specifyPlanet;

            GameMain.gpuiManager.specifyPlanet = GameMain.galaxy.PlanetById(packet.PlanetId);
            Multiplayer.Session.BuildTools.CreatePrebuildsRequest(packet);
            GameMain.gpuiManager.specifyPlanet = pData;
        }
    }
}
