namespace NebulaeModel.Packets.Logistics;

[NebulaeModel.Networking.PacketId(1567686076597495569UL)]
internal class DispenserStorePacket
{
    public DispenserStorePacket() { }

    public DispenserStorePacket(int planetId, in DispenserComponent dispenser)
    {
        PlanetId = planetId;
        DispenserId = dispenser.id;
        HoldupItemCount = dispenser.holdupItemCount;
        ItemIds = new int[HoldupItemCount];
        Counts = new int[HoldupItemCount];
        Incs = new int[HoldupItemCount];
        for (var i = 0; i < HoldupItemCount; i++)
        {
            ItemIds[i] = dispenser.holdupPackage[i].itemId;
            Counts[i] = dispenser.holdupPackage[i].count;
            Incs[i] = dispenser.holdupPackage[i].inc;
        }
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int DispenserId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int HoldupItemCount { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int[] ItemIds { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int[] Counts { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int[] Incs { get; set; }
}
