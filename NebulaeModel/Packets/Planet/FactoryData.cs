namespace NebulaeModel.Packets.Planet;

[NebulaeModel.Networking.PacketId(5161751265371122428UL)]
internal class FactoryData
{
    public FactoryData() { }

    public FactoryData(int id, byte[] data, byte[] terrainModData)
    {
        PlanetId = id;
        BinaryData = data;
        TerrainModData = terrainModData;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public byte[] BinaryData { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public byte[] TerrainModData { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public byte[] EnemyGenerations { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public byte[] BuildAssignments { get; set; }
}
