namespace NebulaeModel.Packets.Factory;

[NebulaeModel.Networking.PacketId(2095852940987933438UL)]
internal class PrebuildItemRequiredUpdate
{
    public PrebuildItemRequiredUpdate() { }

    public PrebuildItemRequiredUpdate(int planetId, int prebuildId, int itemCount)
    {
        PlanetId = planetId;
        PrebuildId = prebuildId;
        ItemCount = itemCount;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int PrebuildId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int ItemCount { get; set; }
}
