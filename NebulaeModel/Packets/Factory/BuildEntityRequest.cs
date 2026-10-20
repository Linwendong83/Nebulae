namespace NebulaeModel.Packets.Factory;

[NebulaeModel.Networking.PacketId(15220517484467970874UL)]
internal class BuildEntityRequest
{
    public BuildEntityRequest() { }

    public BuildEntityRequest(int planetId, int prebuildId, int authorId, int entityId)
    {
        PlanetId = planetId;
        PrebuildId = prebuildId;
        AuthorId = authorId;
        EntityId = entityId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PrebuildId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int AuthorId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int EntityId { get; set; }
}
