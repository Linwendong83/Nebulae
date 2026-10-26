#region

using NebulaeModel.Networking;
using NebulaeModel.DataStructures.Chat;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Combat;
using NebulaeWorld;
using NebulaeWorld.MonoBehaviours.Local.Chat;

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

        var userName = "";
        using (Multiplayer.Session.World.GetRemotePlayersModels(out var remotePlayersModels))
        {
            if (remotePlayersModels.TryGetValue(packet.PlayerId, out var player))
            {
                userName = player.Username;
            }
        }
        var second = (int)(GameMain.history.dfTruceTimer / 60L);
        var minute = second / 60;
        var hour = minute / 60;
        var message = string.Format("{0} set truce time to {1:D2}:{2:D2}:{3:D2}".Translate(),
            userName, hour, minute % 60, second % 60);
        ChatManager.Instance.SendChatMessage(message, ChatMessageType.BattleMessage);
    }
}
