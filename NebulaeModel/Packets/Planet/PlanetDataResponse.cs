namespace NebulaeModel.Packets.Planet;

[NebulaeModel.Networking.PacketId(12416082776530172183UL)]
internal class PlanetDataResponse
{
    public PlanetDataResponse() { }

    public PlanetDataResponse(int planetId, byte[] planetData)
    {
        PlanetDataID = planetId;
        PlanetDataByte = planetData;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetDataID { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public byte[] PlanetDataByte { get; set; }
}
