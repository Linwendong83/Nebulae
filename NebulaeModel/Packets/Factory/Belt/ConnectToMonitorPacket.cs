namespace NebulaeModel.Packets.Factory.Belt;

[NebulaeModel.Networking.PacketId(13569026351456125920UL)]
internal class ConnectToMonitorPacket
{
    public ConnectToMonitorPacket() { }

    public ConnectToMonitorPacket(int monitorId, int beltId, int offset, int planetId)
    {
        MonitorId = monitorId;
        BeltId = beltId;
        Offset = offset;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int MonitorId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int BeltId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int Offset { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int PlanetId { get; set; }
}
