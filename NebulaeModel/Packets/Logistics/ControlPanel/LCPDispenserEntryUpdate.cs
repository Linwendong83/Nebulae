namespace NebulaeModel.Packets.Logistics.ControlPanel;

[NebulaeModel.Networking.PacketId(12487542104481880601UL)]
internal class LCPDispenserEntryUpdate
{
    public LCPDispenserEntryUpdate() { }

    public void Set(int index, DispenserComponent dispenser, PlanetFactory factory)
    {
        Index = index;

        var powerSystem = factory.powerSystem;
        var powerConsumerComponent = powerSystem.consumerPool[dispenser.pcId];
        var networkId = powerConsumerComponent.networkId;
        var powerNetwork = powerSystem.netPool[networkId];
        ConsumerRatio = (float)powerNetwork.consumerRatio;
        RequirePower = (long)((powerConsumerComponent.requiredEnergy * 60L) * ConsumerRatio);
        WorkEnergyPerTick = powerSystem.consumerPool[dispenser.pcId].workEnergyPerTick;
        PowerRound = dispenser.energy / (float)dispenser.energyMax;

        Filter = dispenser.filter;
        PlayerMode = (short)dispenser.playerMode;
        StorageMode = (short)dispenser.storageMode;
        IdleCourierCount = dispenser.idleCourierCount;
        WorkCourierCount = dispenser.workCourierCount;

        var status = 0;
        if (dispenser.playerMode == EPlayerDeliveryMode.None && dispenser.storageMode == EStorageDeliveryMode.None)
        {
            status = 1;
        }
        else if (dispenser.filter == 0)
        {
            status = 2;
        }
        else if (dispenser.idleCourierCount + dispenser.workCourierCount == 0)
        {
            status = 3;
        }
        else if (dispenser.holdupItemCount > 0)
        {
            status = 5;
        }
        else if (ConsumerRatio < 0.0001f && dispenser.energy < 100000L)
        {
            status = 4;
        }
        WarningFlag = status > 0;

        var count = 0;
        var inc = 0;
        var loop = 0;
        if (dispenser.storage != null && dispenser.filter > 0)
        {
            var storageComponent = dispenser.storage;
            do
            {
                count += storageComponent.GetItemCount(dispenser.filter, out var num);
                inc += num;
                storageComponent = storageComponent.previousStorage;
                if (loop++ > 20) break;
            }
            while (storageComponent != null);
        }
        ItemCount = count;
    }

    public static readonly LCPDispenserEntryUpdate Instance = new();

    [NebulaeModel.Networking.WireOrder(0)]
    public int Index { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public float ConsumerRatio { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public long RequirePower { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public long WorkEnergyPerTick { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public float PowerRound { get; set; }

    [NebulaeModel.Networking.WireOrder(5)]
    public int Filter { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public short PlayerMode { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public short StorageMode { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public int IdleCourierCount { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public int WorkCourierCount { get; set; }
    [NebulaeModel.Networking.WireOrder(10)]
    public bool WarningFlag { get; set; }
    [NebulaeModel.Networking.WireOrder(11)]
    public int ItemCount { get; set; }
    [NebulaeModel.Networking.WireOrder(12)] public int Generation { get; set; }
    [NebulaeModel.Networking.WireOrder(13)] public int AstroId { get; set; }
    [NebulaeModel.Networking.WireOrder(14)] public int ObjId { get; set; }
}
