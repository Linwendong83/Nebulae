namespace NebulaeModel.Packets.Factory;

[NebulaeModel.Networking.PacketId(15220517484467970874UL)]
internal class BuildEntityRequest : NebulaeModel.Networking.IVegetationOperation
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
    [NebulaeModel.Networking.WireOrder(4)]
    public byte[] CollectionBefore { get; set; } = System.Array.Empty<byte>();
    [NebulaeModel.Networking.WireOrder(5)]
    public long CommandSequence { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public long InstanceGeneration { get; set; }
}
