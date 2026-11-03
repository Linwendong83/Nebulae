#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Combat;

#endregion

namespace NebulaeNetwork.PacketProcessors.Combat;
internal class CombatTruceUpdateProcessor : PacketProcessor<CombatTruceUpdatePacket>
{
    protected override void ProcessPacket(CombatTruceUpdatePacket packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            return; // Clients request a transaction; this packet is an authoritative result.
        }

        var remaining = packet.TruceEndTime - GameMain.gameTick;
        // Ending a truce must not invoke OnDFTruce and reset hatred/attack waves again.
        if (remaining > 0)
            GameMain.history.AddTruceTime(remaining - GameMain.history.dfTruceTimer);
        else GameMain.history.dfTruceTimer = 0;

    }
}
