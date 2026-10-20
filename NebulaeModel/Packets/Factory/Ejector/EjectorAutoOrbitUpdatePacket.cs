namespace NebulaeModel.Packets.Factory.Ejector;

[NebulaeModel.Networking.PacketId(6387955561608644636UL)]
internal class EjectorAutoOrbitUpdatePacket
{
    public EjectorAutoOrbitUpdatePacket() { }

    public EjectorAutoOrbitUpdatePacket(int ejectorIndex, bool autoOrbit, int planetId)
    {
        EjectorIndex = ejectorIndex;
        AutoOrbit = autoOrbit;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int EjectorIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public bool AutoOrbit { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int PlanetId { get; set; }
}
