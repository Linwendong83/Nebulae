namespace NebulaeModel.Packets.Planet;

[NebulaeModel.Networking.PacketId(3312890902060737998UL)]
internal class VegeAddPacket : NebulaeModel.Networking.IVegetationOperation
{
    public VegeAddPacket() { }

    public VegeAddPacket(int planetId, bool isVein, byte[] data, bool isPlanting = false)
    {
        PlanetId = planetId;
        IsVein = isVein;
        Data = data;
        IsPlanting = isPlanting;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public bool IsVein { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public byte[] Data { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public bool IsPlanting { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public bool IsRejected { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public long CommandSequence { get; set; }
}
