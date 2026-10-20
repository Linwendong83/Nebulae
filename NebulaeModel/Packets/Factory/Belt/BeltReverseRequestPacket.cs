namespace NebulaeModel.Packets.Factory.Belt;

[NebulaeModel.Networking.PacketId(14345786831852627189UL)]
internal class BeltReverseRequestPacket
{
    public BeltReverseRequestPacket() { }

    public BeltReverseRequestPacket(int beltId, int planetId, int authorId)
    {
        BeltId = beltId;
        PlanetId = planetId;
        AuthorId = authorId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int BeltId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int AuthorId { get; set; }
}
