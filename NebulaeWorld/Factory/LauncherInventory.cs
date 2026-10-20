using System;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Factory.Assembler;
using NebulaeWorld.Authority;

namespace NebulaeWorld.Factory;

internal sealed partial class FactoryProductManager
{
    internal readonly struct LauncherStock
    {
        public LauncherStock(int recipe, int item, int count, int inc) { Recipe = recipe; Item = item; Count = count; Inc = inc; }
        public int Recipe { get; }
        public int Item { get; }
        public int Count { get; }
        public int Inc { get; }
    }
    private static bool TryLauncher(PlanetFactory factory, int entity, out LauncherStock stock)
    {
        stock = default;
        if (factory?.factorySystem == null || entity <= 0 || entity >= factory.entityCursor || factory.entityPool[entity].id != entity) return false;
        var data = factory.entityPool[entity]; var system = factory.factorySystem;
        if (data.ejectorId > 0 && data.ejectorId < system.ejectorCursor && system.ejectorPool[data.ejectorId].id == data.ejectorId)
        {
            var machine = system.ejectorPool[data.ejectorId]; stock = new LauncherStock(-3, machine.bulletId, machine.bulletCount, machine.bulletInc); return true;
        }
        if (data.siloId > 0 && data.siloId < system.siloCursor && system.siloPool[data.siloId].id == data.siloId)
        {
            var machine = system.siloPool[data.siloId]; stock = new LauncherStock(-4, machine.bulletId, machine.bulletCount, machine.bulletInc); return true;
        }
        return false;
    }
    private static bool TryLauncher(in ObjectKey key, out PlanetFactory factory, out LauncherStock stock)
    {
        factory = GameMain.galaxy?.PlanetById(key.Scope)?.factory;
        return TryLauncher(factory, key.NativeId, out stock);
    }
    private static void WriteLauncher(PlanetFactory factory, int entity, int count, int inc)
    {
        var data = factory.entityPool[entity];
        if (data.ejectorId > 0) { factory.factorySystem.ejectorPool[data.ejectorId].bulletCount = count; factory.factorySystem.ejectorPool[data.ejectorId].bulletInc = inc; }
        else { factory.factorySystem.siloPool[data.siloId].bulletCount = count; factory.factorySystem.siloPool[data.siloId].bulletInc = inc; }
    }
    public bool RequestLauncher(PlanetFactory factory, int entity, bool deposit, bool hand, int wanted = 0)
    {
        EnsureWorld();
        if (!TryLauncher(factory, entity, out var stock) || stock.Item <= 0 || Multiplayer.Session.AuthorityRuntime.WorldReplica?.TryFindKey(PoolKind.Entity, factory.planetId, entity, out var key) != true) return false;
        var count = deposit ? 100 - stock.Count : stock.Count;
        if (wanted > 0) count = Math.Min(count, wanted);
        if (count <= 0) return false;
        lock (gate)
        {
            if (requests.Count >= 32 || !requestedSlots.Add((key, 0))) return false;
            requests.Enqueue((key, new FactoryProductIntent(deposit ? FactoryInteractionKind.Deposit : FactoryInteractionKind.Withdraw, stock.Recipe, 0, stock.Item, count, hand: hand)));
            return true;
        }
    }
    internal static PlayerInventoryTransferLedger.Transfer TakeLauncher(in LauncherStock stock, in FactoryProductIntent intent, StorageComponent package, MechaData personal)
    {
        if (intent.Recipe != stock.Recipe || intent.Item != stock.Item || intent.Slot != 0 || intent.Count <= 0 || stock.Count <= 0 || stock.Inc < 0 || (long)stock.Inc > (long)stock.Count * 10) return default;
        var native = new StorageComponent(1);
        native.grids[0] = new StorageComponent.GRID { itemId = stock.Item, count = stock.Count, inc = stock.Inc, stackSize = int.MaxValue };
        var item = stock.Item; var count = Math.Min(intent.Count, stock.Count);
        native.TakeItemFromGrid(0, ref item, ref count, out var inc);
        int added, remainingInc;
        if (intent.Hand) { if (personal?.CreditHand(item, count, inc) != true) return default; added = count; remainingInc = 0; }
        else added = package.AddItemStacked(item, count, inc, out remainingInc);
        return added > 0 ? new PlayerInventoryTransferLedger.Transfer(item, added, inc - remainingInc, intent.Hand) : default;
    }
    internal static bool DebitLauncher(in LauncherStock stock, in FactoryProductIntent intent, StorageComponent package, MechaData personal)
    {
        if (intent.Recipe != stock.Recipe || intent.Item != stock.Item || intent.Slot != 0 || intent.Count <= 0 || intent.Inc < 0 || intent.Count > 100 - stock.Count) return false;
        if (intent.Hand)
        {
            if (personal == null || personal.HandItem != stock.Item || personal.HandCount < intent.Count) return false;
            var count = personal.TakeHand(stock.Item, intent.Count, out var inc);
            if (count == intent.Count && inc == intent.Inc) return true;
            if (count > 0) personal.CreditHand(stock.Item, count, inc);
            return false;
        }
        if (package.GetItemCount(stock.Item) < intent.Count) return false;
        var item = stock.Item; var taken = intent.Count;
        package.TakeTailItems(ref item, ref taken, out var takenInc);
        if (taken == intent.Count && takenInc == intent.Inc) return true;
        if (taken > 0) package.AddItemStacked(item, taken, takenInc, out _);
        return false;
    }
    private CommandOutcome ExecuteLauncher(in QueuedHostCommand command, in ObjectKey key, PlanetFactory factory, in LauncherStock stock, in FactoryProductIntent intent, StorageComponent package, MechaData personal, global::Player player)
    {
        if (intent.Recipe != stock.Recipe) return new CommandOutcome(CommandResultCode.RejectedStale);
        PlayerInventoryTransferLedger.Transfer transfer;
        var deposit = intent.Kind == FactoryInteractionKind.Deposit;
        if (deposit)
        {
            if (!DebitLauncher(stock, intent, package, personal)) return new CommandOutcome(CommandResultCode.RejectedResource);
            transfer = new PlayerInventoryTransferLedger.Transfer(intent.Item, -intent.Count, intent.Inc, intent.Hand);
            WriteLauncher(factory, key.NativeId, stock.Count + intent.Count, stock.Inc + intent.Inc);
        }
        else if (intent.Kind == FactoryInteractionKind.Withdraw)
        {
            transfer = TakeLauncher(stock, intent, package, personal);
            if (transfer.Count <= 0) return new CommandOutcome(CommandResultCode.RejectedResource);
            WriteLauncher(factory, key.NativeId, stock.Count - transfer.Count, stock.Inc - transfer.Inc);
        }
        else return new CommandOutcome(CommandResultCode.RejectedInvalid);
        Multiplayer.Session.InventoryTransfers.Record(command.ConnectionPlayerId, command.Key.Sequence, new[] { transfer });
        if (intent.Hand && player != null) personal.ApplyHand(player);
        Commit(key, stock.Recipe, 0, stock.Item, Math.Abs(transfer.Count), deposit ? FactoryProductMessage.InputCommit : FactoryProductMessage.Commit, transfer.Inc);
        return new CommandOutcome(CommandResultCode.Applied, GameMain.gameTick, resourceRevision: command.Key.Sequence,
            resultData: FactoryInventoryReceipt.Encode(deposit ? Array.Empty<PlayerInventoryTransferLedger.Transfer>() : new[] { transfer }));
    }
    public int LocalLauncher(PlanetFactory factory, int entity, bool deposit, bool hand, global::Player player, int wanted = 0)
    {
        EnsureWorld();
        if (!TryLauncher(factory, entity, out var stock) || Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, entity, out var key) != true) return 0;
        var count = deposit ? Math.Min(100 - stock.Count, hand ? player.inhandItemId == stock.Item ? player.inhandItemCount : 0 : player.package.GetItemCount(stock.Item)) : stock.Count;
        if (wanted > 0) count = Math.Min(count, wanted);
        if (count <= 0) return 0;
        var personal = new MechaData(player); PlayerInventoryTransferLedger.Transfer transfer;
        if (deposit)
        {
            var probe = new StorageComponent(1);
            if (hand) probe.grids[0] = new StorageComponent.GRID { itemId = personal.HandItem, count = personal.HandCount, inc = personal.HandInc, stackSize = int.MaxValue };
            else { probe = new StorageComponent(player.package.size); Array.Copy(player.package.grids, probe.grids, player.package.size); }
            var item = stock.Item; var taken = count;
            probe.TakeTailItems(ref item, ref taken, out var inc);
            if (taken != count || !DebitLauncher(stock, new FactoryProductIntent(FactoryInteractionKind.Deposit, stock.Recipe, 0, stock.Item, count, inc, hand: hand), player.package, personal)) return 0;
            transfer = new PlayerInventoryTransferLedger.Transfer(stock.Item, -count, inc, hand);
            WriteLauncher(factory, entity, stock.Count + count, stock.Inc + inc);
        }
        else
        {
            transfer = TakeLauncher(stock, new FactoryProductIntent(FactoryInteractionKind.Withdraw, stock.Recipe, 0, stock.Item, count, hand: hand), player.package, personal);
            count = transfer.Count; if (count <= 0) return 0;
            WriteLauncher(factory, entity, stock.Count - count, stock.Inc - transfer.Inc);
        }
        if (hand) personal.ApplyHand(player);
        Commit(key, stock.Recipe, 0, stock.Item, count, deposit ? FactoryProductMessage.InputCommit : FactoryProductMessage.Commit, transfer.Inc);
        if (!deposit) { player.NotifyPackageAddItem(stock.Item, count, transfer.Inc); UIItemup.Up(stock.Item, count); }
        return count;
    }
    private FactoryProductPacket LauncherSnapshot(in ObjectKey key, PlanetFactory factory, in LauncherStock stock)
    {
        if (Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, key.NativeId, out var live) != true || live != key) return null;
        sequences.TryGetValue(key, out var sequence);
        return new FactoryProductPacket
        {
            Key = FactoryProductPacket.EncodeKey(key),
            Message = (byte)FactoryProductMessage.Snapshot,
            Sequence = sequence,
            Recipe = stock.Recipe,
            Products = new[] { stock.Item },
            Counts = new[] { stock.Count },
            OutputInc = new[] { stock.Inc }
        };
    }
    internal static bool ApplyLauncher(in LauncherStock stock, FactoryProductPacket packet, out int count, out int inc)
    {
        count = stock.Count; inc = stock.Inc;
        if (packet.Recipe != stock.Recipe) return false;
        if (packet.Message == (byte)FactoryProductMessage.Snapshot)
        {
            if (packet.Products?.Length != 1 || packet.Counts?.Length != 1 || packet.OutputInc?.Length != 1 || packet.Products[0] != stock.Item) return false;
            count = packet.Counts[0]; inc = packet.OutputInc[0];
            return count >= 0 && inc >= 0 && (long)inc <= (long)count * 10;
        }
        var sign = packet.Message == (byte)FactoryProductMessage.Commit ? -1 : packet.Message == (byte)FactoryProductMessage.InputCommit ? 1 : 0;
        var after = (long)stock.Count + sign * packet.Count; var afterInc = (long)stock.Inc + sign * packet.Inc;
        if (sign == 0 || packet.Slot != 0 || packet.Item != stock.Item || packet.Count <= 0 || packet.Inc < 0 || (long)packet.Inc > (long)packet.Count * 10 || after < 0 || after > int.MaxValue || afterInc < 0 || afterInc > int.MaxValue) return false;
        count = (int)after; inc = (int)afterInc; return true;
    }
    private void ApplyLauncherPacket(in ObjectKey key, PlanetFactory factory, in LauncherStock stock, FactoryProductPacket packet)
    {
        var initialized = sequences.TryGetValue(key, out var last);
        if (packet.Message == (byte)FactoryProductMessage.Snapshot)
        {
            if (!repairs.Contains(key) || packet.Sequence < last || !ApplyLauncher(stock, packet, out var count, out var inc)) return;
            WriteLauncher(factory, key.NativeId, count, inc); sequences[key] = packet.Sequence; repairs.Remove(key); repairTicks.Remove(key);
        }
        else if (packet.Sequence > last)
        {
            if (!initialized || repairs.Contains(key) || packet.Sequence != last + 1 || !ApplyLauncher(stock, packet, out var count, out var inc))
            { if (repairs.Add(key)) RequestRepair(key); }
            else { WriteLauncher(factory, key.NativeId, count, inc); sequences[key] = packet.Sequence; }
        }
    }
}
