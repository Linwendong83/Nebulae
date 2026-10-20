#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Routers;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Routers;
internal class PlanetBroadcastProcessor : PacketProcessor<PlanetBroadcastPacket>
{
    protected override void ProcessPacket(PlanetBroadcastPacket packet, NebulaeConnection conn)
    {
        var envelope = new PlanetBroadcastPacket(packet.PacketObjects, packet.PlanetId);
        //Forward packet data to be processed
        var packetObjects = envelope.PacketObjects;
        if (packetObjects == null || packetObjects.Length == 0 ||
            packetObjects.Length > PlanetBroadcastPacket.MaxPacketObjects)
        {
            NebulaeModel.Logger.Log.Warn(
                $"Dropping planet envelope with {packetObjects?.Length ?? 0} inner packets.");
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
        if (IsHost && ((NebulaeModel.Networking.Serialization.NebulaeNetPacketProcessor)Multiplayer.Session.Network.PacketProcessor).DispatchDepth <= 1)
            Multiplayer.Session.Server.SendToMatching(envelope, p =>
                p.Data.LocalPlanetId == envelope.PlanetId && !p.Connection.Equals(conn));
    }
}
