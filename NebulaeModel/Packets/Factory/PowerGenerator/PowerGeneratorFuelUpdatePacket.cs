namespace NebulaeModel.Packets.Factory.PowerGenerator;

[NebulaeModel.Networking.PacketId(10108645703091614381UL)]
internal class PowerGeneratorFuelUpdatePacket
{
    public PowerGeneratorFuelUpdatePacket() { }

    public PowerGeneratorFuelUpdatePacket(int powerGeneratorIndex, int fuelId, short fuelAmount, short fuelInc, int planetId)
    {
        PowerGeneratorIndex = powerGeneratorIndex;
        FuelId = fuelId;
        FuelAmount = fuelAmount;
        FuelInc = fuelInc;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PowerGeneratorIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int FuelId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public short FuelAmount { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public short FuelInc { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int PlanetId { get; set; }
}
