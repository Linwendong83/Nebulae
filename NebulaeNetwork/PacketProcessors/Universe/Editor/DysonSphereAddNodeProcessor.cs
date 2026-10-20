#region

using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Logger;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe.Editor;
internal class DysonSphereAddNodeProcessor : PacketProcessor<DysonSphereAddNodePacket>
{
    protected override void ProcessPacket(DysonSphereAddNodePacket packet, NebulaeConnection conn)
    {
        var layer = GameMain.data.dysonSpheres[packet.StarIndex]?.GetLayer(packet.LayerId);
        if (layer == null)
        {
            return;
        }
        using (Multiplayer.Session.DysonSpheres.IsIncomingRequest.On())
        {
            var nodeId = layer.nodeRecycleCursor > 0 ? layer.nodeRecycle[layer.nodeRecycleCursor - 1] : layer.nodeCursor;
            if (nodeId != packet.NodeId || layer.NewDysonNode(packet.NodeProtoId, packet.Position.ToVector3()) == 0)
            {
                Log.Warn($"Cannnot add node[{packet.NodeId}] on layer[{layer.id}], starIndex[{packet.StarIndex}]");
                Multiplayer.Session.DysonSpheres.HandleDesync(packet.StarIndex, conn);
                return;
            }
        }
        if (IsHost)
        {
            Multiplayer.Session.DysonSpheres.SendPacketToDysonSphereExcept(packet, packet.StarIndex, conn);
        }
    }
}
