namespace NebulaeModel.Packets.Factory.Tank;

[NebulaeModel.Networking.PacketId(17529776307770797032UL)]
internal class TankStorageUpdatePacket
{
    public TankStorageUpdatePacket() { }

    public TankStorageUpdatePacket(in TankComponent tankComponent, int planetId)
    {
        TankIndex = tankComponent.id;
        FluidId = tankComponent.fluidId;
        FluidCount = tankComponent.fluidCount;
        FluidInc = tankComponent.fluidInc;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int TankIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int FluidId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int FluidCount { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int FluidInc { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int PlanetId { get; set; }
}
