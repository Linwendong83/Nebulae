namespace NebulaeModel.Packets.Factory.PowerGenerator;

[NebulaeModel.Networking.PacketId(6197877420658645658UL)]
internal class PowerGeneratorProductUpdatePacket
{
    public PowerGeneratorProductUpdatePacket() { }

    public PowerGeneratorProductUpdatePacket(in PowerGeneratorComponent powerGenerator, int planetId)
    {
        PowerGeneratorIndex = powerGenerator.id;
        ProductCount = powerGenerator.productCount;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PowerGeneratorIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public float ProductCount { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int PlanetId { get; set; }
}
