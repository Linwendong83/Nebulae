namespace NebulaeModel.Packets.Factory.Tank;

[NebulaeModel.Networking.PacketId(17394654153042916753UL)]
internal class TankInputOutputSwitchPacket
{
    public TankInputOutputSwitchPacket() { }

    public TankInputOutputSwitchPacket(int tankIndex, bool isInput, bool inClosed, int planetId)
    {
        TankIndex = tankIndex;
        IsInput = isInput;
        IsClosed = inClosed;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int TankIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public bool IsInput { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public bool IsClosed { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int PlanetId { get; set; }
}
