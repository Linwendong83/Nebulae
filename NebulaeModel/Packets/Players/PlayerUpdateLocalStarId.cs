namespace NebulaeModel.Packets.Players;

[NebulaeModel.Networking.PacketId(14292707192860638088UL)]
internal class PlayerUpdateLocalStarId
{
    public PlayerUpdateLocalStarId() { }

    public PlayerUpdateLocalStarId(ushort playerId, int starId)
    {
        PlayerId = playerId;
        StarId = starId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int StarId { get; set; }
}
