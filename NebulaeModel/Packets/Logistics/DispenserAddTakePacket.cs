namespace NebulaeModel.Packets.Logistics;

[NebulaeModel.Networking.PacketId(17740898655026957540UL)]
internal class DispenserAddTakePacket
{
    public DispenserAddTakePacket() { }

    public DispenserAddTakePacket(int planetId, int entityId, EDispenserAddTakeEvent addTakeEvent, int itemId, int itemCount,
        int itemInc)
    {
        PlanetId = planetId;
        EntityId = entityId;
        AddTakeEvent = addTakeEvent;
        ItemId = itemId;
        ItemCount = itemCount;
        ItemInc = itemInc;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int EntityId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public EDispenserAddTakeEvent AddTakeEvent { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int ItemCount { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int ItemInc { get; set; }
}

internal enum EDispenserAddTakeEvent
{
    None,
    ManualAdd,
    ManualTake,
    CourierAdd,
    CourierTake
}
