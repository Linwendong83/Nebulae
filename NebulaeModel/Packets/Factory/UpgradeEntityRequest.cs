namespace NebulaeModel.Packets.Factory;

[NebulaeModel.Networking.PacketId(8021830597503869650UL)]
internal class UpgradeEntityRequest
{
    public UpgradeEntityRequest() { }

    public UpgradeEntityRequest(int planetId, int objId, int originProtoId, int upgradeProtoId, int authorId)
    {
        PlanetId = planetId;
        ObjId = objId;
        OriginProtoId = originProtoId;
        UpgradeProtoId = upgradeProtoId;
        AuthorId = authorId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int ObjId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int OriginProtoId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int UpgradeProtoId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int AuthorId { get; set; }
}
