namespace NebulaeModel.Packets.Logistics;

// Sync route update events in GalacticTransport
[NebulaeModel.Networking.PacketId(6254754508330318489UL)]
internal class ILSUpdateRoute
{
    public ILSUpdateRoute() { }

    public ILSUpdateRoute(ERouteEvent type, int id0, int id1 = 0, int itemId = 0)
    {
        Type = type;
        Id0 = id0;
        Id1 = id1;
        ItemId = itemId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public ERouteEvent Type { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int Id0 { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int Id1 { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public bool Enable { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public string Comment { get; set; }

    internal enum ERouteEvent
    {
        None = 0,
        AddStation2StationRoute,
        RemoveStation2StationRoute_Single,
        RemoveStation2StationRoute_Pair,
        AddAstro2AstroRoute,
        RemoveAstro2AstroRoute,
        SetAstro2AstroRouteEnable,
        SetAstro2AstroRouteComment
    }
}
