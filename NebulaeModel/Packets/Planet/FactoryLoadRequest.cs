namespace NebulaeModel.Packets.Planet;

[NebulaeModel.Networking.PacketId(4184407494928784265UL)]
internal class FactoryLoadRequest
{
    public FactoryLoadRequest() { }

    public FactoryLoadRequest(int planetID)
    {
        PlanetID = planetID;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetID { get; set; }
}
