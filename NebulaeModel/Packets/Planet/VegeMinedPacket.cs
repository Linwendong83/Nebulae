namespace NebulaeModel.Packets.Planet;

[NebulaeModel.Networking.PacketId(10475894585919202692UL)]
internal class VegeMinedPacket : NebulaeModel.Networking.IVegetationOperation
{
    public VegeMinedPacket() { }

    public VegeMinedPacket(int planetId, int vegeId, int amount, bool isVein, bool isCollected = false)
    {
        PlanetId = planetId;
        VegeId = vegeId;
        Amount = amount;
        IsVein = isVein;
        IsCollected = isCollected;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int VegeId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int Amount { get; set; } // the current amount, if 0 remove vege
    [NebulaeModel.Networking.WireOrder(3)]
    public bool IsVein { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public bool IsCollected { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public long CommandSequence { get; set; }
}
