#region

using System;
using NebulaeModel.DataStructures.Chat;

#endregion

namespace NebulaeModel.Packets.Chat;

[NebulaeModel.Networking.PacketId(6118298594733643577UL)]
internal class NewChatMessagePacket
{
    public NewChatMessagePacket() { }

    public NewChatMessagePacket(ChatMessageType messageType, string messageText, DateTime sentAt = default, string userName = "")
    {
        MessageType = messageType;
        MessageText = messageText;
        SentAt = sentAt == default ? 0 : sentAt.ToBinary();
        UserName = userName;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public ChatMessageType MessageType { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public string MessageText { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public long SentAt { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public string UserName { get; set; }
}
