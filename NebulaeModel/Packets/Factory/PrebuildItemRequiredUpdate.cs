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
    [NebulaeModel.Networking.WireOrder(3)] public long Instance { get; set; }
    [NebulaeModel.Networking.WireOrder(4)] public long Sequence { get; set; }
    [NebulaeModel.Networking.WireOrder(5)] public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(6)] public int Inc { get; set; }
    [NebulaeModel.Networking.WireOrder(7)] public ushort Supplier { get; set; }
    [NebulaeModel.Networking.WireOrder(8)] public bool Committed { get; set; }
    [NebulaeModel.Networking.WireOrder(9)] public bool Accepted { get; set; }
    [NebulaeModel.Networking.WireOrder(10)] public int Required { get; set; } = -1;
}
