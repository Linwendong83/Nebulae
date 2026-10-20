namespace NebulaeModel.Packets.Universe;

[NebulaeModel.Networking.PacketId(6487652162679402960UL)]
internal class PlanetMemoUpdatePacket
{
    public PlanetMemoUpdatePacket() { }

    public PlanetMemoUpdatePacket(int planetId, string content, bool hasReminder, short[] colorData = null)
    {
        PlanetId = planetId;
        Content = content;
        HasReminder = hasReminder;
        ColorData = colorData;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public string Content { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public bool HasReminder { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public short[] ColorData { get; set; }
}
