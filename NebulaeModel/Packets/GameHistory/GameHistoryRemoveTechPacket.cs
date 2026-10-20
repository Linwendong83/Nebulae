namespace NebulaeModel.Packets.GameHistory;

[NebulaeModel.Networking.PacketId(3446153762755801376UL)]
internal class GameHistoryRemoveTechPacket
{
    public GameHistoryRemoveTechPacket() { }

    public GameHistoryRemoveTechPacket(int techId)
    {
        TechId = techId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int TechId { get; set; }
}
