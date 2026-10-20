namespace NebulaeModel.Packets.Session;

[NebulaeModel.Networking.PacketId(17102765549700939046UL)]
internal class GlobalGameDataResponse
{
    public GlobalGameDataResponse() { }

    public GlobalGameDataResponse(EDataType dataType, byte[] binaryData)
    {
        DataType = dataType;
        BinaryData = binaryData;
    }

    internal enum EDataType : byte
    {
        History = 1,
        GalacticTransport,
        SpaceSector,
        MilestoneSystem,
        TrashSystem,
        GalacticDigital,
        Ready,
        Session,
        Goals,
        KillStatistics,
        PlanetBaseline
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public EDataType DataType { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public byte[] BinaryData { get; set; }
}
