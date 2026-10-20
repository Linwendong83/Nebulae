
namespace NebulaeModel.Packets.Logistics.ControlPanel;

[NebulaeModel.Networking.PacketId(11579399234579959560UL)]
internal class LCPStationNameSearchPacket
{
    public LCPStationNameSearchPacket() { }

    [NebulaeModel.Networking.WireOrder(0)]
    public string SearchString { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public bool IsExact { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int LocalPlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int[] ResultGids { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public string[] ResultNames { get; set; }
}
