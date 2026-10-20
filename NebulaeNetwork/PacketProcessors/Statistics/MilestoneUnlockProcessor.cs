#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Statistics;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Statistics;
internal class MilestoneUnlockProcessor : PacketProcessor<MilestoneUnlockPacket>
{
    protected override void ProcessPacket(MilestoneUnlockPacket packet, NebulaeConnection conn)
    {
        var valid = true;

        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player != null)
            {
                Server.SendPacketExclude(packet, conn);
            }
            else
            {
                valid = false;
            }
        }

        if (!valid)
        {
            return;
        }
        using (Multiplayer.Session.Statistics.IsIncomingRequest.On())
        {
            if (!GameMain.data.milestoneSystem.milestoneDatas.TryGetValue(packet.Id, out var milestoneData))
            {
                return;
            }
            milestoneData.journalData.patternId = packet.PatternId;
            milestoneData.journalData.parameters = packet.Parameters;
            GameMain.data.milestoneSystem.UnlockMilestone(packet.Id, packet.UnlockTick);
        }
    }
}
