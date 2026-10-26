#region

using NebulaeModel.Networking;
using NebulaeModel.Logger;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Chat;
using NebulaeWorld.Chat;

#endregion

namespace NebulaeNetwork.PacketProcessors.Chat;
internal class NewChatMessageProcessor : PacketProcessor<NewChatMessagePacket>
{
    protected override void ProcessPacket(NewChatMessagePacket packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            Server.SendPacketExclude(packet, conn);
        }

        if (packet.MessageType == NebulaeModel.DataStructures.Chat.ChatMessageType.SystemWarnMessage)
        {
            Log.Warn(packet.MessageText); // Record system warn message in log file
        }

        ChatService.Instance.AddMessage(packet.MessageText, packet.MessageType, packet.UserName);
    }
}
