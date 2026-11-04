using HarmonyLib;
using NebulaeModel.Contracts;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Factory.Storage;
using NebulaeModel.Packets.Logistics;
using NebulaePatcher.Patches.Transpilers;
using NebulaeWorld;
using UnityEngine.EventSystems;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class DispenserInventoryTransferTest
{
    private MultiplayerSession previousSession = null!;
    private bool previousLoaded;
    private int[] previousStacks = null!;
    private RecordingNetwork network = null!;

    [TestInitialize]
    public void Initialize()
    {
        previousSession = Multiplayer.Session;
        previousLoaded = StorageComponent.staticLoaded;
        previousStacks = StorageComponent.itemStackCount;
        StorageComponent.staticLoaded = true;
        StorageComponent.itemStackCount = Enumerable.Repeat(100, 12000).ToArray();
        network = new RecordingNetwork();
        Multiplayer.Session = Uninitialized<MultiplayerSession>();
        Multiplayer.Session.Network = network;
    }

    [TestCleanup]
    public void Cleanup()
    {
        Multiplayer.Session = previousSession;
        StorageComponent.staticLoaded = previousLoaded;
        StorageComponent.itemStackCount = previousStacks;
    }

    private static PlanetFactory Factory(int count, int inc)
    {
        var factory = Uninitialized<PlanetFactory>();
        SetBackingField(factory, nameof(PlanetFactory.planet), new PlanetData { id = 101, star = new StarData { id = 1 } });
        factory.entityPool = [default, new EntityData { id = 1, storageId = 1 }];
        factory.entityMutexs = [new global::Mutex(), new global::Mutex()];
        factory.factoryStorage = Uninitialized<FactoryStorage>();
        var storage = new StorageComponent(1) { id = 1, entityId = 1 };
        storage.grids[0] = new StorageComponent.GRID { itemId = 1601, count = count, inc = inc, stackSize = 100 };
        storage.bottomStorage = storage.topStorage = storage;
        factory.factoryStorage.storagePool = [null!, storage];
        return factory;
    }

    private static StorageComponent Storage(PlanetFactory factory) => factory.factoryStorage.storagePool[1];

    [TestMethod]
    [DataRow(90, 10, 27)]
    [DataRow(100, 0, 47)]
    public void ManualDepositBroadcastsOnlyTransferredIncrement(int initial, int moved, int remainingInc)
    {
        var factory = Factory(initial, initial * 2);
        var player = Uninitialized<Player>();
        player.SetHandItemId_Unsafe(1601);
        player.SetHandItemCount_Unsafe(20);
        player.SetHandItemInc_Unsafe(47);
        var ui = Uninitialized<UIDispenserWindow>();
        ui.factory = factory; ui.player = player;
        ui.transport = Uninitialized<PlanetTransport>();
        ui.transport.dispenserPool = [null!, new DispenserComponent { id = 1, filter = 1601, storage = Storage(factory) }];
        SetField(ui, "_dispenserId", 1);
        var click = Uninitialized<PointerEventData>();
        click.button = PointerEventData.InputButton.Left;

        TestAssert.IsFalse(UIDispenserWindow_Patch.OnItemIconMouseDown_Prefix(ui, click));

        TestAssert.AreEqual(20 - moved, player.inhandItemCount);
        TestAssert.AreEqual(remainingInc, player.inhandItemInc);
        VerifyDeposit(factory, initial, moved, 47 - remainingInc, EDispenserAddTakeEvent.ManualAdd);
    }

    [TestMethod]
    [DataRow(90, 10, 27)]
    [DataRow(100, 0, 47)]
    public void CourierDepositBroadcastsOnlyCommittedItems(int initial, int moved, int remainingInc)
    {
        var factory = Factory(initial, initial * 2);
        TestAssert.AreEqual(moved, DispenserComponent_Transpiler.InsertIntoStorage(factory, 1, 1601, 20, 47, out var inc, false));
        TestAssert.AreEqual(remainingInc, inc);
        VerifyDeposit(factory, initial, moved, 47 - remainingInc, EDispenserAddTakeEvent.CourierAdd);
    }

    private void VerifyDeposit(PlanetFactory factory, int initial, int moved, int inc, EDispenserAddTakeEvent kind)
    {
        TestAssert.AreEqual(initial + moved, Storage(factory).GetItemCount(1601));
        TestAssert.AreEqual(initial * 2 + inc, Storage(factory).grids[0].inc);
        if (moved == 0) { TestAssert.IsEmpty(network.Packets); return; }
        var packet = (DispenserAddTakePacket)network.Packets.Single();
        TestAssert.AreEqual(kind, packet.AddTakeEvent);
        TestAssert.AreEqual(moved, packet.ItemCount);
        TestAssert.AreEqual(inc, packet.ItemInc);
        // A receiver with more free space must gain only what actually left the sender.
        var replica = Factory(0, 0);
        replica.InsertIntoStorage(1, packet.ItemId, packet.ItemCount, packet.ItemInc, out _, false);
        TestAssert.AreEqual(moved, Storage(replica).GetItemCount(1601));
        TestAssert.AreEqual(inc, Storage(replica).grids[0].inc);
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(0)]
    public void CourierPickupBroadcastsOnlyWhatWasAvailable(int available)
    {
        var factory = Factory(available, available * 2);
        TestAssert.AreEqual(available, DispenserComponent_Transpiler.PickFromStorage(factory, 1, 1601, 20, out var inc));
        TestAssert.AreEqual(available * 2, inc);
        if (available == 0) { TestAssert.IsEmpty(network.Packets); return; }
        var packet = (DispenserAddTakePacket)network.Packets.Single();
        TestAssert.AreEqual(EDispenserAddTakeEvent.CourierTake, packet.AddTakeEvent);
        TestAssert.AreEqual(available, packet.ItemCount);
        var replica = Factory(30, 60);
        replica.PickFromStorage(1, packet.ItemId, packet.ItemCount, out _);
        TestAssert.AreEqual(30 - available, Storage(replica).GetItemCount(1601));
    }

    [TestMethod]
    public void BattleBasePickupBroadcastsTheIncrementThatEnteredStorage()
    {
        var factory = Factory(90, 180);
        var storage = Storage(factory);
        storage.type = EStorageType.Filtered;
        storage.grids[0].filter = 1601;
        object[] args = [storage, 1601, 20, 47, 0, false, factory];
        var moved = (int)AccessTools.Method(typeof(BattleBaseComponent_Transpiler), "AddItemFiltered").Invoke(null, args)!;
        TestAssert.AreEqual(10, moved);
        var packet = (StorageSyncRealtimeChangePacket)network.Packets.Single();
        TestAssert.AreEqual(moved, packet.Count);
        TestAssert.AreEqual(47 - (int)args[4], packet.Inc);
        TestAssert.AreEqual(20, packet.Inc);
    }

    private sealed class RecordingNetwork : INetworkProvider
    {
        public List<object> Packets { get; } = [];
        public INetPacketProcessor PacketProcessor => throw new NotSupportedException();
        public byte[] WriteOnce<T>(T packet) where T : class, new() => throw new NotSupportedException();
        public void SendPacket<T>(T packet) where T : class, new() => Packets.Add(packet);
        public void SendPacketToLocalStar<T>(T packet) where T : class, new() => Packets.Add(packet);
        public void SendPacketToStar<T>(T packet, int starId) where T : class, new() => Packets.Add(packet);
        public void SendToMatching<T>(T packet, Predicate<INebulaePlayer> condition) where T : class, new() => throw new NotSupportedException();
        public void SendPacketToLocalPlanet<T>(T packet) where T : class, new() => throw new NotSupportedException();
        public void SendPacketToPlanet<T>(T packet, int planetId) where T : class, new() => throw new NotSupportedException();
        public void SendPacketExclude<T>(T packet, INebulaeConnection exclude) where T : class, new() => throw new NotSupportedException();
        public void SendPacketToStarExclude<T>(T packet, int starId, INebulaeConnection exclude) where T : class, new() => throw new NotSupportedException();
        public void Dispose() { }
    }
}
