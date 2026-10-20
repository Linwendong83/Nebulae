namespace NebulaeModel.Packets.Factory.Belt;

[NebulaeModel.Networking.PacketId(16365180034937673207UL)]
internal class BeltSignalNumberPacket
{
    public BeltSignalNumberPacket() { }

    public BeltSignalNumberPacket(int entityId, float number, int planetId)
    {
        EntityId = entityId;
        Number = number;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int EntityId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public float Number { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int PlanetId { get; set; }
}
