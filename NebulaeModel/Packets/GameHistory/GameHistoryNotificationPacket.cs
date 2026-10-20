namespace NebulaeModel.Packets.GameHistory;

[NebulaeModel.Networking.PacketId(1144316716150544123UL)]
internal class GameHistoryNotificationPacket
{
    public GameHistoryNotificationPacket() { }

    public GameHistoryNotificationPacket(GameHistoryEvent Event)
    {
        this.Event = Event;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public GameHistoryEvent Event { get; set; }
}

internal enum GameHistoryEvent
{
    PauseQueue = 1,
    ResumeQueue = 2,
    OneKeyUnlock = 3
}
