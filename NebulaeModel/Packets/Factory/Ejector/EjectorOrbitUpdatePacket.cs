namespace NebulaeModel.Packets.Factory.Ejector;

[NebulaeModel.Networking.PacketId(9742696380803675641UL)]
internal class EjectorOrbitUpdatePacket
{
    public EjectorOrbitUpdatePacket() { }

    public EjectorOrbitUpdatePacket(int ejectorIndex, int newOrbitIndex, int planetId)
    {
        EjectorIndex = ejectorIndex;
        NewOrbitIndex = newOrbitIndex;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int EjectorIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int NewOrbitIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int PlanetId { get; set; }
}
