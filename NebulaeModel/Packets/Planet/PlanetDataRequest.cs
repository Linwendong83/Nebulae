namespace NebulaeModel.Packets.Planet;

[NebulaeModel.Networking.PacketId(832158411570307241UL)]
internal class PlanetDataRequest
{
    public PlanetDataRequest() { }

    public PlanetDataRequest(int[] planetIDs)
    {
        PlanetIDs = planetIDs;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int[] PlanetIDs { get; set; }
}
