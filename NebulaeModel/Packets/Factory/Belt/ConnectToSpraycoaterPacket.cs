namespace NebulaeModel.Packets.Factory.Belt;

[NebulaeModel.Networking.PacketId(9890531936875537599UL)]
internal class ConnectToSpraycoaterPacket
{
    public ConnectToSpraycoaterPacket() { }

    public ConnectToSpraycoaterPacket(int spraycoaterId, int cargoBeltId, int incBeltId, int planetId)
    {
        SpraycoaterId = spraycoaterId;
        CargoBeltId = cargoBeltId;
        IncBeltId = incBeltId;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int SpraycoaterId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int CargoBeltId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int IncBeltId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int PlanetId { get; set; }
}
