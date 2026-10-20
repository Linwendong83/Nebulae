namespace NebulaeModel.Packets.Logistics;

[NebulaeModel.Networking.PacketId(12082295647753290272UL)]
internal class RemoteOrderUpdate
{
    public RemoteOrderUpdate() { }

    public RemoteOrderUpdate(int stationGid, int[] remoteOrder)
    {
        StationGId = stationGid;
        RemoteOrder = remoteOrder;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StationGId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int[] RemoteOrder { get; set; }
}
