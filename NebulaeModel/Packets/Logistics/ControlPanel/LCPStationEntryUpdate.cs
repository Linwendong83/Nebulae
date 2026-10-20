using UnityEngine;

namespace NebulaeModel.Packets.Logistics.ControlPanel;

[NebulaeModel.Networking.PacketId(5136588810799157272UL)]
internal class LCPStationEntryUpdate
{
    public LCPStationEntryUpdate() { }

    public void Set(int index, StationComponent station, PlanetFactory factory)
    {
        Index = index;

        if (!station.isCollector)
        {
            var powerSystem = factory.powerSystem;
            var powerConsumerComponent = powerSystem.consumerPool[station.pcId];
            var networkId = powerConsumerComponent.networkId;
            var powerNetwork = powerSystem.netPool[networkId];
            ConsumerRatio = (float)powerNetwork.consumerRatio;
            RequirePower = (long)((powerConsumerComponent.requiredEnergy * 60L) * ConsumerRatio);
            WorkEnergyPerTick = powerSystem.consumerPool[station.pcId].workEnergyPerTick;
            PowerRound = station.energy / (float)station.energyMax;
        }
        else
        {
            ConsumerRatio = 1.0f;
            RequirePower = 0;
            WorkEnergyPerTick = 0;
            PowerRound = 0;
        }

        IdleDroneCount = (short)station.idleDroneCount;
        WorkDroneCount = (short)station.workDroneCount;
        IdleShipCount = (short)station.idleShipCount;
        WorkShipCount = (short)station.workShipCount;
        WarperCount = (short)station.warperCount;

        if (ItemId == null)
        {
            ItemId = new int[5];
            ItemCount = new int[5];
            LocalOrder = new short[5];
            RemoteOrder = new short[5];
            StoreMax = new int[5];
            LocalLogic = new byte[5];
            RemoteLogic = new byte[5];
        }
        var i = 0;
        if (station.storage != null)
        {
            var storeLength = station.storage.Length;
            if (storeLength > 5) storeLength = 5;
            for (; i < storeLength; i++)
            {
                ref var store = ref station.storage[i];
                ItemId[i] = store.itemId;
                ItemCount[i] = store.count;
                LocalOrder[i] = (short)Mathf.Clamp(store.localOrder, -20000, 20000);
                RemoteOrder[i] = (short)Mathf.Clamp(store.remoteOrder, -20000, 20000);
                StoreMax[i] = store.max;
                LocalLogic[i] = (byte)store.localLogic;
                RemoteLogic[i] = (byte)store.remoteLogic;
            }
        }
        for (; i < 5; i++)
        {
            ItemId[i] = 0;
            ItemCount[i] = 0;
        }
    }

    public static readonly LCPStationEntryUpdate Instance = new();

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
    public short IdleDroneCount { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public short WorkDroneCount { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public short IdleShipCount { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public short WorkShipCount { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public short WarperCount { get; set; }

    [NebulaeModel.Networking.WireOrder(10)]
    public int[] ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(11)]
    public int[] ItemCount { get; set; }
    [NebulaeModel.Networking.WireOrder(12)]
    public short[] LocalOrder { get; set; }
    [NebulaeModel.Networking.WireOrder(13)]
    public short[] RemoteOrder { get; set; }
    [NebulaeModel.Networking.WireOrder(14)]
    public int[] StoreMax { get; set; }
    [NebulaeModel.Networking.WireOrder(15)]
    public byte[] LocalLogic { get; set; }
    [NebulaeModel.Networking.WireOrder(16)]
    public byte[] RemoteLogic { get; set; }
    [NebulaeModel.Networking.WireOrder(17)] public int Generation { get; set; }
    [NebulaeModel.Networking.WireOrder(18)] public int AstroId { get; set; }
    [NebulaeModel.Networking.WireOrder(19)] public int ObjId { get; set; }
}
