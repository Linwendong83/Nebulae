namespace NebulaeModel.Packets.Planet;

[NebulaeModel.Networking.PacketId(3132663171410371942UL)]
internal class PlanetDetailRequest
{
    public PlanetDetailRequest() { }

    public PlanetDetailRequest(int planetID)
    {
        PlanetID = planetID;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetID { get; set; }
}
