namespace NebulaeModel.Packets.Universe;

[NebulaeModel.Networking.PacketId(12085776575251347968UL)]
internal class MarkerSettingUpdatePacket
{
    public MarkerSettingUpdatePacket() { }

    public MarkerSettingUpdatePacket(int planetId, int markerId, MarkerSettingEvent settingEvent, int intValue = 0,
        float floatValue = 0f, string stringValue = null, short[] colorData = null)
    {
        PlanetId = planetId;
        MarkerId = markerId;
        Event = settingEvent;
        IntValue = intValue;
        FloatValue = floatValue;
        StringValue = stringValue;
        ColorData = colorData;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int MarkerId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public MarkerSettingEvent Event { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int IntValue { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public float FloatValue { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public string StringValue { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public short[] ColorData { get; set; }
}

internal enum MarkerSettingEvent
{
    SetName = 0,
    SetWord = 1,
    SetTags = 2,
    SetTodoContent = 3,
    SetIcon = 4,
    SetColor = 5,
    SetVisibility = 6,
    SetDetailLevel = 7,
    SetHeight = 8,
    SetRadius = 9,
    SetDigitalSignalId = 10,
    SetDFAttractionFlags = 11
}
