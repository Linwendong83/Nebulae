namespace NebulaeModel.Packets.Logistics;

[NebulaeModel.Networking.PacketId(225564716962308712UL)]
internal class DispenserSettingPacket
{
    public DispenserSettingPacket() { }

    public DispenserSettingPacket(int planetId, int dispenserId, EDispenserSettingEvent settingEvent, int parameter1)
    {
        PlanetId = planetId;
        DispenserId = dispenserId;
        Event = settingEvent;
        Parameter1 = parameter1;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int DispenserId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public EDispenserSettingEvent Event { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int Parameter1 { get; set; }
}

internal enum EDispenserSettingEvent
{
    None,
    SetCourierCount, // CourierIconClick
    ToggleAutoReplenish, // CourierAutoReplenishButtonClick
    SetMaxChargePower, // MaxChargePowerSliderValueChange
    SetFilter, // GuessFilterButtonClick, OnItemPickerReturn, OnTakeBackButtonClick
    SetPlayerDeliveryMode, // ModeSwitchClicked
    SetStorageDeliveryMode // ModeToggleClicked
}
