namespace NebulaeModel.Packets.Factory.BattleBase;

[NebulaeModel.Networking.PacketId(28528779569375467UL)]
internal class BattleBaseSettingUpdatePacket
{
    public BattleBaseSettingUpdatePacket() { }

    public BattleBaseSettingUpdatePacket(int planetId, int battleBaseId, BattleBaseSettingEvent settingEvent, float arg1)
    {
        PlanetId = planetId;
        BattleBaseId = battleBaseId;
        Event = settingEvent;
        Arg1 = arg1;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int BattleBaseId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public BattleBaseSettingEvent Event { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public float Arg1 { get; set; }
}

internal enum BattleBaseSettingEvent
{
    None = 0,
    ChangeMaxChargePower = 1, //OnMaxChargePowerSliderChange
    ToggleDroneEnabled = 2, //OnDroneButtonClick
    ChangeDronesPriority = 3, //OnDroneButtonClick
    ToggleCombatModuleEnabled = 4, //OnFleetButtonClick
    ToggleAutoReconstruct = 5, //OnAutoReconstructButtonClick
    ToggleAutoPickEnabled = 6, //OnAutoPickButtonClick
    ChangeFleetConfig = 7, //OnFleetTypeButtonClick
    ToggleAutoReplenishFleet = 8 //OnAutoReplenishButtonClick
}
