using NebulaeModel.Networking;

namespace NebulaeModel.Packets.Logistics;

/// <summary>Ordered departures, returns and shipping decisions; movement stays local.</summary>
[PacketId(17354062874130052291UL)]
internal class ILSShipLifecyclePacket
{
    // Records start with a kind and the physical ship slot. Array positions are never identities.
    public const int Depart = 1, Return = 2, Facts = 3;
    public const int DepartureSize = 9;
    public const int MaxRecordSize = 12;
    public const int MaxChanges = 256;

    [WireOrder(0)] public int GId { get; set; }
    [WireOrder(1)] public int PlanetId { get; set; }
    [WireOrder(2)] public long PreviousRevision { get; set; }
    [WireOrder(3)] public long Revision { get; set; }
    [WireOrder(4)] public int[] Changes { get; set; }
    [WireOrder(5)] public int WorkShipCount { get; set; }
    [WireOrder(6)] public int IdleShipCount { get; set; }
    [WireOrder(7)] public ulong WorkShipIndices { get; set; }
    [WireOrder(8)] public ulong IdleShipIndices { get; set; }
    [WireOrder(9)] public long Energy { get; set; }
    [WireOrder(10)] public int WarperCount { get; set; }
    [WireOrder(11)] public double WarpEnableDistance { get; set; }
}
