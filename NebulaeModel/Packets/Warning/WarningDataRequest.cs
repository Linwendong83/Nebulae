namespace NebulaeModel.Packets.Warning;

[NebulaeModel.Networking.PacketId(11363113929243278341UL)]
internal class WarningDataRequest
{
    public WarningDataRequest() { }

    public WarningDataRequest(WarningRequestEvent requestEvent)
    {
        Event = requestEvent;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public WarningRequestEvent Event { get; set; }
}

internal enum WarningRequestEvent
{
    Signal = 0,
    Data = 1
}
