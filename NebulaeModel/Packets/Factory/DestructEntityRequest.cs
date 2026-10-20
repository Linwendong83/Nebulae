namespace NebulaeModel.Packets.Factory;

[NebulaeModel.Networking.PacketId(6363043609523338168UL)]
internal class DestructEntityRequest
{
    public DestructEntityRequest() { }

    public DestructEntityRequest(int planetId, int objId, int protoId, int authorId)
    {
        AuthorId = authorId;
        PlanetId = planetId;
        ProtoId = protoId;
        ObjId = objId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int ObjId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int ProtoId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int AuthorId { get; set; }
}
