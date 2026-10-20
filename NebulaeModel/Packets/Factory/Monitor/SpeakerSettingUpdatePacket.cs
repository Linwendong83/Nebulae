namespace NebulaeModel.Packets.Factory.Monitor;

[NebulaeModel.Networking.PacketId(15757679823857761672UL)]
internal class SpeakerSettingUpdatePacket
{
    public SpeakerSettingUpdatePacket() { }

    public SpeakerSettingUpdatePacket(int planetId, int speakerId, SpeakerSettingEvent settingEvent, int parameter1,
        int parameter2 = 0)
    {
        PlanetId = planetId;
        SpeakerId = speakerId;
        Event = settingEvent;
        Parameter1 = parameter1;
        Parameter2 = parameter2;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int SpeakerId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public SpeakerSettingEvent Event { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int Parameter1 { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int Parameter2 { get; set; }
}

internal enum SpeakerSettingEvent
{
    SetTone = 0,
    SetVolume = 1,
    SetPitch = 2,
    SetLength = 3,
    SetRepeat = 4,
    SetFalloffRadius = 5
}
