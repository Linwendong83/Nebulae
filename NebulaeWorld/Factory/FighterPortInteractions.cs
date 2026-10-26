using System.Collections.Generic;
using System.Linq;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Factory.Assembler;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;

namespace NebulaeWorld.Factory;

internal sealed partial class FactoryProductManager
{
    private readonly Dictionary<ObjectKey, FactoryProductPacket> fighterPorts = [];
    private readonly List<ObjectKey> fighterPortScan = [];

    public bool RequestFleetConfiguration(PlanetFactory factory, int entity, int item)
    {
        EnsureWorld();
        if (factory == null || Multiplayer.Session.AuthorityRuntime.WorldReplica?.TryFindKey(
            PoolKind.Entity, factory.planetId, entity, out var key) != true) return false;
        lock (gate)
        {
            if (requests.Count >= 32 || !requestedSlots.Add((key, -1))) return false;
            requests.Enqueue((key, new FactoryProductIntent(FactoryInteractionKind.FleetConfigure, -5, item: item)));
            return true;
        }
    }

    public bool LocalFleetConfiguration(PlanetFactory factory, int entity, int item, global::Player player)
    {
        EnsureWorld();
        if (factory == null || player == null || Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(
            factory, entity, out var key) != true || !TryBattleBase(key, out _, out var battleBase)) return false;
        using var incoming = Multiplayer.Session.Storage.IsIncomingRequest.On();
        if (!FleetConfiguration.TryConfigureGround(battleBase.combatModule, 0, item, battleBase.storage, player.package, out _)) return false;
        CommitFighterPorts(key);
        if (Multiplayer.Session.WorldPaused) Multiplayer.Session.World.HostStepWorld();
        return true;
    }

    private CommandOutcome ExecuteFleetConfiguration(in QueuedHostCommand command, FactoryProductIntent intent, global::Player player)
    {
        var session = Multiplayer.Session;
        var actor = session.Server.Players.Get(command.ConnectionPlayerId)?.Data as PlayerData;
        if (actor?.Life?.IsAlive != true || player == null || !session.InventoryTransfers.CanRecord(command.ConnectionPlayerId, command.Key.Sequence))
            return new CommandOutcome(CommandResultCode.RejectedNotReady);
        if (!command.Packet.TryGetTargetKey(out var key) || key.Kind != PoolKind.Entity || actor.LocalPlanetId != key.Scope ||
            !TryBattleBase(key, out var factory, out var battleBase) || session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(
                factory, key.NativeId, out var current) != true || current != key)
            return new CommandOutcome(CommandResultCode.RejectedTarget);
        using var incoming = session.Storage.IsIncomingRequest.On();
        if (LDB.items.Select(intent.Item)?.isGroundFighter != true) return new CommandOutcome(CommandResultCode.RejectedInvalid);
        if (!FleetConfiguration.IsCurrent(battleBase.combatModule, 0, intent.Item) && !FleetConfiguration.TryPrepare(battleBase.combatModule, 0))
            return new CommandOutcome(CommandResultCode.RejectedNotReady);
        if (!FleetConfiguration.TryConfigureGround(battleBase.combatModule, 0, intent.Item, battleBase.storage, player.package, out var credits))
            return new CommandOutcome(CommandResultCode.RejectedResource);
        if (credits.Count > 0) session.InventoryTransfers.Record(command.ConnectionPlayerId, command.Key.Sequence, credits);
        CommitFighterPorts(key);
        return new CommandOutcome(CommandResultCode.Applied, GameMain.gameTick,
            resourceRevision: credits.Count > 0 ? command.Key.Sequence : 0, resultData: FactoryInventoryReceipt.Encode(credits));
    }

    public void ObserveFighterPorts(PlanetFactory factory, int entity)
    {
        EnsureWorld();
        if (Multiplayer.Session.IsClient)
        {
            if (Multiplayer.Session.AuthorityRuntime.WorldReplica?.TryFindKey(PoolKind.Entity, factory.planetId, entity, out var key) == true)
                Multiplayer.Session.Network.SendPacket(new FactoryProductPacket { Key = FactoryProductPacket.EncodeKey(key), Message = (byte)FactoryProductMessage.RequestSnapshot });
        }
        else if (Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, entity, out var key) == true && !fighterPorts.ContainsKey(key)) fighterPorts[key] = null;
    }

    public bool RequestFighterPort(PlanetFactory factory, int entity, int fleet, int port, bool space, int gesture, int item = 0)
    {
        EnsureWorld();
        var key = default(ObjectKey);
        if (factory != null && Multiplayer.Session.AuthorityRuntime.WorldReplica?.TryFindKey(PoolKind.Entity, factory.planetId, entity, out key) != true) return false;
        var slot = fleet * 256 + port;
        lock (gate)
        {
            if (requests.Count >= 32 || !requestedSlots.Add((key, slot))) return false;
            requests.Enqueue((key, new FactoryProductIntent(FactoryInteractionKind.FighterPort, factory == null ? -6 : -5,
                slot, item, nextRecipe: gesture | (space ? FighterPortOperation.Space : 0))));
            return true;
        }
    }

    private static bool TryBattleBase(in ObjectKey key, out PlanetFactory factory, out BattleBaseComponent battleBase)
    {
        factory = GameMain.galaxy?.PlanetById(key.Scope)?.factory; battleBase = null;
        if (factory?.defenseSystem == null || key.NativeId <= 0 || key.NativeId >= factory.entityCursor || factory.entityPool[key.NativeId].id != key.NativeId) return false;
        var id = factory.entityPool[key.NativeId].battleBaseId;
        var pool = factory.defenseSystem.battleBases;
        if (id <= 0 || id >= pool.cursor) return false;
        battleBase = pool.buffer[id];
        return battleBase?.id == id && battleBase.entityId == key.NativeId && battleBase.storage != null &&
            battleBase.combatModule?.moduleFleets?.Length > 0 && battleBase.combatModule.moduleFleets[0].fighters != null;
    }

    public bool LocalFighterPort(PlanetFactory factory, int entity, int fleet, int port, bool space, int gesture, int item, global::Player player)
    {
        var personal = new MechaData(player);
        var module = space ? player.mecha.spaceCombatModule : player.mecha.groundCombatModule;
        var storage = player.mecha.fighterStorage;
        var key = default(ObjectKey); BattleBaseComponent battleBase = null;
        if (factory != null)
        {
            if (Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, entity, out key) != true || !TryBattleBase(key, out _, out battleBase)) return false;
            module = battleBase.combatModule; storage = battleBase.storage;
        }
        if (!ApplyPort(module, fleet, port, space, gesture, item, storage, player.package, personal, out _)) return false;
        personal.ApplyHand(player);
        if (battleBase != null) CommitFighterPorts(key);
        if (Multiplayer.Session.WorldPaused) Multiplayer.Session.World.HostStepWorld();
        return true;
    }

    private CommandOutcome ExecuteFighterPort(in QueuedHostCommand command, FactoryProductIntent intent, global::Player player)
    {
        var session = Multiplayer.Session;
        var actor = session.Server.Players.Get(command.ConnectionPlayerId)?.Data as PlayerData;
        var personal = actor?.Mecha as MechaData;
        if (personal == null || actor.Life?.IsAlive != true || player == null) return new CommandOutcome(CommandResultCode.RejectedNotReady);
        var configure = (intent.NextRecipe & 3) == FighterPortOperation.Configure;
        if (!configure && (personal.HandItem != intent.Item || personal.HandCount != intent.Count || personal.HandInc != intent.Inc))
            return new CommandOutcome(CommandResultCode.RejectedStale);
        if (!session.InventoryTransfers.CanRecord(command.ConnectionPlayerId, command.Key.Sequence)) return new CommandOutcome(CommandResultCode.RejectedNotReady);
        var space = (intent.NextRecipe & FighterPortOperation.Space) != 0;
        var module = space ? player.mecha.spaceCombatModule : player.mecha.groundCombatModule;
        var storage = player.mecha.fighterStorage;
        var key = default(ObjectKey); BattleBaseComponent battleBase = null;
        if (intent.Recipe == -5)
        {
            if (space || intent.Slot / 256 != 0 || !command.Packet.TryGetTargetKey(out key) || key.Kind != PoolKind.Entity || actor.LocalPlanetId != key.Scope ||
                !TryBattleBase(key, out var factory, out battleBase) || session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, key.NativeId, out var current) != true || current != key)
                return new CommandOutcome(CommandResultCode.RejectedTarget);
            module = battleBase.combatModule; storage = battleBase.storage;
        }
        var before = new MechaData { HandItem = personal.HandItem, HandCount = personal.HandCount, HandInc = personal.HandInc };
        if (!ApplyPort(module, intent.Slot / 256, intent.Slot % 256, space, intent.NextRecipe, intent.Item, storage, player.package, personal, out var credits))
            return new CommandOutcome(CommandResultCode.RejectedResource);
        personal.ApplyHand(player);
        var transfers = new List<PlayerInventoryTransferLedger.Transfer>(credits);
        if (!configure && before.HandCount > 0) transfers.Add(new PlayerInventoryTransferLedger.Transfer(before.HandItem, -before.HandCount, before.HandInc, true));
        if (!configure && personal.HandCount > 0) transfers.Add(new PlayerInventoryTransferLedger.Transfer(personal.HandItem, personal.HandCount, personal.HandInc, true));
        if (transfers.Count > 0) session.InventoryTransfers.Record(command.ConnectionPlayerId, command.Key.Sequence, transfers);
        if (!configure && personal.HandCount > 0) credits.Add(new PlayerInventoryTransferLedger.Transfer(personal.HandItem, personal.HandCount, personal.HandInc, true));
        if (battleBase != null) CommitFighterPorts(key);
        else session.CombatAuthority.FighterPortChanged(command.ConnectionPlayerId);
        return new CommandOutcome(CommandResultCode.Applied, GameMain.gameTick, resourceRevision: transfers.Count > 0 ? command.Key.Sequence : 0,
            resultData: FactoryInventoryReceipt.Encode(credits));
    }

    private static bool ApplyPort(CombatModuleComponent module, int fleet, int port, bool space, int gesture, int item, StorageComponent storage,
        StorageComponent package, MechaData hand, out List<PlayerInventoryTransferLedger.Transfer> credits)
    {
        credits = null;
        if (module?.moduleFleets == null || fleet < 0 || fleet >= module.fleetCount || fleet >= module.moduleFleets.Length) return false;
        if (module.moduleFleets[fleet].fighters == null) return false;
        var unlocked = FighterPortOperation.UnlockedPorts(module.moduleFleets[fleet],
            space ? GameMain.history.spaceFleetPortCount : GameMain.history.groundFleetPortCount, space);
        using var incoming = Multiplayer.Session.Storage.IsIncomingRequest.On();
        return FighterPortOperation.TryApply(ref module.moduleFleets[fleet], unlocked, port, space, gesture, item, storage, package, hand, out credits);
    }

    private FactoryProductPacket FighterPortSnapshot(ObjectKey key, BattleBaseComponent battleBase)
    {
        sequences.TryGetValue(key, out var sequence);
        var fleet = battleBase.combatModule.moduleFleets[0]; var storage = battleBase.storage;
        return new FactoryProductPacket
        {
            Key = FactoryProductPacket.EncodeKey(key),
            Message = (byte)FactoryProductMessage.FighterPortsSnapshot,
            Sequence = sequence,
            Recipe = fleet.protoId,
            Products = fleet.fighters.Select(f => f.itemId).ToArray(),
            Counts = fleet.fighters.Select(f => f.count).ToArray(),
            Inputs = storage.grids.Select(g => g.itemId).ToArray(),
            Served = storage.grids.Select(g => g.count).ToArray(),
            IncServed = storage.grids.Select(g => g.inc).ToArray(),
            Filters = storage.grids.Select(g => g.filter).ToArray()
        };
    }
    private void CommitFighterPorts(ObjectKey key)
    {
        EnsureWorld();
        fighterPorts[key] = null;
    }

    // Auto-replenishment runs after a manual gesture. Publish its final port and hangar
    // state at the completed-frame boundary, then keep watched bases current as fleets return.
    public void TickHostFighterPorts(long tick, bool force = false)
    {
        EnsureWorld();
        fighterPortScan.Clear(); fighterPortScan.AddRange(fighterPorts.Keys);
        foreach (var key in fighterPortScan)
        {
            var previous = fighterPorts[key];
            if (previous != null && !force && tick % 6 != 0) continue;
            if (!TryBattleBase(key, out var factory, out var battleBase) ||
                Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, key.NativeId, out var current) != true || current != key)
            { fighterPorts.Remove(key); continue; }
            if (previous != null && FighterPortsMatch(battleBase, previous)) continue;
            sequences.TryGetValue(key, out var sequence); sequences[key] = sequence + 1;
            var snapshot = FighterPortSnapshot(key, battleBase);
            fighterPorts[key] = snapshot;
            Multiplayer.Session.Network.SendPacketToStar(snapshot, key.Scope / 100);
        }
    }

    internal static bool FighterPortsMatch(BattleBaseComponent battleBase, FactoryProductPacket packet)
    {
        var fleet = battleBase.combatModule.moduleFleets[0]; var grids = battleBase.storage.grids;
        if (packet.Recipe != fleet.protoId || packet.Products.Length != fleet.fighters.Length || packet.Inputs.Length != grids.Length) return false;
        for (var i = 0; i < fleet.fighters.Length; i++) if (packet.Products[i] != fleet.fighters[i].itemId || packet.Counts[i] != fleet.fighters[i].count) return false;
        for (var i = 0; i < grids.Length; i++) if (packet.Inputs[i] != grids[i].itemId || packet.Served[i] != grids[i].count ||
            packet.IncServed[i] != grids[i].inc || packet.Filters[i] != grids[i].filter) return false;
        return true;
    }
    internal static bool ApplyFighterPortSnapshot(BattleBaseComponent battleBase, FactoryProductPacket packet)
    {
        ref var fleet = ref battleBase.combatModule.moduleFleets[0]; var storage = battleBase.storage;
        if (packet.Recipe != fleet.protoId || packet.Products.Length != fleet.fighters.Length || packet.Counts.Length != fleet.fighters.Length ||
            packet.Inputs.Length != storage.size || packet.Served.Length != storage.size || packet.IncServed.Length != storage.size || packet.Filters.Length != storage.size) return false;
        for (var i = 0; i < packet.Products.Length; i++) if (packet.Products[i] < 0 || packet.Counts[i] < 0 || packet.Counts[i] > 1 || packet.Counts[i] > 0 && packet.Products[i] == 0) return false;
        for (var i = 0; i < storage.size; i++) if (packet.Inputs[i] < 0 || packet.Inputs[i] >= StorageComponent.itemStackCount.Length ||
            packet.Filters[i] < 0 || packet.Filters[i] >= StorageComponent.itemStackCount.Length || packet.Served[i] < 0 || packet.IncServed[i] < 0 ||
            (long)packet.IncServed[i] > (long)packet.Served[i] * 10 || packet.Served[i] > 0 && packet.Inputs[i] == 0) return false;
        for (var i = 0; i < fleet.fighters.Length; i++) { fleet.fighters[i].itemId = packet.Products[i]; fleet.fighters[i].count = packet.Counts[i]; }
        for (var i = 0; i < storage.size; i++)
        {
            ref var grid = ref storage.grids[i]; grid.itemId = packet.Inputs[i]; grid.count = packet.Served[i]; grid.inc = packet.IncServed[i];
            grid.filter = packet.Filters[i];
            grid.stackSize = StorageComponent.itemStackCount[grid.itemId > 0 ? grid.itemId : grid.filter];
        }
        storage.ResetOptimizationFlags(); storage.changed = true;
        return true;
    }
}
