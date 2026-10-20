namespace NebulaeModel.Packets.Factory.PowerExchanger;

[NebulaeModel.Networking.PacketId(8073637478912031691UL)]
internal class PowerExchangerChangeModePacket
{
    public PowerExchangerChangeModePacket() { }

    public PowerExchangerChangeModePacket(int powerExchangerIndex, int mode, int planetId)
    {
        PowerExchangerIndex = powerExchangerIndex;
        Mode = mode;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PowerExchangerIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int Mode { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int PlanetId { get; set; }
}
