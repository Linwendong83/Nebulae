namespace NebulaeModel.Packets.Universe;

[NebulaeModel.Networking.PacketId(7366345785457837012UL)]
internal class DysonSphereLoadRequest
{
    public DysonSphereLoadRequest() { }

    public DysonSphereLoadRequest(int starIndex, DysonSphereRequestEvent requestEvent)
    {
        StarIndex = starIndex;
        Event = requestEvent;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public DysonSphereRequestEvent Event { get; set; }
}

internal enum DysonSphereRequestEvent
{
    List = 1,
    Load = 2,
    Unload = 3,
    Query = 4
}
