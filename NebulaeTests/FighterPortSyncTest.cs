using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Factory.Assembler;
using NebulaeWorld.Combat;
using NebulaeWorld.Factory;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class FighterPortSyncTest
{
    private int[] previousStacks = null!;
    private bool previousLoaded;

    [TestInitialize]
    public void SetUp()
    {
        previousStacks = StorageComponent.itemStackCount;
        previousLoaded = StorageComponent.staticLoaded;
        StorageComponent.staticLoaded = true;
        StorageComponent.itemStackCount = new int[12000];
        StorageComponent.itemStackCount[1001] = StorageComponent.itemStackCount[1002] = 100;
    }
    [TestCleanup]
    public void TearDown()
    {
        StorageComponent.itemStackCount = previousStacks;
        StorageComponent.staticLoaded = previousLoaded;
    }
    private static bool Valid(int item, ECraftSize size, bool space) => item is 1001 or 1002;
    private static ModuleFleet Fleet(params int[] items) => new()
    {
        fighters = items.Select(item => new ModuleFighter { itemId = item, count = item > 0 ? 1 : 0 }).ToArray()
    };

    [TestMethod]
    public void LargeSpacePortsUseFourUnitsOfUnlockedCapacity()
    {
        var fleet = Fleet(0, 0, 0);
        fleet.fighters[0].size = fleet.fighters[2].size = ECraftSize.Small;
        fleet.fighters[1].size = ECraftSize.Large;
        TestAssert.AreEqual(1, FighterPortOperation.UnlockedPorts(fleet, 4, true));
        TestAssert.AreEqual(2, FighterPortOperation.UnlockedPorts(fleet, 5, true));
        TestAssert.AreEqual(3, FighterPortOperation.UnlockedPorts(fleet, 6, true));
    }

    [TestMethod]
    public void ControlDepositAndWithdrawalConserveEveryFighter()
    {
        var fleet = Fleet(0, 0, 0, 0);
        var hand = new MechaData { HandItem = 1001, HandCount = 3, HandInc = 6 };
        var stock = new StorageComponent(2); var bag = new StorageComponent(2);
        TestAssert.IsTrue(FighterPortOperation.TryApply(ref fleet, 4, 0, false, FighterPortOperation.Control, 0, stock, bag, hand, out var credits, Valid));
        TestAssert.AreEqual(3, fleet.fighters.Sum(f => f.count));
        TestAssert.AreEqual(0, hand.HandCount);
        TestAssert.IsEmpty(credits);
        TestAssert.IsTrue(FighterPortOperation.TryApply(ref fleet, 4, 0, false, FighterPortOperation.Control, 0, stock, bag, hand, out credits, Valid));
        TestAssert.AreEqual(0, fleet.fighters.Sum(f => f.count));
        TestAssert.AreEqual(3, hand.HandCount);
        TestAssert.AreEqual(1001, hand.HandItem);
        TestAssert.IsEmpty(credits);
    }

    [TestMethod]
    public void SwappingOneFighterReturnsThePreviousFighterToTheHand()
    {
        var fleet = Fleet(1001);
        var hand = new MechaData { HandItem = 1002, HandCount = 1 };
        TestAssert.IsTrue(FighterPortOperation.TryApply(ref fleet, 1, 0, false, 0, 0,
            new StorageComponent(1), new StorageComponent(1), hand, out _, Valid));
        TestAssert.AreEqual(1002, fleet.fighters[0].itemId);
        TestAssert.AreEqual(1, fleet.fighters[0].count);
        TestAssert.AreEqual(1001, hand.HandItem);
        TestAssert.AreEqual(1, hand.HandCount);
    }

    [TestMethod]
    public void DeployedLockedAndInvalidPortsCannotConsumeTheHand()
    {
        var fleet = Fleet(1001, 0); fleet.fleetId = 1;
        var hand = new MechaData { HandItem = 1002, HandCount = 1, HandInc = 4 };
        var stock = new StorageComponent(1); var bag = new StorageComponent(1);
        TestAssert.IsFalse(FighterPortOperation.TryApply(ref fleet, 2, 0, false, 0, 0, stock, bag, hand, out _, Valid));
        fleet.fleetId = 0;
        TestAssert.IsFalse(FighterPortOperation.TryApply(ref fleet, 1, 1, false, 0, 0, stock, bag, hand, out _, Valid));
        TestAssert.IsFalse(FighterPortOperation.TryApply(ref fleet, 2, 1, false, 0, 0, stock, bag, hand, out _, (_, _, _) => false));
        TestAssert.AreEqual(1002, hand.HandItem); TestAssert.AreEqual(1, hand.HandCount); TestAssert.AreEqual(4, hand.HandInc);
        TestAssert.AreEqual(1, fleet.fighters[0].count); TestAssert.AreEqual(0, fleet.fighters[1].count);
    }

    [TestMethod]
    public void StowingWithNoCapacityRollsBackTheWholeGesture()
    {
        var stock = new StorageComponent(1); var bag = new StorageComponent(0);
        stock.grids[0] = new StorageComponent.GRID { itemId = 1001, count = 99, stackSize = 100 };
        var hand = new MechaData();
        // An empty selected port is the native Ctrl-click gesture for stowing the entire fleet.
        var all = Fleet(0, 1001, 1001);
        TestAssert.IsFalse(FighterPortOperation.TryApply(ref all, 3, 0, false, FighterPortOperation.Control, 0,
            stock, bag, hand, out _, Valid));
        TestAssert.AreEqual(99, stock.grids[0].count);
        TestAssert.AreEqual(2, all.fighters.Sum(f => f.count));
        TestAssert.AreEqual(0, hand.HandCount);
    }

    [TestMethod]
    public void RightClickStowsAndClearsAGroundPortWithoutLosingStock()
    {
        var fleet = Fleet(1001);
        var stock = new StorageComponent(1); var hand = new MechaData();
        TestAssert.IsTrue(FighterPortOperation.TryApply(ref fleet, 1, 0, false, FighterPortOperation.Right, 0,
            stock, new StorageComponent(0), hand, out var credits, Valid));
        TestAssert.AreEqual(1, stock.GetItemCount(1001));
        TestAssert.AreEqual(0, fleet.fighters[0].itemId); TestAssert.AreEqual(0, fleet.fighters[0].count);
        TestAssert.IsEmpty(credits);
    }

    [TestMethod]
    public void PortConfigurationOnlyChangesAnEmptyGroundPort()
    {
        var fleet = Fleet(0, 1001);
        var hand = new MechaData { HandItem = 1002, HandCount = 8, HandInc = 16 };
        var stock = new StorageComponent(1); var bag = new StorageComponent(1);
        TestAssert.IsTrue(FighterPortOperation.TryApply(ref fleet, 2, 0, false, FighterPortOperation.Configure, 1002, stock, bag, hand, out _, Valid));
        TestAssert.AreEqual(1002, fleet.fighters[0].itemId); TestAssert.AreEqual(0, fleet.fighters[0].count);
        TestAssert.IsFalse(FighterPortOperation.TryApply(ref fleet, 2, 1, false, FighterPortOperation.Configure, 1002, stock, bag, hand, out _, Valid));
        TestAssert.IsFalse(FighterPortOperation.TryApply(ref fleet, 2, 0, true, FighterPortOperation.Configure, 1002, stock, bag, hand, out _, Valid));
        TestAssert.AreEqual(8, hand.HandCount); TestAssert.AreEqual(16, hand.HandInc);
    }

    [TestMethod]
    public void FighterPortWireValidationRejectsInvalidGesturesAndSlots()
    {
        foreach (var recipe in new[] { -5, -6 })
        {
            var intent = new FactoryProductIntent(FactoryInteractionKind.FighterPort, recipe, 2, 1001, 3, 6, 4);
            TestAssert.IsTrue(FactoryProductIntent.TryDecode(intent.Encode(), out var decoded));
            TestAssert.AreEqual(intent.NextRecipe, decoded.NextRecipe);
        }
        foreach (var invalid in new[]
        {
            new FactoryProductIntent(FactoryInteractionKind.FighterPort, -6, 64),
            new FactoryProductIntent(FactoryInteractionKind.FighterPort, -6, 0, nextRecipe: 3),
            new FactoryProductIntent(FactoryInteractionKind.FighterPort, -5, 0, 1001, -1),
            new FactoryProductIntent(FactoryInteractionKind.FighterPort, -6, 0, 1001, 1, 11),
            new FactoryProductIntent(FactoryInteractionKind.FighterPort, -6, 0, 1001, 1, nextRecipe: 2)
        }) TestAssert.IsFalse(FactoryProductIntent.TryDecode(invalid.Encode(), out _));
    }

    [TestMethod]
    public void FleetTargetSettingsSurviveReportsAndSnapshotMerging()
    {
        var ground = new CombatModuleComponent { attackBuilding = false, moduleFleets = [], fleetCount = 0 };
        var space = new CombatModuleComponent { attackRelay = true, attackBuilding = false, moduleFleets = [], fleetCount = 0 };
        var flags = CombatFleetSettings.Encode(123, ground, space);
        var targetGround = new CombatModuleComponent { attackBuilding = true, moduleFleets = [] };
        var targetSpace = new CombatModuleComponent { attackBuilding = true, moduleFleets = [] };
        CombatFleetSettings.Apply(flags, targetGround, targetSpace);
        TestAssert.IsTrue(targetSpace.attackRelay); TestAssert.IsFalse(targetSpace.attackBuilding); TestAssert.IsFalse(targetGround.attackBuilding);
        var copy = AccessTools.Method(typeof(CombatAuthorityManager), "CopyFleetSettings");
        targetGround.attackBuilding = true;
        copy.Invoke(null, new object[] { targetGround, ground });
        TestAssert.IsFalse(targetGround.attackBuilding);
    }

    [TestMethod]
    public void BaseSnapshotsKeepFiltersAndRejectPartialOrInvalidPortUpdates()
    {
        var fleet = Fleet(0); fleet.protoId = 1;
        var battleBase = new BattleBaseComponent
        {
            combatModule = new CombatModuleComponent { moduleFleets = new[] { fleet } },
            storage = new StorageComponent(1)
        };
        var packet = new FactoryProductPacket
        {
            Message = (byte)FactoryProductMessage.FighterPortsSnapshot,
            Recipe = 1,
            Products = [1001],
            Counts = [1],
            Inputs = [0],
            Served = [0],
            IncServed = [0],
            Filters = [1002]
        };
        TestAssert.IsTrue(FactoryProductManager.ApplyFighterPortSnapshot(battleBase, packet));
        TestAssert.AreEqual(1001, battleBase.combatModule.moduleFleets[0].fighters[0].itemId);
        TestAssert.AreEqual(1002, battleBase.storage.grids[0].filter);
        TestAssert.AreEqual(100, battleBase.storage.grids[0].stackSize);
        TestAssert.IsTrue(FactoryProductManager.FighterPortsMatch(battleBase, packet));
        battleBase.storage.grids[0].filter = 0;
        TestAssert.IsFalse(FactoryProductManager.FighterPortsMatch(battleBase, packet));
        packet.Products[0] = 1002; packet.Counts[0] = 2;
        TestAssert.IsFalse(FactoryProductManager.ApplyFighterPortSnapshot(battleBase, packet));
        TestAssert.AreEqual(1001, battleBase.combatModule.moduleFleets[0].fighters[0].itemId);
        TestAssert.AreEqual(0, battleBase.storage.grids[0].filter);
    }

    [TestMethod]
    public void BaseStorageFiltersRoundTripAndQueuedSnapshotsOwnTheirArrays()
    {
        var packet = new FactoryProductPacket { Message = (byte)FactoryProductMessage.FighterPortsSnapshot, Filters = [1001, 1002] };
        var received = RoundTrip(packet);
        CollectionAssert.AreEqual(packet.Filters, received.Filters);
        var queued = received.Copy(); received.Filters[0] = 0;
        TestAssert.AreEqual(1001, queued.Filters[0]);
    }
}
