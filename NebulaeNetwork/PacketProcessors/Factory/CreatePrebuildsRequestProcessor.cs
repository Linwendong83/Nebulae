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
        // The re-entrant CreatePrebuilds call broadcasts this same blueprint back to the star;
        // exposing the packet lets it forward the encoded payload instead of re-serializing it.
        using (Multiplayer.Session.Factories.RelayPrebuildRequest(packet))
        {
            // setting specifyPlanet here to avoid accessing a null object (see GPUInstancingManager activePlanet getter)
            var pData = GameMain.gpuiManager.specifyPlanet;

            GameMain.gpuiManager.specifyPlanet = GameMain.galaxy.PlanetById(packet.PlanetId);
            Multiplayer.Session.BuildTools.CreatePrebuildsRequest(packet);
            GameMain.gpuiManager.specifyPlanet = pData;
        }
    }
}
