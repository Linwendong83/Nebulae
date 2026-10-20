#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Session;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Session;
internal class PlayerDisconnectedProcessor : PacketProcessor<PlayerDisconnected>
{
    protected override void ProcessPacket(PlayerDisconnected packet, NebulaeConnection conn)
    {
        Multiplayer.Session.NumPlayers = packet.NumPlayers;
        Multiplayer.Session.World.DestroyRemotePlayerModel(packet.PlayerId, true);
        Multiplayer.Session.PowerTowers.RemovePlayer(packet.PlayerId);
        Multiplayer.Session.Life.Remove(packet.PlayerId);
    }
}
