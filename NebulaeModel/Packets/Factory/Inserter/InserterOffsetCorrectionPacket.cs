namespace NebulaeModel.Packets.Factory.Inserter;

[NebulaeModel.Networking.PacketId(4689914206505289187UL)]
internal class InserterOffsetCorrectionPacket
{
    public InserterOffsetCorrectionPacket() { }

    public InserterOffsetCorrectionPacket(int inserterId, short pickOffset, short insertOffset, int planetId)
    {
        InserterId = inserterId;
        PickOffset = pickOffset;
        InsertOffset = insertOffset;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int InserterId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public short PickOffset { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public short InsertOffset { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int PlanetId { get; set; }
}
