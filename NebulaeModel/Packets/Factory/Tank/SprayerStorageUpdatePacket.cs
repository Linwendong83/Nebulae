namespace NebulaeModel.Packets.Factory.Tank;

[NebulaeModel.Networking.PacketId(7205722838587392426UL)]
internal class SprayerStorageUpdatePacket
{
    public SprayerStorageUpdatePacket() { }

    public SprayerStorageUpdatePacket(in SpraycoaterComponent spraycoater, int planetId)
    {
        SprayerIndex = spraycoater.id;
        IncItemId = spraycoater.incItemId;
        IncAbility = spraycoater.incAbility;
        IncSprayTimes = spraycoater.incSprayTimes;
        IncCount = spraycoater.incCount;
        ExtraIncCount = spraycoater.extraIncCount;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int SprayerIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int IncItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int IncAbility { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int IncSprayTimes { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int IncCount { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int ExtraIncCount { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public int PlanetId { get; set; }
}
