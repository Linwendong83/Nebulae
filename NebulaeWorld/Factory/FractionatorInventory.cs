using System;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Factory.Assembler;
using NebulaeWorld.Authority;

namespace NebulaeWorld.Factory;

internal sealed partial class FactoryProductManager
{
    private static bool TryFractionator(in ObjectKey key, out PlanetFactory factory, out int id)
    {
        factory = GameMain.galaxy?.PlanetById(key.Scope)?.factory; id = 0;
        if (factory?.factorySystem == null || key.NativeId <= 0 || key.NativeId >= factory.entityCursor || factory.entityPool[key.NativeId].id != key.NativeId) return false;
        id = factory.entityPool[key.NativeId].fractionatorId;
        return id > 0 && id < factory.factorySystem.fractionatorCursor && factory.factorySystem.fractionatorPool[id].id == id;
    }
    public bool RequestFractionator(PlanetFactory factory, int id, int slot, bool hand, bool deposit = false)
    {
        EnsureWorld();
        if (factory?.factorySystem == null || id <= 0 || id >= factory.factorySystem.fractionatorCursor || slot is < 0 or > 1) return false;
        var machine = factory.factorySystem.fractionatorPool[id];
        if (machine.id != id || Multiplayer.Session.AuthorityRuntime.WorldReplica?.TryFindKey(PoolKind.Entity, factory.planetId, machine.entityId, out var key) != true) return false;
        var item = slot == 0 ? machine.productId : machine.fluidId;
        var available = slot == 0 ? machine.productOutputCount : machine.fluidOutputCount;
        var count = available;
        if (deposit)
        {
            var player = GameMain.mainPlayer;
            if (!hand || player.inhandItemId != item) return false;
            count = Math.Min(player.inhandItemCount, (slot == 0 ? machine.productOutputMax : machine.fluidOutputMax) - available);
        }
        if (item <= 0 || count <= 0) return false;
        lock (gate)
        {
            if (requests.Count >= 32 || !requestedSlots.Add((key, slot))) return false;
            requests.Enqueue((key, new FactoryProductIntent(deposit ? FactoryInteractionKind.Deposit : FactoryInteractionKind.Withdraw, -2, slot, item, count, hand: hand)));
            return true;
        }
    }

    internal static PlayerInventoryTransferLedger.Transfer TakeFractionator(ref FractionatorComponent machine, in FactoryProductIntent intent, StorageComponent package, MechaData personal)
    {
        if (intent.Recipe != -2 || intent.Slot is < 0 or > 1 || intent.Count <= 0 || intent.Item != (intent.Slot == 0 ? machine.productId : machine.fluidId)) return default;
        var available = intent.Slot == 0 ? machine.productOutputCount : machine.fluidOutputCount;
        var totalInc = intent.Slot == 0 ? 0 : machine.fluidOutputInc;
        if (available <= 0 || totalInc < 0 || (long)totalInc > (long)available * 10) return default;
        var storage = new StorageComponent(1);
        storage.grids[0] = new StorageComponent.GRID { itemId = intent.Item, count = available, inc = totalInc, stackSize = int.MaxValue };
        var item = intent.Item; var count = Math.Min(intent.Count, available);
        storage.TakeItemFromGrid(0, ref item, ref count, out var inc);
        int added, remainingInc;
        if (intent.Hand) { if (personal?.CreditHand(item, count, inc) != true) return default; added = count; remainingInc = 0; }
        else added = package.AddItemStacked(item, count, inc, out remainingInc);
        if (added <= 0) return default;
        if (intent.Slot == 0) machine.productOutputCount -= added;
        else { machine.fluidOutputCount -= added; machine.fluidOutputInc -= inc - remainingInc; }
        return new PlayerInventoryTransferLedger.Transfer(item, added, inc - remainingInc, intent.Hand);
    }
    internal static bool PutFractionator(ref FractionatorComponent machine, in FactoryProductIntent intent, MechaData personal)
    {
        if (intent.Recipe != -2 || !intent.Hand || intent.Slot is < 0 or > 1 || intent.Count <= 0 || intent.Inc < 0 || intent.Item != (intent.Slot == 0 ? machine.productId : machine.fluidId) || personal == null || personal.HandItem != intent.Item || personal.HandCount < intent.Count) return false;
        var free = intent.Slot == 0 ? machine.productOutputMax - machine.productOutputCount : machine.fluidOutputMax - machine.fluidOutputCount;
        if (free < intent.Count) return false;
        var count = personal.TakeHand(intent.Item, intent.Count, out var inc);
        if (count != intent.Count || inc != intent.Inc) { if (count > 0) personal.CreditHand(intent.Item, count, inc); return false; }
        if (intent.Slot == 0) machine.productOutputCount += count;
        else { machine.fluidOutputCount += count; machine.fluidOutputInc += inc; }
        return true;
    }
    private CommandOutcome ExecuteFractionator(in QueuedHostCommand command, in ObjectKey key, PlanetFactory factory, int id, in FactoryProductIntent intent, StorageComponent package, MechaData personal, global::Player player)
    {
        ref var machine = ref factory.factorySystem.fractionatorPool[id];
        if (intent.Recipe != -2) return new CommandOutcome(CommandResultCode.RejectedStale);
        PlayerInventoryTransferLedger.Transfer transfer;
        if (intent.Kind == FactoryInteractionKind.Deposit)
        {
            if (!PutFractionator(ref machine, intent, personal)) return new CommandOutcome(CommandResultCode.RejectedResource);
            transfer = new PlayerInventoryTransferLedger.Transfer(intent.Item, -intent.Count, intent.Inc, true);
            Commit(key, -2, intent.Slot, intent.Item, intent.Count, FactoryProductMessage.InputCommit, intent.Slot == 0 ? 0 : intent.Inc);
        }
        else if (intent.Kind == FactoryInteractionKind.Withdraw)
        {
            transfer = TakeFractionator(ref machine, intent, package, personal);
            if (transfer.Count <= 0) return new CommandOutcome(CommandResultCode.RejectedResource);
            Commit(key, -2, intent.Slot, intent.Item, transfer.Count, FactoryProductMessage.Commit, transfer.Inc);
        }
        else return new CommandOutcome(CommandResultCode.RejectedInvalid);
        Multiplayer.Session.InventoryTransfers.Record(command.ConnectionPlayerId, command.Key.Sequence, new[] { transfer });
        if (intent.Hand && player != null) personal.ApplyHand(player);
        return new CommandOutcome(CommandResultCode.Applied, GameMain.gameTick, resourceRevision: command.Key.Sequence,
            resultData: FactoryInventoryReceipt.Encode(transfer.Count > 0 ? new[] { transfer } : Array.Empty<PlayerInventoryTransferLedger.Transfer>()));
    }
    public int LocalFractionator(PlanetFactory factory, int id, int slot, global::Player player, bool hand, bool deposit = false)
    {
        EnsureWorld();
        ref var machine = ref factory.factorySystem.fractionatorPool[id];
        if (slot is < 0 or > 1 || Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, machine.entityId, out var key) != true) return 0;
        var item = slot == 0 ? machine.productId : machine.fluidId; var personal = new MechaData(player);
        var count = deposit ? Math.Min(player.inhandItemCount, (slot == 0 ? machine.productOutputMax - machine.productOutputCount : machine.fluidOutputMax - machine.fluidOutputCount)) : slot == 0 ? machine.productOutputCount : machine.fluidOutputCount;
        if (count <= 0) return 0;
        var transfer = default(PlayerInventoryTransferLedger.Transfer);
        if (deposit)
        {
            var probe = new MechaData { HandItem = personal.HandItem, HandCount = personal.HandCount, HandInc = personal.HandInc };
            if (probe.TakeHand(item, count, out var inc) != count || !PutFractionator(ref machine, new FactoryProductIntent(FactoryInteractionKind.Deposit, -2, slot, item, count, inc, hand: true), personal)) return 0;
            Commit(key, -2, slot, item, count, FactoryProductMessage.InputCommit, slot == 0 ? 0 : inc);
        }
        else
        {
            transfer = TakeFractionator(ref machine, new FactoryProductIntent(FactoryInteractionKind.Withdraw, -2, slot, item, count, hand: hand), player.package, personal);
            count = transfer.Count; if (count <= 0) return 0;
            Commit(key, -2, slot, item, count, FactoryProductMessage.Commit, transfer.Inc);
        }
        if (hand) personal.ApplyHand(player);
        if (!deposit) { player.NotifyPackageAddItem(item, count, transfer.Inc); UIItemup.Up(item, count); }
        return count;
    }
    private FactoryProductPacket FractionatorSnapshot(in ObjectKey key, PlanetFactory factory, int id)
    {
        if (Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, key.NativeId, out var live) != true || live != key) return null;
        ref var machine = ref factory.factorySystem.fractionatorPool[id]; sequences.TryGetValue(key, out var sequence);
        return new FactoryProductPacket
        {
            Key = FactoryProductPacket.EncodeKey(key),
            Message = (byte)FactoryProductMessage.Snapshot,
            Sequence = sequence,
            Recipe = -2,
            Products = new[] { machine.productId, machine.fluidId },
            Counts = new[] { machine.productOutputCount, machine.fluidOutputCount },
            OutputInc = new[] { 0, machine.fluidOutputInc }
        };
    }
    internal static bool ApplyFractionator(ref FractionatorComponent machine, FactoryProductPacket packet)
    {
        if (packet.Recipe != -2) return false;
        if (packet.Message == (byte)FactoryProductMessage.Snapshot)
        {
            if (packet.Products?.Length != 2 || packet.Counts?.Length != 2 || packet.OutputInc?.Length != 2 || packet.Products[0] != machine.productId || packet.Products[1] != machine.fluidId ||
                packet.Counts[0] < 0 || packet.Counts[1] < 0 || packet.OutputInc[0] != 0 || packet.OutputInc[1] < 0 || (long)packet.OutputInc[1] > (long)packet.Counts[1] * 10) return false;
            machine.productOutputCount = packet.Counts[0]; machine.fluidOutputCount = packet.Counts[1]; machine.fluidOutputInc = packet.OutputInc[1]; return true;
        }
        if (packet.Slot is < 0 or > 1 || packet.Item != (packet.Slot == 0 ? machine.productId : machine.fluidId) || packet.Count <= 0 || packet.Inc < 0 || (long)packet.Inc > (long)packet.Count * 10 || packet.Slot == 0 && packet.Inc != 0) return false;
        var sign = packet.Message == (byte)FactoryProductMessage.Commit ? -1 : packet.Message == (byte)FactoryProductMessage.InputCommit ? 1 : 0;
        var after = (long)(packet.Slot == 0 ? machine.productOutputCount : machine.fluidOutputCount) + sign * packet.Count;
        var afterInc = (long)machine.fluidOutputInc + sign * packet.Inc;
        if (sign == 0 || after < 0 || after > int.MaxValue || afterInc < 0 || afterInc > int.MaxValue) return false;
        if (packet.Slot == 0) machine.productOutputCount = (int)after;
        else { machine.fluidOutputCount = (int)after; machine.fluidOutputInc = (int)afterInc; }
        return true;
    }
    private void ApplyFractionatorPacket(in ObjectKey key, PlanetFactory factory, int id, FactoryProductPacket packet)
    {
        var initialized = sequences.TryGetValue(key, out var last);
        ref var machine = ref factory.factorySystem.fractionatorPool[id];
        if (packet.Message == (byte)FactoryProductMessage.Snapshot)
        {
            if (!repairs.Contains(key) || packet.Sequence < last || !ApplyFractionator(ref machine, packet)) return;
            sequences[key] = packet.Sequence; repairs.Remove(key); repairTicks.Remove(key);
        }
        else if (packet.Sequence > last)
        {
            if (!initialized || repairs.Contains(key) || packet.Sequence != last + 1 || !ApplyFractionator(ref machine, packet))
            { if (repairs.Add(key)) RequestRepair(key); }
            else sequences[key] = packet.Sequence;
        }
    }
}
