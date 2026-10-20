namespace NebulaeModel.Packets.Factory.Foundation;

[NebulaeModel.Networking.PacketId(5807290630840879386UL)]
internal class PlanetReformPacket
{
    public PlanetReformPacket() { }

    public PlanetReformPacket(int planetId, bool isReform, int type = 0, int color = 0, bool bury = false)
    {
        PlanetId = planetId;
        IsReform = isReform;
        Type = type;
        Color = color;
        Bury = bury;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public bool IsReform { get; set; } // true = reform all, false = revert
    [NebulaeModel.Networking.WireOrder(2)]
    public int Type { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int Color { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public bool Bury { get; set; }
}
