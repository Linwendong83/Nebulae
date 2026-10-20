namespace NebulaeModel.Packets.GameHistory;

[NebulaeModel.Networking.PacketId(292268225779324980UL)]
internal class GameHistoryUnlockTechPacket
{
    public GameHistoryUnlockTechPacket() { }

    public GameHistoryUnlockTechPacket(int techId, int level)
    {
        TechId = techId;
        Level = level;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int TechId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int Level { get; set; }
}
