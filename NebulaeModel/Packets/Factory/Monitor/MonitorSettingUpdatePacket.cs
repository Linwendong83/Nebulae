namespace NebulaeModel.Packets.Factory.Monitor;

[NebulaeModel.Networking.PacketId(16330450483657773357UL)]
internal class MonitorSettingUpdatePacket
{
    public MonitorSettingUpdatePacket() { }

    public MonitorSettingUpdatePacket(int planetId, int monitorId, MonitorSettingEvent settingEvent, int parameter1,
        int parameter2 = 0)
    {
        PlanetId = planetId;
        MonitorId = monitorId;
        Event = settingEvent;
        Parameter1 = parameter1;
        Parameter2 = parameter2;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int MonitorId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public MonitorSettingEvent Event { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int Parameter1 { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int Parameter2 { get; set; }
}

internal enum MonitorSettingEvent
{
    SetPassColorId = 0,
    SetFailColorId = 1,
    SetPassOperator = 2,
    SetMonitorMode = 3,
    SetSystemWarningMode = 4,
    SetSystemWarningSignalId = 5,
    SetDigitalSignalId = 6,
    SetCargoFilter = 7,
    SetTargetCargoBytes = 8,
    SetPeriodTickCount = 9,
    SetTargetBelt = 10,
    SetSpawnOperator = 11
}
