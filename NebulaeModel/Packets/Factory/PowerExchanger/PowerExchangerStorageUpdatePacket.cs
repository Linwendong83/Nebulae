namespace NebulaeModel.Packets.Factory.PowerExchanger;

[NebulaeModel.Networking.PacketId(8760157090537783292UL)]
internal class PowerExchangerStorageUpdatePacket
{
    public PowerExchangerStorageUpdatePacket() { }

    public PowerExchangerStorageUpdatePacket(int powerExchangerIndex, short emptyAccumulatorCount, short fullAccumulatorCount,
        int planetId, int inc)
    {
        PowerExchangerIndex = powerExchangerIndex;
        EmptyAccumulatorCount = emptyAccumulatorCount;
        FullAccumulatorCount = fullAccumulatorCount;
        PlanetId = planetId;
        Inc = inc;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PowerExchangerIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public short EmptyAccumulatorCount { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public short FullAccumulatorCount { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int Inc { get; set; }
}
