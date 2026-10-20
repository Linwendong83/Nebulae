namespace NebulaeModel.Packets.Statistics;

[NebulaeModel.Networking.PacketId(11998666894613926809UL)]
internal class StatisticsRequestEvent
{
    public StatisticsRequestEvent() { }

    public StatisticsRequestEvent(StatisticEvent statisticEvent, int astroFilter)
    {
        Event = statisticEvent;
        AstroFilter = astroFilter;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public StatisticEvent Event { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int AstroFilter { get; set; }
}

internal enum StatisticEvent
{
    WindowOpened = 1,
    WindowClosed = 2,
    AstroFilterChanged = 3
}
