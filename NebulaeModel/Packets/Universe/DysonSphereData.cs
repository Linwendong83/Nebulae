namespace NebulaeModel.Packets.Universe;

[NebulaeModel.Networking.PacketId(1657361759075504211UL)]
internal class DysonSphereData
{
    public DysonSphereData() { }

    public DysonSphereData(int starIndex, byte[] data, DysonSphereRespondEvent respondEvent, int requestId = 0, long snapshotTick = 0)
    {
        StarIndex = starIndex;
        BinaryData = data;
        Event = respondEvent;
        RequestId = requestId;
        SnapshotTick = snapshotTick;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public byte[] BinaryData { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public DysonSphereRespondEvent Event { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int RequestId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public long SnapshotTick { get; set; }
}

internal enum DysonSphereRespondEvent
{
    List = 1,
    Load = 2,
    Desync = 3,
    Missing = 4
}
