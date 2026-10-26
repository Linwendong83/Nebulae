using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Factory;
internal class PrebuildItemRequiredUpdateProcessor : PacketProcessor<PrebuildItemRequiredUpdate>
{
    protected override void ProcessPacket(PrebuildItemRequiredUpdate packet, NebulaeConnection conn)
    {
        var dispatch = Multiplayer.Session.BuildDispatch;
        if (!IsHost) { dispatch.ApplySupply(packet); return; }
        var player = Players.Get(conn);
        // A player may leave the planet after reserving material locally. Still return a
        // rejection receipt so that reservation can be refunded and its checkpoint released.
        if (player == null || !dispatch.AcceptSupply(player.Id, packet)) return;
        if (packet.Accepted) Multiplayer.Session.Network.SendPacketToStar(packet, GameMain.galaxy.PlanetById(packet.PlanetId).star.id);
        else conn.SendPacket(packet);
    }
}
