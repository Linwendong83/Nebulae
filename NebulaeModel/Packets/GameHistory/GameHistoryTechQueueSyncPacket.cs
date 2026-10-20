using System;

namespace NebulaeModel.Packets.GameHistory;

[NebulaeModel.Networking.PacketId(2190311157592356816UL)]
internal class GameHistoryTechQueueSyncPacket
{
    public GameHistoryTechQueueSyncPacket() { }

    public GameHistoryTechQueueSyncPacket(bool isRequest)
    {
        IsRequest = isRequest;
        TechQueue = Array.Empty<int>();
    }

    public GameHistoryTechQueueSyncPacket(int[] techQueue)
    {
        IsRequest = false;
        TechQueue = techQueue;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int[] TechQueue { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public bool IsRequest { get; set; }
}
