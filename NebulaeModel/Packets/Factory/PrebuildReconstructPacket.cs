namespace NebulaeModel.Packets.Factory;

[NebulaeModel.Networking.PacketId(6326903031990402289UL)]
internal class PrebuildReconstructPacket
{
    public PrebuildReconstructPacket() { }

    public PrebuildReconstructPacket(int planetId, int prebuildId)
    {
        PlanetId = planetId;
        PrebuildId = prebuildId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PrebuildId { get; set; }
}
