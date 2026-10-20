namespace NebulaeModel.Packets.Universe;

[NebulaeModel.Networking.PacketId(1657361759075504211UL)]
internal class DysonSphereData
{
    public DysonSphereData() { }

    public DysonSphereData(int starIndex, byte[] data, DysonSphereRespondEvent respondEvent)
    {
        StarIndex = starIndex;
        BinaryData = data;
        Event = respondEvent;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public byte[] BinaryData { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public DysonSphereRespondEvent Event { get; set; }
}

internal enum DysonSphereRespondEvent
{
    List = 1,
    Load = 2,
    Desync = 3
}
