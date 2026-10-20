namespace NebulaeModel.Packets.Factory.Belt;

[NebulaeModel.Networking.PacketId(597068113748533767UL)]
internal class BeltSignalIconPacket
{
    public BeltSignalIconPacket() { }

    public BeltSignalIconPacket(int entityId, int signalId, int planetId)
    {
        EntityId = entityId;
        SignalId = signalId;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int EntityId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int SignalId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int PlanetId { get; set; }
}
