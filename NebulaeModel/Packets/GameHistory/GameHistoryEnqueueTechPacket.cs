namespace NebulaeModel.Packets.GameHistory;

[NebulaeModel.Networking.PacketId(13051779530816418086UL)]
internal class GameHistoryEnqueueTechPacket
{
    public GameHistoryEnqueueTechPacket() { }

    public GameHistoryEnqueueTechPacket(int techId)
    {
        TechId = techId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int TechId { get; set; }
}
