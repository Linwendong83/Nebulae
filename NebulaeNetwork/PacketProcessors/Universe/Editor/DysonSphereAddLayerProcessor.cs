#region

using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe.Editor;
internal class DysonSphereAddLayerProcessor : PacketProcessor<DysonSphereAddLayerPacket>
{
    protected override void ProcessPacket(DysonSphereAddLayerPacket packet, NebulaeConnection conn)
    {
        var sphere = GameMain.data.dysonSpheres[packet.StarIndex];
        if (sphere == null)
        {
            return;
        }
        using (Multiplayer.Session.DysonSpheres.IsIncomingRequest.On())
        {
            if (packet.LayerId != sphere.QueryLayerId())
            {
                Multiplayer.Session.DysonSpheres.HandleDesync(packet.StarIndex, conn);
                return;
            }
            sphere.AddLayer(packet.OrbitRadius, packet.OrbitRotation.ToQuaternion(), packet.OrbitAngularSpeed);
        }
        if (IsHost)
        {
            Multiplayer.Session.DysonSpheres.SendPacketToDysonSphereExcept(packet, packet.StarIndex, conn);
        }
    }
}
