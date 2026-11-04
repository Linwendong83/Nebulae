using NebulaeModel.Authority;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Factory.Assembler;
using NebulaeWorld.Factory;
using NebulaePatcher.Patches.Authority;
using NebulaeModel.Packets.Authority;
using HarmonyLib;
using NebulaeModel.DataStructures;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class FactoryProductTest
{
    private int[]? previousStacks;
    private bool previousStaticLoaded;
    [TestInitialize]
    public void InitializeStorageStacks()
    {
        previousStacks = StorageComponent.itemStackCount;
        previousStaticLoaded = (bool)AccessTools.Field(typeof(StorageComponent), "staticLoaded").GetValue(null);
        AccessTools.Field(typeof(StorageComponent), "staticLoaded").SetValue(null, true);
        StorageComponent.itemStackCount = previousStacks == null ? new int[12000] : (int[])previousStacks.Clone();
        StorageComponent.itemStackCount[1001] = 100; StorageComponent.itemStackCount[1201] = 100;
        StorageComponent.itemStackCount[6001] = 100;
        StorageComponent.itemStackCount[1120] = 100; StorageComponent.itemStackCount[1121] = 100;
        StorageComponent.itemStackCount[1501] = 100; StorageComponent.itemStackCount[1503] = 100;
    }
    [TestCleanup]
    public void RestoreStorageStacks()
    {
        StorageComponent.itemStackCount = previousStacks;
        AccessTools.Field(typeof(StorageComponent), "staticLoaded").SetValue(null, previousStaticLoaded);
    }
    private static AssemblerComponent Machine(int count = 20) => new()
    {
        id = 1,
        entityId = 2,
        recipeId = 17,
        produced = [count],
        recipeExecuteData = new RecipeExecuteData([], [], [1001], [1], 3600, 3600, false)
    };
    private static StorageComponent Package(int count = 0, int capacity = 100)
    {
        var package = Uninitialized<StorageComponent>();
        package.size = 1;
        package.grids = [new StorageComponent.GRID { itemId = 1001, count = count, stackSize = capacity }];
        return package;
    }

    [TestMethod]
    public void ResearchMatrixTransfersPreserveFractionalPointsAndNativeIncrementSplits()
    {
        var lab = new LabComponent { id = 1, entityId = 2, researchMode = true, matrixServed = [7201, 0, 0, 0, 0, 0], matrixIncServed = [28804, 0, 0, 0, 0, 0] };
        var machine = FactoryMachine.ViewOf(lab, -1); var personal = new MechaData(); var package = Package();
        var take = new FactoryProductIntent(FactoryInteractionKind.InputWithdraw, -1, 0, 6001, 1, hand: true);
        TestAssert.IsTrue(FactoryProductIntent.TryDecode(take.Encode(), out _));
        var transfer = FactoryProductManager.WithdrawInput(ref machine, take, package, personal);
        TestAssert.AreEqual(1, transfer.Count); TestAssert.AreEqual(4, transfer.Inc); TestAssert.IsTrue(transfer.Hand);
        TestAssert.AreEqual(3601, lab.matrixServed[0]); TestAssert.AreEqual(14404, lab.matrixIncServed[0]);
        TestAssert.AreEqual(1, personal.HandCount); TestAssert.AreEqual(4, personal.HandInc);
        transfer = FactoryProductManager.WithdrawInput(ref machine, take, package, personal);
        TestAssert.AreEqual(1, lab.matrixServed[0]); TestAssert.AreEqual(4, lab.matrixIncServed[0]);
        var put = new FactoryProductIntent(FactoryInteractionKind.Deposit, -1, 0, 6001, 2, 8, hand: true);
        TestAssert.IsTrue(FactoryProductManager.DepositHand(ref machine, put, personal));
        TestAssert.AreEqual(7201, lab.matrixServed[0]); TestAssert.AreEqual(28804, lab.matrixIncServed[0]);
        TestAssert.AreEqual(0, personal.HandCount);
    }

    [TestMethod]
    public void ResearchWithdrawalWithOnePackageSlotLeavesTheRestInTheLab()
    {
        var lab = new LabComponent { id = 1, entityId = 2, researchMode = true, matrixServed = [7201, 0, 0, 0, 0, 0], matrixIncServed = [28804, 0, 0, 0, 0, 0] };
        var machine = FactoryMachine.ViewOf(lab, -1); var package = Package(99); package.grids[0].itemId = 6001;
        var take = new FactoryProductIntent(FactoryInteractionKind.InputWithdraw, -1, 0, 6001, 2);
        var transfer = FactoryProductManager.WithdrawInput(ref machine, take, package, null);
        TestAssert.AreEqual(1, transfer.Count); TestAssert.AreEqual(4, transfer.Inc);
        TestAssert.AreEqual(3601, lab.matrixServed[0]); TestAssert.AreEqual(14404, lab.matrixIncServed[0]);
        TestAssert.AreEqual(0, FactoryProductManager.WithdrawInput(ref machine, take, package, null).Count);
        TestAssert.AreEqual(3601, lab.matrixServed[0]);
    }

    [TestMethod]
    public void NativeLabRefundCollectionDoesNotMutateResearchStockOrMode()
    {
        var system = Uninitialized<FactorySystem>();
        system.factory = Uninitialized<PlanetFactory>();
        system.factory.entitySignPool = new SignData[4];
        system.labPool = [default, new LabComponent { id = 1, entityId = 2, researchMode = true, matrixServed = [7201, 0, 0, 0, 0, 0], matrixIncServed = [28804, 0, 0, 0, 0, 0] }];
        var harmony = new Harmony("nebulae.tests.lab-refunds");
        harmony.CreateClassProcessor(typeof(FactoryRefunds)).Patch();
        var refunds = FactoryRefunds.CollectLab(system, 1);
        TestAssert.HasCount(1, refunds); TestAssert.AreEqual(6001, refunds[0].Item);
        TestAssert.AreEqual(2, refunds[0].Count); TestAssert.AreEqual(8, refunds[0].Inc);
        TestAssert.IsTrue(system.labPool[1].researchMode); TestAssert.AreEqual(7201, system.labPool[1].matrixServed[0]);
    }

    [TestMethod]
    public void ProducedItemsUseTheCanonicalHandAndRejectAnotherHeldItem()
    {
        var machine = Machine(); var personal = new MechaData { HandItem = 1201, HandCount = 1 };
        var intent = new FactoryProductIntent(FactoryInteractionKind.Withdraw, 17, 0, 1001, 3, hand: true);
        TestAssert.IsTrue(FactoryProductIntent.TryDecode(intent.Encode(), out _));
        TestAssert.AreEqual(0, FactoryProductManager.WithdrawToHand(ref machine, intent, personal));
        TestAssert.AreEqual(20, machine.produced[0]);
        personal.HandItem = personal.HandCount = 0;
        TestAssert.AreEqual(3, FactoryProductManager.WithdrawToHand(ref machine, intent, personal));
        TestAssert.AreEqual(17, machine.produced[0]); TestAssert.AreEqual(1001, personal.HandItem); TestAssert.AreEqual(3, personal.HandCount);
    }

    [TestMethod]
    public void LaboratoryStackTraversalUsesNativeConnectionsAndTerminatesOnCycles()
    {
        var factory = Uninitialized<PlanetFactory>();
        factory.factorySystem = Uninitialized<FactorySystem>();
        factory.entityCursor = 4; factory.entityPool = new EntityData[4]; factory.entityConnPool = new int[64];
        factory.factorySystem.labCursor = 4; factory.factorySystem.labPool = new LabComponent[4];
        for (var i = 1; i < 4; i++) { factory.entityPool[i] = new EntityData { id = i, labId = i }; factory.factorySystem.labPool[i] = new LabComponent { id = i, entityId = i }; }
        factory.WriteObjectConn(1, 15, true, 2, 14); factory.WriteObjectConn(2, 15, true, 3, 14);
        var root = new FactoryMachine(factory, -2);
        TestAssert.HasCount(3, root.Stack());
        factory.WriteObjectConn(3, 15, true, 1, 14);
        TestAssert.HasCount(3, root.Stack());
        TestAssert.IsFalse(new FactoryMachine(factory, int.MinValue).Valid);
    }

    [TestMethod]
    public void FractionatorWithdrawalKeepsNewOutputAndCreditsOnlyAvailablePackageSpace()
    {
        var machine = new FractionatorComponent { id = 1, fluidId = 1120, productId = 1121, productOutputCount = 20, fluidOutputCount = 7, fluidOutputInc = 23, seed = 77, progress = 125 };
        var package = Package(99); package.grids[0].itemId = 1120;
        var intent = new FactoryProductIntent(FactoryInteractionKind.Withdraw, -2, 1, 1120, 5);
        TestAssert.IsTrue(FactoryProductIntent.TryDecode(intent.Encode(), out _));
        var transfer = FactoryProductManager.TakeFractionator(ref machine, intent, package, null);
        TestAssert.AreEqual(1, transfer.Count); TestAssert.AreEqual(3, transfer.Inc);
        TestAssert.AreEqual(6, machine.fluidOutputCount); TestAssert.AreEqual(20, machine.fluidOutputInc);
        var client = new FractionatorComponent { fluidId = 1120, productId = 1121, productOutputCount = 22, fluidOutputCount = 9, fluidOutputInc = 31, seed = 99, progress = 250 };
        TestAssert.IsTrue(FactoryProductManager.ApplyFractionator(ref client, new FactoryProductPacket { Recipe = -2, Message = (byte)FactoryProductMessage.Commit, Slot = 1, Item = 1120, Count = transfer.Count, Inc = transfer.Inc }));
        TestAssert.AreEqual(8, client.fluidOutputCount); TestAssert.AreEqual(28, client.fluidOutputInc);
        TestAssert.AreEqual(22, client.productOutputCount); TestAssert.AreEqual(99u, client.seed); TestAssert.AreEqual(250, client.progress);
        TestAssert.AreEqual(0, FactoryProductManager.TakeFractionator(ref machine, intent, package, null).Count);
    }

    [TestMethod]
    public void FractionatorHandDepositAndSnapshotPreserveNativeInputAndRandomState()
    {
        var machine = new FractionatorComponent { fluidId = 1120, productId = 1121, fluidOutputCount = 2, fluidOutputInc = 6, fluidOutputMax = 10, productOutputMax = 10, fluidInputCount = 11, fluidInputInc = 44, seed = 77, progress = 125 };
        var hand = new MechaData { HandItem = 1120, HandCount = 7, HandInc = 23 };
        var put = new FactoryProductIntent(FactoryInteractionKind.Deposit, -2, 1, 1120, 3, 9, hand: true);
        TestAssert.IsTrue(FactoryProductManager.PutFractionator(ref machine, put, hand));
        TestAssert.AreEqual(5, machine.fluidOutputCount); TestAssert.AreEqual(15, machine.fluidOutputInc);
        TestAssert.AreEqual(4, hand.HandCount); TestAssert.AreEqual(14, hand.HandInc);
        var tooMuch = new FactoryProductIntent(FactoryInteractionKind.Deposit, -2, 1, 1120, 6, 18, hand: true);
        TestAssert.IsFalse(FactoryProductManager.PutFractionator(ref machine, tooMuch, hand));
        var packet = new FactoryProductPacket { Recipe = -2, Message = (byte)FactoryProductMessage.Snapshot, Products = [1121, 1120], Counts = [3, 6], OutputInc = [0, 20] };
        var processor = new NebulaeNetPacketProcessor(); FactoryProductPacket? received = null;
        processor.SubscribeReusable<FactoryProductPacket>(value => received = value);
        processor.ReadPacket(new NetDataReader(processor.Write(packet)));
        TestAssert.IsNotNull(received);
        TestAssert.AreEqual(20, received.OutputInc[1]);
        TestAssert.IsTrue(FactoryProductManager.ApplyFractionator(ref machine, packet));
        TestAssert.AreEqual(3, machine.productOutputCount); TestAssert.AreEqual(6, machine.fluidOutputCount); TestAssert.AreEqual(20, machine.fluidOutputInc);
        TestAssert.AreEqual(11, machine.fluidInputCount); TestAssert.AreEqual(44, machine.fluidInputInc); TestAssert.AreEqual(77u, machine.seed); TestAssert.AreEqual(125, machine.progress);
        var clone = packet.Copy(); packet.OutputInc[1] = 0; TestAssert.AreEqual(20, clone.OutputInc[1]);
    }

    [TestMethod]
    public void BothLauncherStoresUseActualSpaceAndPreserveInterveningConsumption()
    {
        foreach (var recipe in new[] { -3, -4 })
        {
            var item = recipe == -3 ? 1501 : 1503;
            var stock = new FactoryProductManager.LauncherStock(recipe, item, 7, 23);
            var intent = new FactoryProductIntent(FactoryInteractionKind.Withdraw, recipe, 0, item, 5);
            TestAssert.IsTrue(FactoryProductIntent.TryDecode(intent.Encode(), out _));
            var package = Package(99); package.grids[0].itemId = item;
            var credit = FactoryProductManager.TakeLauncher(stock, intent, package, null);
            TestAssert.AreEqual(1, credit.Count); TestAssert.AreEqual(3, credit.Inc);
            var local = new FactoryProductManager.LauncherStock(recipe, item, 6, 20);
            var commit = new FactoryProductPacket { Recipe = recipe, Message = (byte)FactoryProductMessage.Commit, Item = item, Count = credit.Count, Inc = credit.Inc };
            TestAssert.IsTrue(FactoryProductManager.ApplyLauncher(local, commit, out var count, out var inc));
            TestAssert.AreEqual(5, count); TestAssert.AreEqual(17, inc);
            TestAssert.AreEqual(0, FactoryProductManager.TakeLauncher(stock, intent, package, null).Count);
        }
    }

    [TestMethod]
    public void LauncherDepositRejectsFullMagazineWithoutChangingTheHand()
    {
        var hand = new MechaData { HandItem = 1501, HandCount = 7, HandInc = 23 };
        var intent = new FactoryProductIntent(FactoryInteractionKind.Deposit, -3, 0, 1501, 3, 9, hand: true);
        var package = Package();
        TestAssert.IsFalse(FactoryProductManager.DebitLauncher(new FactoryProductManager.LauncherStock(-3, 1501, 99, 0), intent, package, hand));
        TestAssert.AreEqual(7, hand.HandCount); TestAssert.AreEqual(23, hand.HandInc);
        TestAssert.IsTrue(FactoryProductManager.DebitLauncher(new FactoryProductManager.LauncherStock(-3, 1501, 50, 0), intent, package, hand));
        TestAssert.AreEqual(4, hand.HandCount); TestAssert.AreEqual(14, hand.HandInc);
        var packet = new FactoryProductPacket { Recipe = -3, Message = (byte)FactoryProductMessage.Snapshot, Products = [1501], Counts = [53], OutputInc = [9] };
        TestAssert.IsTrue(FactoryProductManager.ApplyLauncher(new FactoryProductManager.LauncherStock(-3, 1501, 0, 0), packet, out var count, out var inc));
        TestAssert.AreEqual(53, count); TestAssert.AreEqual(9, inc);
    }

    [TestMethod]
    public void WithdrawalPreservesProductionAfterTheClickAndUsesActualPackageCapacity()
    {
        var machine = Machine(20); var package = Package();
        var removed = FactoryProductManager.Withdraw(ref machine, new FactoryProductIntent(17, 0, 1001, 10), package);
        TestAssert.AreEqual(10, removed); TestAssert.AreEqual(10, machine.produced[0]); TestAssert.AreEqual(10, package.grids[0].count);
        var client = Machine(13);
        TestAssert.IsTrue(FactoryProductManager.ApplyCommit(ref client, new FactoryProductPacket { Recipe = 17, Slot = 0, Item = 1001, Count = removed }));
        TestAssert.AreEqual(3, client.produced[0], "The commit preserves locally produced items.");
        package.grids[0].count = 99;
        TestAssert.AreEqual(1, FactoryProductManager.Withdraw(ref machine, new FactoryProductIntent(17, 0, 1001, 10), package));
        TestAssert.AreEqual(9, machine.produced[0]);
        TestAssert.AreEqual(0, FactoryProductManager.Withdraw(ref machine, new FactoryProductIntent(17, 0, 1001, 10), package));
        TestAssert.AreEqual(9, machine.produced[0]);
    }

    [TestMethod]
    public void StaleRecipeAndMalformedCommitCannotMutateEitherInventory()
    {
        var machine = Machine(); var package = Package();
        TestAssert.AreEqual(0, FactoryProductManager.Withdraw(ref machine, new FactoryProductIntent(18, 0, 1001, 10), package));
        TestAssert.AreEqual(0, FactoryProductManager.Withdraw(ref machine, new FactoryProductIntent(17, 0, 1002, 10), package));
        TestAssert.IsFalse(FactoryProductManager.ApplyCommit(ref machine, new FactoryProductPacket { Recipe = 17, Slot = 0, Item = 1001, Count = 21 }));
        TestAssert.IsFalse(FactoryProductManager.ApplySnapshot(ref machine, new FactoryProductPacket { Recipe = 17, Products = [1001, 1002], Counts = [2, 3] }));
        TestAssert.AreEqual(20, machine.produced[0]); TestAssert.AreEqual(0, package.grids[0].count);
    }

    [TestMethod]
    public void ProductPacketsFenceEpochAndGenerationAndOwnQueuedBuffers()
    {
        var epoch = new AuthorityEpoch(19, 23);
        var key = ObjectKey.Create(epoch, PoolKind.Entity, 101, 2, 7);
        var packet = new FactoryProductPacket
        {
            Key = FactoryProductPacket.EncodeKey(key),
            Message = (byte)FactoryProductMessage.Snapshot,
            Sequence = 12,
            Recipe = 17,
            Products = [1001],
            Counts = [31]
        };
        var processor = new NebulaeNetPacketProcessor();
        FactoryProductPacket received = null!;
        processor.SubscribeReusable<FactoryProductPacket>(result => received = result);
        processor.ReadPacket(new NetDataReader(processor.Write(packet)));
        TestAssert.IsTrue(received.TryKey(epoch, out var restored)); TestAssert.AreEqual(key, restored);
        TestAssert.AreEqual(12L, received.Sequence); TestAssert.AreEqual(31, received.Counts[0]);
        TestAssert.IsFalse(received.TryKey(new AuthorityEpoch(20, 24), out _));
        var queued = received.Copy(); received.Counts[0] = 0; received.Key[0] = 0;
        TestAssert.AreEqual(31, queued.Counts[0]); TestAssert.IsTrue(queued.TryKey(epoch, out _));
        var intent = new FactoryProductIntent(17, 0, 1001, 10).Encode();
        for (var length = 0; length < intent.Length; length++) TestAssert.IsFalse(FactoryProductIntent.TryDecode(intent.Take(length).ToArray(), out _));
        TestAssert.IsTrue(FactoryProductIntent.TryDecode(intent, out _));
    }

    [TestMethod]
    public void DepositUsesReservedIncrementAndRejectsStaleCapacityWithoutDebit()
    {
        var machine = Machine(); machine.recipeType = ERecipeType.Assemble;
        machine.recipeExecuteData = new RecipeExecuteData([1001], [1], [1201], [1], 60, 60, false);
        machine.served = [2]; machine.incServed = [4];
        var package = Package(10); package.grids[0].inc = 20;
        var intent = new FactoryProductIntent(FactoryInteractionKind.Deposit, 17, 0, 1001, 5, 10);
        TestAssert.IsTrue(FactoryProductManager.Deposit(ref machine, intent, package));
        TestAssert.AreEqual(7, machine.served[0]); TestAssert.AreEqual(14, machine.incServed[0]);
        TestAssert.AreEqual(5, package.grids[0].count); TestAssert.AreEqual(10, package.grids[0].inc);
        machine.served[0] = 19;
        TestAssert.IsFalse(FactoryProductManager.Deposit(ref machine, intent, package));
        TestAssert.AreEqual(5, package.grids[0].count);
        machine.served[0] = 0;
        TestAssert.IsFalse(FactoryProductManager.Deposit(ref machine, new FactoryProductIntent(FactoryInteractionKind.Deposit, 17, 0, 1001, 5, 15), package));
        TestAssert.AreEqual(5, package.grids[0].count); TestAssert.AreEqual(10, package.grids[0].inc);
    }

    [TestMethod]
    public void NativeRefundPlanIncludesTheRunningBatchAndDoesNotResetTheRecipe()
    {
        var machine = Machine(3); machine.recipeExecuteData = new RecipeExecuteData([1001], [2], [1201], [1], 60, 60, false);
        machine.served = [5]; machine.incServed = [50]; machine.time = 1;
        var system = Uninitialized<FactorySystem>();
        system.assemblerPool = [default, machine]; system.factory = new PlanetFactory { entitySignPool = new SignData[3] };
        var harmony = new Harmony("NebulaeTests.NativeAssemblerRefund");
        harmony.CreateClassProcessor(typeof(FactoryRefunds)).Patch();
        var refunds = FactoryRefunds.Collect(system, 1);
        TestAssert.HasCount(2, refunds);
        TestAssert.AreEqual(3, refunds[0].Count); TestAssert.AreEqual(7, refunds[1].Count); TestAssert.AreEqual(50, refunds[1].Inc);
        TestAssert.AreEqual(17, system.assemblerPool[1].recipeId); TestAssert.AreEqual(5, system.assemblerPool[1].served[0]);
    }

    [TestMethod]
    public void CompoundReceiptSurvivesReplayAndOwnsCachedBuffers()
    {
        var batch = new[] { new PlayerInventoryTransferLedger.Transfer(1201, 3, 0), new PlayerInventoryTransferLedger.Transfer(1001, 7, 50) };
        var ledger = new PlayerInventoryTransferLedger(); ledger.Record(8, batch);
        TestAssert.AreEqual(2, ledger.PendingAfter(0).Count()); ledger.Acknowledge(8); TestAssert.AreEqual(0, ledger.PendingCount);
        var data = FactoryInventoryReceipt.Encode(batch);
        var outcome = new CommandOutcome(CommandResultCode.Applied, 40, resourceRevision: 8, resultData: data);
        data[0] = 0;
        var epoch = new AuthorityEpoch(19, 23);
        var header = new AuthorityEnvelopeHeader(AuthorityFamily.CommandResult, epoch, new ConnectionEpoch(1), 8, 40, 1, outcome.ResultData.Length);
        var packet = AuthorityCommandResultPacket.Create(header, outcome);
        var copy = (AuthorityCommandResultPacket)packet.CreateOwnedCopy(); packet.ResultData[0] = 0;
        TestAssert.IsTrue(FactoryInventoryReceipt.TryDecode(copy.ResultData, out var restored)); TestAssert.HasCount(2, restored);
        for (var length = 0; length < copy.ResultData.Length; length++) TestAssert.IsFalse(FactoryInventoryReceipt.TryDecode(copy.ResultData.Take(length).ToArray(), out _));
    }

    [TestMethod]
    public void HandTransfersReuseNativeIncrementSplittingAndDoNotTouchThePackage()
    {
        var machine = Machine(); machine.recipeType = ERecipeType.Assemble;
        machine.recipeExecuteData = new RecipeExecuteData([1001], [1], [1201], [1], 60, 60, false);
        machine.served = [0]; machine.incServed = [0];
        var personal = new MechaData { HandItem = 1001, HandCount = 7, HandInc = 23 };
        var package = Package(10);
        var deposit = new FactoryProductIntent(FactoryInteractionKind.Deposit, 17, 0, 1001, 3, 9, hand: true);
        TestAssert.IsTrue(FactoryProductManager.DepositHand(ref machine, deposit, personal));
        TestAssert.AreEqual(4, personal.HandCount); TestAssert.AreEqual(14, personal.HandInc);
        TestAssert.AreEqual(3, machine.served[0]); TestAssert.AreEqual(9, machine.incServed[0]);
        var transfer = FactoryProductManager.WithdrawInput(ref machine, new FactoryProductIntent(FactoryInteractionKind.InputWithdraw, 17, 0, 1001, 2, hand: true), package, personal);
        TestAssert.AreEqual(2, transfer.Count); TestAssert.IsTrue(transfer.Hand);
        TestAssert.AreEqual(6, personal.HandCount); TestAssert.AreEqual(20, personal.HandInc);
        TestAssert.AreEqual(1, machine.served[0]); TestAssert.AreEqual(10, package.grids[0].count);
        var encoded = FactoryInventoryReceipt.Encode(new[] { transfer });
        TestAssert.IsTrue(FactoryInventoryReceipt.TryDecode(encoded, out var restored)); TestAssert.IsTrue(restored[0].Hand);
        personal.HandItem = 1201;
        TestAssert.AreEqual(0, FactoryProductManager.WithdrawInput(ref machine, new FactoryProductIntent(FactoryInteractionKind.InputWithdraw, 17, 0, 1001, 1, hand: true), package, personal).Count);
        TestAssert.AreEqual(1, machine.served[0]);
    }

    [TestMethod]
    public void HandCheckpointPreservesInventoryAndRejectsInvalidIncrementCounts()
    {
        var readerMethod = AccessTools.Method(typeof(MechaData), "ReadHand");
        var personal = new MechaData { HandItem = 1001, HandCount = 3, HandInc = 9 };
        var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true)) { writer.Write(0x48414E44); writer.Write(1001); writer.Write(3); writer.Write(9); }
        data.Position = 0;
        using (var reader = new BinaryReader(data, System.Text.Encoding.UTF8, true)) readerMethod.Invoke(personal, new object[] { reader });
        TestAssert.AreEqual(3, personal.HandCount); TestAssert.AreEqual(9, personal.HandInc);
        data.Position = 12; using (var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true)) writer.Write(31);
        data.Position = 0;
        using var invalid = new BinaryReader(data);
        TestAssert.Throws<System.Reflection.TargetInvocationException>(() => readerMethod.Invoke(personal, new object[] { invalid }));
    }

    [TestMethod]
    public void AMachineViewReportsItsNativeAccelerationSoAnUnchangedToggleNeedsNoCommit()
    {
        // LocalAcceleration skips a stack member whose native forceAccMode already equals the requested
        // state: a client that loaded the planet received that state in its import slice, so both sides
        // already agree and broadcasting would only advance the member's sequence. That skip is sound
        // only if the view reports the component's real value — the assembler path reads the pool
        // directly while the laboratory path is projected through ViewOf, so both are covered here.
        var factory = Uninitialized<PlanetFactory>();
        factory.factorySystem = Uninitialized<FactorySystem>();
        factory.factorySystem.assemblerCursor = 2; factory.factorySystem.assemblerPool = new AssemblerComponent[2];
        factory.factorySystem.labCursor = 2; factory.factorySystem.labPool = new LabComponent[2];
        factory.factorySystem.assemblerPool[1] = new AssemblerComponent { id = 1, entityId = 1, recipeId = 17 };
        factory.factorySystem.labPool[1] = new LabComponent { id = 1, entityId = 2, recipeId = 17 };

        var assembler = new FactoryMachine(factory, 1);
        var laboratory = new FactoryMachine(factory, -1);
        TestAssert.IsFalse(assembler.View.forceAccMode);
        TestAssert.IsFalse(laboratory.View.forceAccMode, "A laboratory view is projected through ViewOf and must still report its native flag.");

        assembler.SetAcceleration(true); laboratory.SetAcceleration(true);
        TestAssert.IsTrue(assembler.View.forceAccMode);
        TestAssert.IsTrue(laboratory.View.forceAccMode, "The projected view must observe a write made through SetAcceleration.");

        // The state that decides the skip is the one the next toggle compares against.
        var requested = true;
        TestAssert.AreEqual(requested, assembler.View.forceAccMode, "An accelerated member matches an accelerate request and needs no commit.");
        requested = false;
        TestAssert.AreNotEqual(requested, assembler.View.forceAccMode, "The same member does not match a settle request, so that toggle still commits.");
    }
}
