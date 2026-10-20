#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeWorld;
using NebulaeWorld.Universe;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe.Editor;
internal class DysonSphereRemoveLayerProcessor : PacketProcessor<DysonSphereRemoveLayerPacket>
{
    protected override void ProcessPacket(DysonSphereRemoveLayerPacket packet, NebulaeConnection conn)
    {
        var sphere = GameMain.data.dysonSpheres[packet.StarIndex];
        if (sphere == null)
        {
            return;
        }
        using (Multiplayer.Session.DysonSpheres.IsIncomingRequest.On())
        {
            sphere.RemoveLayer(packet.LayerId);
            DysonSphereManager.ClearSelection(packet.StarIndex);
        }
        if (IsHost)
        {
            Multiplayer.Session.DysonSpheres.SendPacketToDysonSphereExcept(packet, packet.StarIndex, conn);
        }
    }
}
