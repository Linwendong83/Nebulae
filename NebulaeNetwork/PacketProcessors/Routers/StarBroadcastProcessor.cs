#region

using NebulaeModel.Logger;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Routers;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Routers;
internal class StarBroadcastProcessor : PacketProcessor<StarBroadcastPacket>
{
    protected override void ProcessPacket(StarBroadcastPacket packet, NebulaeConnection conn)
    {
        // SubscribeReusable can replace the outer envelope when a child has the same type.
        var envelope = new StarBroadcastPacket(packet.PacketObjects, packet.StarId);
        // Apply the children in order before forwarding the committed operation. Requeueing
        // them could let a later snapshot include the wrong side of this change.
        ForwardInnerPackets(envelope.PacketObjects, conn);
        //Forward packet to other users if we're the host
        if (IsHost && ((NebulaeModel.Networking.Serialization.NebulaeNetPacketProcessor)Multiplayer.Session.Network.PacketProcessor).DispatchDepth <= 1)
            Multiplayer.Session.Server.SendToMatching(envelope, p =>
                p.Data.LocalStarId == envelope.StarId &&
                !p.Connection.Equals(conn)
            );

    }

    /// <summary>
    ///     Re-enqueue every blob an envelope carried. The count is bounded by the packet itself, so a
    ///     peer cannot make the host allocate or loop past the envelope ceiling.
    /// </summary>
    private static void ForwardInnerPackets(byte[][] packetObjects, NebulaeConnection conn)
    {
        if (packetObjects == null || packetObjects.Length == 0 || packetObjects.Length > StarBroadcastPacket.MaxPacketObjects)
        {
            Log.Warn($"Dropping star envelope with {packetObjects?.Length ?? 0} inner packets.");
            return;
        }
        foreach (var packetObject in packetObjects)
        {
            if (packetObject == null || packetObject.Length == 0)
            {
                continue;
            }
            ((NebulaeModel.Networking.Serialization.NebulaeNetPacketProcessor)Multiplayer.Session.Network.PacketProcessor)
                .DispatchInline(packetObject, conn);
        }
    }
}
