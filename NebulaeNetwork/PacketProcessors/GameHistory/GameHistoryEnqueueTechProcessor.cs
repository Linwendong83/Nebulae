
#region
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameHistory;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.GameHistory;
internal class GameHistoryEnqueueTechProcessor : PacketProcessor<GameHistoryEnqueueTechPacket>
{
    protected override void ProcessPacket(GameHistoryEnqueueTechPacket packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            using (Multiplayer.Session.History.IsIncomingRequest.On())
            {
                GameMain.history.EnqueueTech(packet.TechId);
            }
            Server.SendPacketExclude(packet, conn);
        }
        else
        {
            using (Multiplayer.Session.History.IsIncomingRequest.On())
            {
                GameMain.history.EnqueueTech(packet.TechId);
            }
        }
    }
}
