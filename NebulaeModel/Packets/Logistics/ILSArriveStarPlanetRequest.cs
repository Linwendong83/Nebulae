namespace NebulaeModel.Packets.Logistics;

[NebulaeModel.Networking.PacketId(1663804022296653269UL)]
internal class ILSArriveStarPlanetRequest
{
    public ILSArriveStarPlanetRequest() { }

    public ILSArriveStarPlanetRequest(int starId)
    {
        StarId = starId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int GId { get; set; }
}
