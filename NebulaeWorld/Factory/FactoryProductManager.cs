using System;
using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Factory.Assembler;
using NebulaeWorld.Authority;

namespace NebulaeWorld.Factory;

/// <summary>Native production stays local; manual withdrawals settle once on the host.</summary>
internal sealed partial class FactoryProductManager : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<(ObjectKey Key, FactoryProductIntent Intent)> requests = [];
    private readonly HashSet<(ObjectKey Key, int Slot)> requestedSlots = [];
    private readonly Queue<FactoryProductPacket> incoming = [];
    private sealed class ClientResult
    {
        public long Sequence, InventorySequence;
        public readonly Queue<PlayerInventoryTransferLedger.Transfer> Credits = [];
    }
    private readonly Queue<ClientResult> results = [];
    private readonly Dictionary<ObjectKey, long> sequences = [];
    private readonly HashSet<ObjectKey> repairs = [];
    private readonly Dictionary<ObjectKey, long> repairTicks = [];
    private readonly List<ObjectKey> repairScan = [];
    private readonly InventoryReceiptGate receipts = new();
    private AuthorityEpoch epoch;
    private ConnectionEpoch connection;
    private long pending, checkpointTicket;
    private (ObjectKey Key, FactoryProductIntent Intent) activeRequest;
    public bool InventoryPending => pending != 0;
    public Func<FactorySystem, int, IReadOnlyList<PlayerInventoryTransferLedger.Transfer>> CollectRefunds { get; set; }
    public Func<FactorySystem, int, IReadOnlyList<PlayerInventoryTransferLedger.Transfer>> CollectLabRefunds { get; set; }

    private void EnsureWorld()
    {
        var current = Multiplayer.Session.Authority;
        if (epoch == current.Epoch && connection == current.Connection) return;
        epoch = current.Epoch; connection = current.Connection;
        lock (gate) { requests.Clear(); requestedSlots.Clear(); incoming.Clear(); results.Clear(); receipts.Clear(); }
        sequences.Clear(); repairs.Clear(); repairTicks.Clear(); pending = checkpointTicket = 0;
        fighterPorts.Clear(); fighterPortScan.Clear();
        Multiplayer.Session.InventoryTransfers.EnsureWorld();
    }

    private static bool TryView(PlanetFactory factory, int id, out AssemblerComponent view)
    {
        var binding = new FactoryMachine(factory, id); view = default;
        if (!binding.Valid) return false;
        view = binding.View; return true;
    }
    private bool Queue(PlanetFactory factory, int id, FactoryProductIntent intent)
    {
        EnsureWorld();
        if (!TryView(factory, id, out var machine) || Multiplayer.Session.AuthorityRuntime.WorldReplica?.TryFindKey(PoolKind.Entity, factory.planetId, machine.entityId, out var key) != true) return false;
        lock (gate)
        {
            if (requests.Count >= 32 || !requestedSlots.Add((key, intent.Slot))) return false;
            requests.Enqueue((key, intent)); return true;
        }
    }
    public bool Request(PlanetFactory factory, int id, int slot, bool hand = false)
    {
        if (!TryView(factory, id, out var machine) || machine.recipeId <= 0 || machine.produced == null || slot < 0 || slot >= machine.produced.Length || machine.produced[slot] <= 0) return false;
        return Queue(factory, id, new FactoryProductIntent(FactoryInteractionKind.Withdraw, machine.recipeId, slot, machine.recipeExecuteData.products[slot], machine.produced[slot], hand: hand));
    }
    public bool RequestInput(PlanetFactory factory, int id, int slot)
    {
        if (!TryView(factory, id, out var machine) || machine.recipeExecuteData?.requires == null || slot < 0 || slot >= machine.served.Length) return false;
        var count = (InputCapacity(machine, slot) - machine.served[slot]) / InputUnit(machine);
        if (count <= 0) return false;
        return Queue(factory, id, new FactoryProductIntent(FactoryInteractionKind.Deposit, machine.recipeId, slot, machine.recipeExecuteData.requires[slot], count));
    }
    public bool RequestRecipe(PlanetFactory factory, int id, int recipe) => TryView(factory, id, out var machine) &&
        Queue(factory, id, new FactoryProductIntent(FactoryInteractionKind.Recipe, machine.recipeId, nextRecipe: recipe));
    public bool RequestHandInput(PlanetFactory factory, int id, int slot, int wanted)
    {
        if (!TryView(factory, id, out var machine) || machine.recipeExecuteData?.requires == null || slot < 0 || slot >= machine.served.Length) return false;
        var player = GameMain.mainPlayer; var item = machine.recipeExecuteData.requires[slot];
        if (player.inhandItemId != item || player.inhandItemCount <= 0) return false;
        var count = Math.Min((InputCapacity(machine, slot) - machine.served[slot]) / InputUnit(machine), Math.Min(wanted <= 0 ? player.inhandItemCount : wanted, player.inhandItemCount));
        return count > 0 && Queue(factory, id, new FactoryProductIntent(FactoryInteractionKind.Deposit, machine.recipeId, slot, item, count, hand: true));
    }
    public bool RequestInputWithdrawal(PlanetFactory factory, int id, int slot, int wanted, bool hand)
    {
        if (!TryView(factory, id, out var machine) || machine.recipeExecuteData?.requires == null || slot < 0 || slot >= machine.served.Length) return false;
        var available = machine.served[slot] / InputUnit(machine); var count = Math.Min(wanted <= 0 ? available : wanted, available);
        return count > 0 && Queue(factory, id, new FactoryProductIntent(FactoryInteractionKind.InputWithdraw, machine.recipeId, slot, machine.recipeExecuteData.requires[slot], count, hand: hand));
    }
    public bool RequestAcceleration(PlanetFactory factory, int id, bool enable) => TryView(factory, id, out var machine) && machine.recipeId > 0 &&
        Queue(factory, id, new FactoryProductIntent(FactoryInteractionKind.Acceleration, machine.recipeId, slot: -2, count: enable ? 1 : 0));

    private static bool TryMachine(in ObjectKey key, out PlanetFactory factory, out int id)
    {
        factory = GameMain.galaxy?.PlanetById(key.Scope)?.factory; id = 0;
        if (factory?.factorySystem == null || key.NativeId <= 0 || key.NativeId >= factory.entityCursor || factory.entityPool[key.NativeId].id != key.NativeId) return false;
        var entity = factory.entityPool[key.NativeId]; id = entity.assemblerId > 0 ? entity.assemblerId : -entity.labId;
        return new FactoryMachine(factory, id).Valid;
    }

    public CommandOutcome Execute(in QueuedHostCommand command, global::Player player)
    {
        EnsureWorld();
        var session = Multiplayer.Session;
        if (!FactoryProductIntent.TryDecode(command.Packet.Payload, out var intent))
            return new CommandOutcome(CommandResultCode.RejectedInvalid);
        if (intent.Kind == FactoryInteractionKind.FighterPort) return ExecuteFighterPort(command, intent, player);
        if (!command.Packet.TryGetTargetKey(out var key) || key.Kind != PoolKind.Entity)
            return new CommandOutcome(CommandResultCode.RejectedTarget);
        var machineFound = TryMachine(key, out var factory, out var id);
        var fractionator = !machineFound && TryFractionator(key, out factory, out id);
        var launcherStock = default(LauncherStock);
        var launcher = !machineFound && !fractionator && TryLauncher(key, out factory, out launcherStock);
        if ((!machineFound && !fractionator && !launcher) ||
            session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, key.NativeId, out var current) != true || current != key)
            return new CommandOutcome(CommandResultCode.RejectedTarget);
        var actor = session.Server.Players.Get(command.ConnectionPlayerId)?.Data as PlayerData;
        var personal = actor?.Mecha as MechaData;
        var package = player?.package ?? actor?.Mecha?.Inventory;
        var alive = player?.isAlive ?? actor?.Life?.IsAlive == true;
        if (actor?.LocalPlanetId != key.Scope || !alive || package == null)
            return new CommandOutcome(CommandResultCode.RejectedUnauthorized);
        if (!session.InventoryTransfers.CanRecord(command.ConnectionPlayerId, command.Key.Sequence))
            return new CommandOutcome(CommandResultCode.RejectedNotReady);
        if (fractionator) return ExecuteFractionator(command, key, factory, id, intent, package, personal, player);
        if (launcher) return ExecuteLauncher(command, key, factory, launcherStock, intent, package, personal, player);
        var binding = new FactoryMachine(factory, id); var machine = binding.View;
        if (machine.recipeId != intent.Recipe) return new CommandOutcome(CommandResultCode.RejectedStale);
        if (intent.Kind == FactoryInteractionKind.Deposit)
        {
            if (!(intent.Hand ? personal != null && DepositHand(ref machine, intent, personal) : Deposit(ref machine, intent, package))) return new CommandOutcome(CommandResultCode.RejectedResource);
            session.InventoryTransfers.Record(command.ConnectionPlayerId, command.Key.Sequence, new[] { new PlayerInventoryTransferLedger.Transfer(intent.Item, -intent.Count, intent.Inc, intent.Hand) });
            if (intent.Hand && player != null) personal.ApplyHand(player);
            Commit(key, intent.Recipe, intent.Slot, intent.Item, intent.Count * InputUnit(machine), FactoryProductMessage.InputCommit, intent.Inc * InputUnit(machine));
            return new CommandOutcome(CommandResultCode.Applied, GameMain.gameTick, resourceRevision: command.Key.Sequence, resultData: FactoryInventoryReceipt.Encode(Array.Empty<PlayerInventoryTransferLedger.Transfer>()));
        }
        if (intent.Kind == FactoryInteractionKind.InputWithdraw)
        {
            if (intent.Hand && personal == null) return new CommandOutcome(CommandResultCode.RejectedNotReady);
            if (machine.incServed == null || intent.Slot < 0 || intent.Slot >= machine.incServed.Length) return new CommandOutcome(CommandResultCode.RejectedInvalid);
            var oldInc = machine.incServed[intent.Slot];
            var transfer = WithdrawInput(ref machine, intent, package, personal);
            if (transfer.Count <= 0) return new CommandOutcome(CommandResultCode.RejectedResource);
            session.InventoryTransfers.Record(command.ConnectionPlayerId, command.Key.Sequence, new[] { transfer });
            if (intent.Hand && player != null) personal.ApplyHand(player);
            Commit(key, machine.recipeId, intent.Slot, intent.Item, -transfer.Count * InputUnit(machine), FactoryProductMessage.InputCommit, machine.incServed[intent.Slot] - oldInc);
            return new CommandOutcome(CommandResultCode.Applied, GameMain.gameTick, resourceRevision: command.Key.Sequence, resultData: FactoryInventoryReceipt.Encode(new[] { transfer }));
        }
        if (intent.Kind == FactoryInteractionKind.Recipe)
        {
            if (!ValidRecipe(binding, intent.NextRecipe)) return new CommandOutcome(CommandResultCode.RejectedInvalid);
            if (!ChangeRecipe(binding, intent.NextRecipe, package, out var refunds)) return new CommandOutcome(CommandResultCode.RejectedResource);
            if (refunds.Count > 0) session.InventoryTransfers.Record(command.ConnectionPlayerId, command.Key.Sequence, refunds);
            return new CommandOutcome(CommandResultCode.Applied, GameMain.gameTick, resourceRevision: refunds.Count > 0 ? command.Key.Sequence : 0, resultData: FactoryInventoryReceipt.Encode(refunds));
        }
        if (intent.Kind == FactoryInteractionKind.Acceleration)
        {
            if (machine.recipeId <= 0) return new CommandOutcome(CommandResultCode.RejectedInvalid);
            LocalAcceleration(factory, id, intent.Count != 0);
            return new CommandOutcome(CommandResultCode.Applied, GameMain.gameTick, resultData: FactoryInventoryReceipt.Encode(Array.Empty<PlayerInventoryTransferLedger.Transfer>()));
        }
        var added = intent.Hand ? WithdrawToHand(ref machine, intent, personal) : Withdraw(ref machine, intent, package);
        if (added <= 0) return new CommandOutcome(CommandResultCode.RejectedResource);
        var output = new PlayerInventoryTransferLedger.Transfer(intent.Item, added, 0, intent.Hand);
        session.InventoryTransfers.Record(command.ConnectionPlayerId, command.Key.Sequence, new[] { output });
        if (intent.Hand && player != null) personal.ApplyHand(player);
        Commit(key, intent.Recipe, intent.Slot, intent.Item, added);
        return new CommandOutcome(CommandResultCode.Applied, GameMain.gameTick, ((long)intent.Item << 32) | (uint)added, command.Key.Sequence,
            intent.Hand ? FactoryInventoryReceipt.Encode(new[] { output }) : null);
    }

    private static bool ValidRecipe(FactoryMachine binding, int recipe)
    {
        if (recipe == -1) return binding.Laboratory;
        if (recipe == 0) return true;
        var selected = LDB.recipes.Select(recipe);
        return selected != null && selected.Type == (binding.Laboratory ? ERecipeType.Research : PlanetFactory.PrefabDescByModelIndex[binding.Factory.entityPool[binding.View.entityId].modelIndex].assemblerRecipeType) && GameMain.history.RecipeUnlocked(recipe);
    }
    internal static int InputUnit(in AssemblerComponent machine) => machine.recipeId == -1 ? 3600 : 1;
    internal static int InputCapacity(in AssemblerComponent machine, int slot) => machine.recipeId == -1 ? 360000 + machine.served[slot] % 3600 :
        machine.recipeType == ERecipeType.Research ? 100 : machine.recipeExecuteData.requireCounts[slot] * (machine.recipeType == ERecipeType.Smelt ? 50 : 20);

    internal static bool Deposit(ref AssemblerComponent machine, in FactoryProductIntent intent, StorageComponent package)
    {
        var requires = machine.recipeExecuteData?.requires;
        if (intent.Count <= 0 || intent.Inc < 0 || (long)intent.Inc > (long)intent.Count * 10 || machine.recipeId != intent.Recipe || requires == null ||
            intent.Slot < 0 || intent.Slot >= requires.Length || requires[intent.Slot] != intent.Item || package.GetItemCount(intent.Item) < intent.Count) return false;
        lock (machine.served)
        {
            var unit = InputUnit(machine);
            if ((long)intent.Count * unit > InputCapacity(machine, intent.Slot) - machine.served[intent.Slot]) return false;
            var item = intent.Item; var count = intent.Count;
            package.TakeTailItems(ref item, ref count, out var inc);
            if (count != intent.Count || inc != intent.Inc) { package.AddItemStacked(item, count, inc, out _); return false; }
            machine.served[intent.Slot] += count * unit; machine.incServed[intent.Slot] += inc * unit;
            return true;
        }
    }
    internal static bool DepositHand(ref AssemblerComponent machine, in FactoryProductIntent intent, MechaData personal)
    {
        var requires = machine.recipeExecuteData?.requires;
        if (machine.recipeId != intent.Recipe || requires == null || intent.Slot < 0 || intent.Slot >= requires.Length || requires[intent.Slot] != intent.Item ||
            intent.Count <= 0 || personal.HandItem != intent.Item || personal.HandCount < intent.Count) return false;
        lock (machine.served)
        {
            var unit = InputUnit(machine);
            if ((long)intent.Count * unit > InputCapacity(machine, intent.Slot) - machine.served[intent.Slot]) return false;
            var count = personal.TakeHand(intent.Item, intent.Count, out var inc);
            if (inc != intent.Inc) { personal.CreditHand(intent.Item, count, inc); return false; }
            machine.served[intent.Slot] += count * unit; machine.incServed[intent.Slot] += inc * unit; return true;
        }
    }
    internal static PlayerInventoryTransferLedger.Transfer WithdrawInput(ref AssemblerComponent machine, in FactoryProductIntent intent, StorageComponent package, MechaData personal)
    {
        var requires = machine.recipeExecuteData?.requires;
        if (machine.recipeId != intent.Recipe || requires == null || intent.Slot < 0 || intent.Slot >= requires.Length || requires[intent.Slot] != intent.Item || intent.Count <= 0) return default;
        lock (machine.served)
        {
            var unit = InputUnit(machine); var available = machine.served[intent.Slot] / unit;
            var residualInc = 0; var totalInc = machine.incServed[intent.Slot];
            if (unit > 1)
            {
                var fractional = new StorageComponent(1);
                fractional.grids[0] = new StorageComponent.GRID { itemId = intent.Item, count = machine.served[intent.Slot], inc = totalInc, stackSize = int.MaxValue };
                var fractionalItem = intent.Item; var fraction = machine.served[intent.Slot] % unit;
                fractional.TakeItemFromGrid(0, ref fractionalItem, ref fraction, out residualInc);
                totalInc = fractional.grids[0].inc / unit;
            }
            var native = new StorageComponent(1);
            native.grids[0] = new StorageComponent.GRID { itemId = intent.Item, count = available, inc = totalInc, stackSize = int.MaxValue };
            var item = intent.Item; var count = Math.Min(intent.Count, available);
            if (count <= 0) return default;
            native.TakeItemFromGrid(0, ref item, ref count, out var inc);
            int added, remainingInc;
            if (intent.Hand) { if (!personal.CreditHand(item, count, inc)) return default; added = count; remainingInc = 0; }
            else added = package.AddItemStacked(item, count, inc, out remainingInc);
            if (added <= 0) return default;
            machine.served[intent.Slot] -= added * unit;
            machine.incServed[intent.Slot] = unit > 1 && added == available ? residualInc : machine.incServed[intent.Slot] - (inc - remainingInc) * unit;
            return new PlayerInventoryTransferLedger.Transfer(item, added, inc - remainingInc, intent.Hand);
        }
    }
    internal static bool CreditAll(StorageComponent package, IReadOnlyList<PlayerInventoryTransferLedger.Transfer> refunds)
    {
        var probe = new StorageComponent(package.size) { type = package.type };
        Array.Copy(package.grids, probe.grids, package.size);
        foreach (var refund in refunds) if (probe.AddItemStacked(refund.Item, refund.Count, refund.Inc, out _) != refund.Count) return false;
        foreach (var refund in refunds)
            if (package.AddItemStacked(refund.Item, refund.Count, refund.Inc, out _) != refund.Count) throw new InvalidOperationException("Inventory changed during a parked-frame refund.");
        return true;
    }

    internal static int Withdraw(ref AssemblerComponent machine, in FactoryProductIntent intent, StorageComponent package)
    {
        var products = machine.recipeExecuteData?.products;
        if (machine.recipeId != intent.Recipe || machine.produced == null || products == null || intent.Slot < 0 ||
            intent.Slot >= products.Length || intent.Slot >= machine.produced.Length || products[intent.Slot] != intent.Item || intent.Count <= 0) return 0;
        lock (machine.produced)
        {
            var count = Math.Min(intent.Count, machine.produced[intent.Slot]);
            if (count <= 0) return 0;
            var added = package.AddItemStacked(intent.Item, count, 0, out _);
            machine.produced[intent.Slot] -= added;
            return added;
        }
    }

    internal static int WithdrawToHand(ref AssemblerComponent machine, in FactoryProductIntent intent, MechaData personal)
    {
        if (personal == null || machine.recipeId != intent.Recipe || machine.produced == null || machine.recipeExecuteData?.products == null || intent.Slot < 0 ||
            intent.Slot >= machine.produced.Length || machine.recipeExecuteData.products[intent.Slot] != intent.Item) return 0;
        var count = Math.Min(intent.Count, machine.produced[intent.Slot]);
        if (count <= 0 || !personal.CreditHand(intent.Item, count, 0)) return 0;
        machine.produced[intent.Slot] -= count; return count;
    }
    public int LocalWithdraw(PlanetFactory factory, int assembler, int slot, global::Player player, bool showPopup = true, bool hand = false)
    {
        EnsureWorld();
        if (!new FactoryMachine(factory, assembler).Valid) return 0;
        var binding = new FactoryMachine(factory, assembler); var machine = binding.View;
        if (machine.id != assembler || machine.produced == null || slot < 0 || slot >= machine.produced.Length ||
            machine.recipeExecuteData?.products == null || slot >= machine.recipeExecuteData.products.Length ||
            Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, machine.entityId, out var key) != true) return 0;
        var intent = new FactoryProductIntent(machine.recipeId, slot, machine.recipeExecuteData.products[slot], machine.produced[slot]);
        var personal = hand ? new MechaData(player) : null;
        var added = hand ? WithdrawToHand(ref machine, intent, personal) : Withdraw(ref machine, intent, player.package);
        if (added > 0)
        {
            if (hand) personal.ApplyHand(player);
            Commit(key, intent.Recipe, slot, intent.Item, added);
            player.NotifyPackageAddItem(intent.Item, added, 0);
            if (showPopup) UIItemup.Up(intent.Item, added);
        }
        return added;
    }

    public int LocalInput(PlanetFactory factory, int assembler, int slot, StorageComponent package)
    {
        EnsureWorld();
        var binding = new FactoryMachine(factory, assembler); var machine = binding.View;
        var requires = machine.recipeExecuteData?.requires;
        if (requires == null || slot < 0 || slot >= requires.Length || Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, machine.entityId, out var key) != true) return 0;
        lock (machine.served)
        {
            var unit = InputUnit(machine); var item = requires[slot]; var count = Math.Min((InputCapacity(machine, slot) - machine.served[slot]) / unit, package.GetItemCount(item));
            if (count <= 0) return 0;
            package.TakeTailItems(ref item, ref count, out var inc);
            machine.served[slot] += count * unit; machine.incServed[slot] += inc * unit;
            Commit(key, machine.recipeId, slot, item, count * unit, FactoryProductMessage.InputCommit, inc * unit);
            return count;
        }
    }
    public int LocalHandInput(PlanetFactory factory, int assembler, int slot, int wanted, global::Player player)
    {
        EnsureWorld();
        var binding = new FactoryMachine(factory, assembler); var machine = binding.View; var requires = machine.recipeExecuteData?.requires;
        if (requires == null || slot < 0 || slot >= requires.Length || player.inhandItemId != requires[slot] ||
            Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, machine.entityId, out var key) != true) return 0;
        var unit = InputUnit(machine); var count = Math.Min((InputCapacity(machine, slot) - machine.served[slot]) / unit, Math.Min(wanted <= 0 ? player.inhandItemCount : wanted, player.inhandItemCount));
        if (count <= 0) return 0;
        var item = requires[slot]; player.TakeItemFromPlayer(ref item, ref count, out var inc, false, null);
        machine.served[slot] += count * unit; machine.incServed[slot] += inc * unit;
        Commit(key, machine.recipeId, slot, item, count * unit, FactoryProductMessage.InputCommit, inc * unit); return count;
    }
    public int LocalInputWithdrawal(PlanetFactory factory, int assembler, int slot, int wanted, bool hand, global::Player player)
    {
        EnsureWorld();
        var binding = new FactoryMachine(factory, assembler); var machine = binding.View; var requires = machine.recipeExecuteData?.requires;
        if (requires == null || slot < 0 || slot >= requires.Length || Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, machine.entityId, out var key) != true) return 0;
        var available = machine.served[slot] / InputUnit(machine); var count = Math.Min(wanted <= 0 ? available : wanted, available); var oldInc = machine.incServed[slot];
        if (count <= 0) return 0;
        var personal = new MechaData(player);
        var transfer = WithdrawInput(ref machine, new FactoryProductIntent(FactoryInteractionKind.InputWithdraw, machine.recipeId, slot, requires[slot], count, hand: hand), player.package, personal);
        if (transfer.Count <= 0) return 0;
        if (hand) personal.ApplyHand(player); else player.NotifyPackageAddItem(transfer.Item, transfer.Count, transfer.Inc);
        Commit(key, machine.recipeId, slot, transfer.Item, -transfer.Count * InputUnit(machine), FactoryProductMessage.InputCommit, machine.incServed[slot] - oldInc); return transfer.Count;
    }
    public bool LocalRecipe(PlanetFactory factory, int assembler, int nextRecipe, StorageComponent package)
    {
        EnsureWorld();
        if (!new FactoryMachine(factory, assembler).Valid) return false;
        var binding = new FactoryMachine(factory, assembler);
        return ValidRecipe(binding, nextRecipe) && ChangeRecipe(binding, nextRecipe, package, out _);
    }
    public void LocalAcceleration(PlanetFactory factory, int assembler, bool enable)
    {
        EnsureWorld();
        var binding = new FactoryMachine(factory, assembler);
        foreach (var member in binding.Stack())
        {
            if (Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, member.View.entityId, out var key) != true) continue;
            // A member already in the requested state needs no record. A client that loaded this
            // planet received the native forceAccMode in its import slice, so both sides already
            // agree; broadcasting it would advance the member's sequence and fan a packet out to the
            // whole star without telling any subscriber anything it does not have. A member whose
            // state genuinely moves still commits, and a client that diverged is recovered by the
            // existing repair path rather than by a redundant broadcast.
            if (member.View.forceAccMode == enable) continue;
            member.SetAcceleration(enable); Commit(key, member.View.recipeId, -2, 0, enable ? 1 : 0, FactoryProductMessage.Acceleration);
        }
    }

    private bool ChangeRecipe(FactoryMachine root, int recipe, StorageComponent package, out IReadOnlyList<PlayerInventoryTransferLedger.Transfer> refunds)
    {
        refunds = Array.Empty<PlayerInventoryTransferLedger.Transfer>();
        var changed = new List<(FactoryMachine Machine, ObjectKey Key)>();
        var totals = new Dictionary<int, (int Count, int Inc)>();
        foreach (var member in root.Stack())
        {
            if (member.Id != root.Id && member.View.recipeId == recipe) continue;
            if (Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(root.Factory, member.View.entityId, out var key) != true) return false;
            var collected = member.Laboratory ? CollectLabRefunds?.Invoke(root.Factory.factorySystem, -member.Id) : CollectRefunds?.Invoke(root.Factory.factorySystem, member.Id);
            if (collected == null) return false;
            foreach (var credit in collected)
            {
                totals.TryGetValue(credit.Item, out var total);
                if ((long)total.Count + credit.Count > int.MaxValue || (long)total.Inc + credit.Inc > int.MaxValue) return false;
                totals[credit.Item] = (total.Count + credit.Count, total.Inc + credit.Inc);
            }
            changed.Add((member, key));
        }
        if (totals.Count > 32) return false;
        var credits = new List<PlayerInventoryTransferLedger.Transfer>();
        foreach (var total in totals) if (total.Value.Count > 0) credits.Add(new PlayerInventoryTransferLedger.Transfer(total.Key, total.Value.Count, total.Value.Inc));
        if (!CreditAll(package, credits)) return false;
        foreach (var member in changed)
        {
            member.Machine.SetRecipe(0); member.Machine.SetRecipe(recipe);
            Commit(member.Key, recipe, -1, 0, 0, FactoryProductMessage.Recipe);
        }
        refunds = credits; return true;
    }

    private void Commit(in ObjectKey key, int recipe, int slot, int item, int count, FactoryProductMessage message = FactoryProductMessage.Commit, int inc = 0)
    {
        sequences.TryGetValue(key, out var sequence); sequences[key] = ++sequence;
        Multiplayer.Session.Network.SendPacketToStar(new FactoryProductPacket
        {
            Key = FactoryProductPacket.EncodeKey(key),
            Message = (byte)message,
            Sequence = sequence,
            Recipe = recipe,
            Slot = slot,
            Item = item,
            Count = count,
            Inc = inc
        }, key.Scope / 100);
    }

    public FactoryProductPacket Snapshot(FactoryProductPacket request)
    {
        EnsureWorld();
        if (!request.TryKey(epoch, out var key)) return null;
        if (TryBattleBase(key, out var baseFactory, out var battleBase))
        {
            if (Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(baseFactory, key.NativeId, out var live) != true || live != key) return null;
            var snapshot = FighterPortSnapshot(key, battleBase);
            if (!fighterPorts.ContainsKey(key)) fighterPorts[key] = snapshot;
            return snapshot;
        }
        if (TryFractionator(key, out var fractionatorFactory, out var fractionatorId)) return FractionatorSnapshot(key, fractionatorFactory, fractionatorId);
        if (TryLauncher(key, out var launcherFactory, out var launcherStock)) return LauncherSnapshot(key, launcherFactory, launcherStock);
        if (!TryMachine(key, out var factory, out var id) ||
            Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, key.NativeId, out var current) != true || current != key) return null;
        var binding = new FactoryMachine(factory, id); var machine = binding.View;
        sequences.TryGetValue(key, out var sequence);
        return new FactoryProductPacket
        {
            Key = request.Key,
            Message = (byte)FactoryProductMessage.Snapshot,
            Sequence = sequence,
            Recipe = machine.recipeId,
            Products = machine.recipeExecuteData?.products == null ? Array.Empty<int>() : (int[])machine.recipeExecuteData.products.Clone(),
            Counts = machine.produced == null ? Array.Empty<int>() : (int[])machine.produced.Clone(),
            Inputs = machine.recipeExecuteData?.requires == null ? Array.Empty<int>() : (int[])machine.recipeExecuteData.requires.Clone(),
            Served = machine.served == null ? Array.Empty<int>() : (int[])machine.served.Clone(),
            IncServed = machine.incServed == null ? Array.Empty<int>() : (int[])machine.incServed.Clone(),
            ForceAcceleration = machine.forceAccMode
        };
    }

    public void Receive(FactoryProductPacket packet)
    {
        EnsureWorld();
        var portSnapshot = packet.Message == (byte)FactoryProductMessage.FighterPortsSnapshot;
        var max = portSnapshot ? 256 : 16;
        if (!packet.TryKey(epoch, out _) || packet.Sequence < 0 || packet.Message < (byte)FactoryProductMessage.Commit || packet.Message > (byte)FactoryProductMessage.FighterPortsSnapshot ||
            packet.Counts == null || packet.Products == null || packet.OutputInc == null || packet.OutputInc.Length > max || packet.Counts.Length > max || packet.Products.Length > max ||
            packet.Inputs == null || packet.Served == null || packet.IncServed == null || packet.Filters == null || packet.Filters.Length > max ||
            packet.Inputs.Length > max || packet.Served.Length > max || packet.IncServed.Length > max) return;
        lock (gate)
        {
            // Join replay is already byte-bounded. Keep those events until the entity baseline
            // supplies their generation identities; a live-session burst keeps its usual cap.
            if (incoming.Count < 256 || Multiplayer.Session.JoinSync?.LocalPending == true)
                incoming.Enqueue(packet.Copy());
        }
    }

    public void FactoryImported(int planet)
    {
        // The native import is a new cut. An event already included in it cannot be subtracted again.
        foreach (var key in new List<ObjectKey>(sequences.Keys)) if (key.Scope == planet) sequences.Remove(key);
        repairs.RemoveWhere(key => key.Scope == planet);
        foreach (var key in new List<ObjectKey>(repairTicks.Keys)) if (key.Scope == planet) repairTicks.Remove(key);
    }
    public void ReceiveResult(AuthorityCommandResultPacket packet)
    {
        lock (gate)
        {
            if (packet.Sequence != pending || new AuthorityEpoch(packet.EpochHigh, packet.EpochLow) != epoch || new ConnectionEpoch(packet.ConnectionEpoch) != connection) return;
            if (!receipts.TryAccept(new CommandKey(epoch, connection, packet.Sequence))) return;
            var result = new ClientResult { Sequence = packet.Sequence, InventorySequence = packet.ResourceRevision };
            if ((CommandResultCode)packet.ResultCode != CommandResultCode.Applied)
            {
                if (activeRequest.Intent.Kind != FactoryInteractionKind.FighterPort && (CommandResultCode)packet.ResultCode is CommandResultCode.RejectedResource or CommandResultCode.RejectedStale)
                    if (repairs.Add(activeRequest.Key)) RequestRepair(activeRequest.Key);
                if (activeRequest.Intent.Kind == FactoryInteractionKind.Deposit)
                    result.Credits.Enqueue(new PlayerInventoryTransferLedger.Transfer(activeRequest.Intent.Item, activeRequest.Intent.Count, activeRequest.Intent.Inc, activeRequest.Intent.Hand));
                if (activeRequest.Intent.Kind == FactoryInteractionKind.FighterPort && activeRequest.Intent.Count > 0)
                    result.Credits.Enqueue(new PlayerInventoryTransferLedger.Transfer(activeRequest.Intent.Item, activeRequest.Intent.Count, activeRequest.Intent.Inc, true));
            }
            else if (packet.ResultData?.Length > 0)
            {
                if (!FactoryInventoryReceipt.TryDecode(packet.ResultData, out var credits)) throw new InvalidOperationException("Invalid factory inventory receipt.");
                foreach (var credit in credits) result.Credits.Enqueue(credit);
            }
            else
            {
                var item = (int)(packet.TransactionId >> 32); var count = (int)(uint)packet.TransactionId;
                if (activeRequest.Intent.Kind != FactoryInteractionKind.Withdraw || item != activeRequest.Intent.Item || count <= 0 || count > activeRequest.Intent.Count)
                    throw new InvalidOperationException("Invalid factory withdrawal receipt.");
                result.Credits.Enqueue(new PlayerInventoryTransferLedger.Transfer(item, count, 0));
            }
            results.Enqueue(result);
        }
    }

    public void TickClient()
    {
        if (Multiplayer.Session.JoinSync?.LocalPending == true && !Multiplayer.Session.JoinSync.AuthorityReady) return;
        EnsureWorld();
        while (true)
        {
            FactoryProductPacket packet;
            lock (gate) { if (incoming.Count == 0) break; packet = incoming.Dequeue(); }
            if (!packet.TryKey(epoch, out var key)) continue;
            if (Multiplayer.Session.AuthorityRuntime.WorldReplica?.TryFindKey(PoolKind.Entity, key.Scope, key.NativeId, out var current) != true || current != key) continue;
            if (packet.Message == (byte)FactoryProductMessage.FighterPortsSnapshot)
            {
                if (TryBattleBase(key, out _, out var battleBase) && (!sequences.TryGetValue(key, out var before) || packet.Sequence >= before) && ApplyFighterPortSnapshot(battleBase, packet))
                { sequences[key] = packet.Sequence; repairs.Remove(key); repairTicks.Remove(key); }
                continue;
            }
            if (TryFractionator(key, out var fractionatorFactory, out var fractionatorId)) { ApplyFractionatorPacket(key, fractionatorFactory, fractionatorId, packet); continue; }
            if (TryLauncher(key, out var launcherFactory, out var launcherStock)) { ApplyLauncherPacket(key, launcherFactory, launcherStock, packet); continue; }
            if (!TryMachine(key, out var factory, out var id)) continue;
            var initialized = sequences.TryGetValue(key, out var last);
            var binding = new FactoryMachine(factory, id); var machine = binding.View;
            if (packet.Message == (byte)FactoryProductMessage.Snapshot)
            {
                if (repairs.Contains(key) && packet.Sequence >= last && packet.Recipe != machine.recipeId)
                {
                    if (packet.Recipe < -1 || packet.Recipe == -1 && !binding.Laboratory || packet.Recipe > 0 && LDB.recipes.Select(packet.Recipe) == null) continue;
                    binding.SetRecipe(0); binding.SetRecipe(packet.Recipe); machine = binding.View;
                }
                if (!repairs.Contains(key) || packet.Sequence < last || !ApplySnapshot(ref machine, packet)) continue;
                binding.SetAcceleration(packet.ForceAcceleration); sequences[key] = packet.Sequence; repairs.Remove(key); repairTicks.Remove(key);
            }
            else if (packet.Sequence > last)
            {
                if (!initialized || repairs.Contains(key) || packet.Sequence != last + 1 || !ApplyEvent(binding, ref machine, packet))
                {
                    if (repairs.Add(key)) RequestRepair(key);
                }
                else sequences[key] = packet.Sequence;
            }
        }
        if (repairs.Count > 0)
        {
            // Reuse scratch storage while retrying pending repairs to avoid per-frame allocation.
            repairScan.Clear();
            repairScan.AddRange(repairs);
            foreach (var key in repairScan)
            {
                if (Multiplayer.Session.AuthorityRuntime.WorldReplica?.TryFindKey(PoolKind.Entity, key.Scope, key.NativeId, out var live) != true || live != key)
                { repairs.Remove(key); repairTicks.Remove(key); continue; }
                if (!repairTicks.TryGetValue(key, out var sent) || GameMain.gameTick - sent >= 120) RequestRepair(key);
            }
        }
        lock (gate)
        {
            if (results.Count > 0)
            {
                var result = results.Peek();
                if (result.Sequence != pending) results.Dequeue();
                else
                {
                    while (result.Credits.Count > 0)
                    {
                        var credit = result.Credits.Dequeue();
                        int added, remainingInc;
                        if (credit.Hand)
                        {
                            var item = credit.Item; var count = credit.Count; var inc = credit.Inc;
                            GameMain.mainPlayer.SendItemToPlayer(ref item, ref count, ref inc, false, null);
                            added = credit.Count - count; remainingInc = inc;
                        }
                        else added = GameMain.mainPlayer.package.AddItemStacked(credit.Item, credit.Count, credit.Inc, out remainingInc);
                        if (added > 0) { GameMain.mainPlayer.NotifyPackageAddItem(credit.Item, added, credit.Inc - remainingInc); UIItemup.Up(credit.Item, added); }
                        if (added < credit.Count)
                        {
                            var rest = new Queue<PlayerInventoryTransferLedger.Transfer>();
                            rest.Enqueue(new PlayerInventoryTransferLedger.Transfer(credit.Item, credit.Count - added, remainingInc, credit.Hand));
                            foreach (var other in result.Credits) rest.Enqueue(other);
                            result.Credits.Clear(); foreach (var other in rest) result.Credits.Enqueue(other);
                            return;
                        }
                    }
                    if (result.InventorySequence > 0) Multiplayer.Session.InventoryTransfers.Applied(result.InventorySequence);
                    receipts.Finish(new CommandKey(epoch, connection, result.Sequence));
                    requestedSlots.Remove((activeRequest.Key, activeRequest.Intent.Slot)); pending = 0; results.Dequeue();
                }
            }
        }
    }

    public void DispatchPending()
    {
        EnsureWorld();
        lock (gate)
        {
            if (pending != 0 || requests.Count == 0 || Multiplayer.Session.InventoryTransfers.Pending) return;
            if (!Multiplayer.Session.Life.EnsureInventoryCheckpoint(ref checkpointTicket)) return;
            checkpointTicket = 0;
            var request = requests.Dequeue();
            if (request.Intent.Kind == FactoryInteractionKind.FighterPort && (request.Intent.NextRecipe & 3) != 2)
            {
                var player = GameMain.mainPlayer;
                var item = player.inhandItemCount > 0 ? player.inhandItemId : 0;
                var count = player.inhandItemCount;
                var inc = player.inhandItemInc;
                if (count > 0) player.TakeItemFromPlayer(ref item, ref count, out inc, false, null);
                request = (request.Key, new FactoryProductIntent(FactoryInteractionKind.FighterPort, request.Intent.Recipe,
                    request.Intent.Slot, count > 0 ? item : 0, count, inc, request.Intent.NextRecipe));
            }
            if (request.Intent.Kind == FactoryInteractionKind.Deposit)
            {
                var item = request.Intent.Item;
                var count = Math.Min(request.Intent.Count, request.Intent.Hand
                    ? GameMain.mainPlayer.inhandItemId == item ? GameMain.mainPlayer.inhandItemCount : 0
                    : GameMain.mainPlayer.package.GetItemCount(item));
                if (count <= 0) { requestedSlots.Remove((request.Key, request.Intent.Slot)); return; }
                int inc;
                if (request.Intent.Hand) GameMain.mainPlayer.TakeItemFromPlayer(ref item, ref count, out inc, false, null);
                else GameMain.mainPlayer.package.TakeTailItems(ref item, ref count, out inc);
                request = (request.Key, new FactoryProductIntent(FactoryInteractionKind.Deposit, request.Intent.Recipe, request.Intent.Slot, item, count, inc, hand: request.Intent.Hand));
            }
            if (Multiplayer.Session.CombatAuthority.SendCommand(request.Key, FactoryProductIntent.Category, request.Intent.Encode(), out var sequence))
            {
                activeRequest = request; pending = sequence;
                if (Multiplayer.Session.WorldPaused) Multiplayer.Session.Network.SendPacket(new NebulaeModel.Packets.GameStates.GamePauseStepRequest());
            }
            else
            {
                requestedSlots.Remove((request.Key, request.Intent.Slot));
                if (request.Intent.Kind == FactoryInteractionKind.Deposit || request.Intent.Kind == FactoryInteractionKind.FighterPort && request.Intent.Count > 0)
                {
                    var item = request.Intent.Item; var count = request.Intent.Count; var inc = request.Intent.Inc;
                    GameMain.mainPlayer.SendItemToPlayer(ref item, ref count, ref inc, request.Intent.Kind != FactoryInteractionKind.FighterPort && !request.Intent.Hand, null);
                }
            }
        }
    }

    public bool JoinReady { get { lock (gate) return incoming.Count == 0 && repairs.Count == 0; } }

    internal static bool ApplyCommit(ref AssemblerComponent machine, FactoryProductPacket packet)
    {
        if (packet.Count <= 0 || packet.Recipe != machine.recipeId || machine.produced == null || machine.recipeExecuteData?.products == null ||
            packet.Slot < 0 || packet.Slot >= machine.produced.Length || packet.Slot >= machine.recipeExecuteData.products.Length ||
            packet.Item != machine.recipeExecuteData.products[packet.Slot] || machine.produced[packet.Slot] < packet.Count) return false;
        machine.produced[packet.Slot] -= packet.Count; return true;
    }
    private static bool ApplyEvent(FactoryMachine binding, ref AssemblerComponent machine, FactoryProductPacket packet)
    {
        switch ((FactoryProductMessage)packet.Message)
        {
            case FactoryProductMessage.Commit: return ApplyCommit(ref machine, packet);
            case FactoryProductMessage.InputCommit:
                var requires = machine.recipeExecuteData?.requires;
                if (packet.Recipe != machine.recipeId || requires == null || packet.Slot < 0 || packet.Slot >= requires.Length ||
                    requires[packet.Slot] != packet.Item || packet.Count == 0 || Math.Abs((long)packet.Inc) > Math.Abs((long)packet.Count) * 10 || Math.Sign(packet.Count) != Math.Sign(packet.Inc) && packet.Inc != 0 ||
                    (long)machine.served[packet.Slot] + packet.Count < 0 || (long)machine.incServed[packet.Slot] + packet.Inc < 0 ||
                    (long)machine.served[packet.Slot] + packet.Count > int.MaxValue || (long)machine.incServed[packet.Slot] + packet.Inc > int.MaxValue) return false;
                machine.served[packet.Slot] += packet.Count; machine.incServed[packet.Slot] += packet.Inc; return true;
            case FactoryProductMessage.Recipe:
                if (packet.Recipe < -1 || packet.Recipe == -1 && !binding.Laboratory || packet.Recipe > 0 && LDB.recipes.Select(packet.Recipe) == null) return false;
                binding.SetRecipe(0); binding.SetRecipe(packet.Recipe); machine = binding.View; return true;
            case FactoryProductMessage.Acceleration:
                if (packet.Recipe != machine.recipeId || packet.Count is < 0 or > 1) return false;
                binding.SetAcceleration(packet.Count != 0); return true;
            default: return false;
        }
    }
    internal static bool ApplySnapshot(ref AssemblerComponent machine, FactoryProductPacket packet)
    {
        if (packet.Recipe != machine.recipeId || packet.Counts == null || packet.Products == null) return false;
        if (machine.recipeId == 0)
        {
            if (packet.Counts.Length != 0 || packet.Products.Length != 0 || packet.Served?.Length != 0) return false;
            machine.forceAccMode = packet.ForceAcceleration; return true;
        }
        if (machine.produced == null ||
            packet.Counts.Length > 16 || packet.Counts.Length != machine.produced.Length || machine.recipeExecuteData?.products == null ||
            packet.Products.Length != packet.Counts.Length || packet.Products.Length != machine.recipeExecuteData.products.Length || packet.Inputs == null || packet.Served == null || packet.IncServed == null ||
            packet.Inputs.Length > 16 || packet.Inputs.Length != machine.recipeExecuteData.requires.Length || packet.Served.Length != packet.Inputs.Length || packet.IncServed.Length != packet.Inputs.Length) return false;
        for (var i = 0; i < packet.Counts.Length; i++) if (packet.Counts[i] < 0 || packet.Products[i] != machine.recipeExecuteData.products[i]) return false;
        for (var i = 0; i < packet.Served.Length; i++) if (packet.Served[i] < 0 || packet.IncServed[i] < 0 || packet.Inputs[i] != machine.recipeExecuteData.requires[i]) return false;
        Array.Copy(packet.Counts, machine.produced, packet.Counts.Length);
        if (packet.Served.Length > 0)
        { Array.Copy(packet.Served, machine.served, packet.Served.Length); Array.Copy(packet.IncServed, machine.incServed, packet.Served.Length); }
        machine.forceAccMode = packet.ForceAcceleration; return true;
    }
    private void RequestRepair(in ObjectKey key)
    {
        repairTicks[key] = GameMain.gameTick;
        Multiplayer.Session.Network.SendPacket(new FactoryProductPacket { Key = FactoryProductPacket.EncodeKey(key), Message = (byte)FactoryProductMessage.RequestSnapshot });
    }
    public void Dispose() { lock (gate) { requests.Clear(); requestedSlots.Clear(); incoming.Clear(); results.Clear(); receipts.Clear(); } sequences.Clear(); repairs.Clear(); repairTicks.Clear(); fighterPorts.Clear(); fighterPortScan.Clear(); pending = 0; }
}
