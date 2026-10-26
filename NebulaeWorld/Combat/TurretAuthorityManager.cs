using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority.Adapters;
using NebulaeWorld.Authority;

namespace NebulaeWorld.Combat;

/// <summary>Turret commands share the session's deduplicated combat command stream.</summary>
internal sealed class TurretAuthorityManager : IDisposable
{
    public TurretSnapshotAdapter HostView { get; set; }
    private readonly object gate = new();
    private readonly ConcurrentDictionary<long, TurretIntent> pending = new();
    private readonly Queue<(AuthorityEpoch Epoch, ConnectionEpoch Connection, long Sequence, CommandResultCode Code, long Transfer)> results = new();
    private readonly InventoryReceiptGate receiptGate = new();
    private AuthorityEpoch clientEpoch;
    private ConnectionEpoch clientConnection;
    private SkillSystem importedSkills;
    private readonly Queue<(ObjectKey Key, TurretIntent Intent)> requests = new();
    private long checkpointTicket;
    public bool InventoryPending => pending.Values.Any(x => x.IsInventory);
    internal long DiagnosticSequence => pending.IsEmpty ? 0 : pending.Keys.DefaultIfEmpty().Min();
    internal bool DiagnosticWaiting => !pending.IsEmpty || requests.Count > 0;
    internal string DiagnosticState => $"commands={pending.Count} first={DiagnosticSequence} requests={requests.Count} results={results.Count} checkpoint={checkpointTicket}";
    public void BeginHostWorld(AuthorityEpoch epoch)
    {
        Multiplayer.Session.InventoryTransfers.EnsureWorld();
        HostView = new TurretSnapshotAdapter(epoch);
    }

    public CommandOutcome Execute(in QueuedHostCommand command, global::Player player)
    {
        if (!TurretIntent.TryDecode(command.Packet.Payload, out var intent) || !command.Packet.TryGetTargetKey(out var key) || HostView?.IsCurrent(key) != true)
            return new CommandOutcome(CommandResultCode.RejectedTarget);
        var session = Multiplayer.Session;
        var data = session.Server.Players.Get(command.ConnectionPlayerId)?.Data;
        if (data?.LocalPlanetId != key.Scope || player?.isAlive != true || player.package == null)
            return new CommandOutcome(CommandResultCode.RejectedUnauthorized);
        var factory = GameMain.galaxy.PlanetById(key.Scope)?.factory;
        if (!TurretSnapshotAdapter.TryTurret(factory, key.NativeId, out var id)) return new CommandOutcome(CommandResultCode.RejectedTarget);
        ref var turret = ref factory.defenseSystem.turrets.buffer[id];
        if (intent.IsInventory && !session.InventoryTransfers.CanRecord(command.ConnectionPlayerId, command.Key.Sequence)) return new CommandOutcome(CommandResultCode.RejectedNotReady);
        switch (intent.Kind)
        {
            case TurretIntentKind.Group:
                if (intent.Argument < 0 || intent.Argument > 255) return Invalid();
                turret.SetGroup(intent.Argument); break;
            case TurretIntentKind.Priority:
                if (intent.Argument < 0 || intent.Argument > 255) return Invalid();
                turret.vsSettings = (VSLayerMask)intent.Argument;
                turret.Search(factory, PlanetFactory.PrefabDescByModelIndex[factory.entityPool[turret.entityId].modelIndex], GameMain.gameTick);
                break;
            case TurretIntentKind.Phase:
                if (turret.type != ETurretType.Disturb || intent.Argument < 0 || intent.Argument > 3600) return Invalid();
                turret.phasePos = intent.Argument; break;
            case TurretIntentKind.Supernova:
                var mode = intent.Argument & 3; var enable = (intent.Argument & 4) != 0;
                if (mode < 1 || mode > 3 || intent.Argument > 7 || turret.type == ETurretType.Disturb) return Invalid();
                var pool = factory.defenseSystem.turrets;
                for (var i = 1; i < pool.cursor; i++)
                {
                    ref var other = ref pool.buffer[i];
                    if (other.id != i || mode == 1 && i != id || mode == 2 && (turret.group == 0 || other.group != turret.group)) continue;
                    if (enable) other.SetSupernova(); else other.CancelSupernova();
                    HostView.MarkChanged(factory, other.entityId);
                }
                break;
            case TurretIntentKind.Deposit:
                var item = intent.Argument;
                var needs = ItemProto.turretNeeds[(uint)turret.ammoType];
                if (item <= 0 || needs == null || !needs.Contains(item) || intent.Count <= 0 ||
                    turret.itemId != 0 && turret.itemId != item || turret.itemCount + intent.Count > 100 || player.package.GetItemCount(item) < intent.Count)
                    return new CommandOutcome(CommandResultCode.RejectedResource);
                var count = intent.Count;
                player.package.TakeTailItems(ref item, ref count, out var inc);
                if (count != intent.Count || inc != intent.Inc)
                {
                    player.package.AddItemStacked(item, count, inc, out _);
                    return new CommandOutcome(CommandResultCode.RejectedStale);
                }
                if (turret.itemId == 0) turret.SetNewItem(item, (short)count, (short)inc);
                else { turret.itemCount += (short)count; turret.itemInc += (short)inc; }
                session.InventoryTransfers.Record(command.ConnectionPlayerId, command.Key.Sequence, item, -count, inc);
                return Applied(Pack(item, count, inc));
            case TurretIntentKind.Withdraw:
            case TurretIntentKind.Clear:
                var oldItem = turret.itemId; var oldCount = turret.itemCount; var oldInc = turret.itemInc;
                if (oldItem <= 0 || oldCount <= 0) return new CommandOutcome(CommandResultCode.RejectedResource);
                var added = player.package.AddItemStacked(oldItem, oldCount, oldInc, out var remainingInc);
                if (added <= 0) return new CommandOutcome(CommandResultCode.RejectedResource);
                turret.itemCount -= (short)added; turret.itemInc = (short)remainingInc;
                if (turret.itemCount == 0 && (intent.Kind == TurretIntentKind.Clear || turret.bulletCount == 0)) turret.ClearItem();
                session.InventoryTransfers.Record(command.ConnectionPlayerId, command.Key.Sequence, oldItem, added, oldInc - remainingInc);
                return Applied(Pack(oldItem, added, oldInc - remainingInc));
            default: return Invalid();
        }
        return Applied();
        CommandOutcome Applied(long transfer = 0)
        {
            HostView.Invalidate(key);
            return new CommandOutcome(CommandResultCode.Applied, GameMain.gameTick, transfer);
        }
        static CommandOutcome Invalid() => new(CommandResultCode.RejectedInvalid);
    }

    public bool Request(int planet, int turret, TurretIntent intent)
    {
        EnsureClientWorld();
        if (pending.Count + requests.Count >= 64 || Multiplayer.Session.Life?.IsExiting == true) return false;
        var session = Multiplayer.Session;
        var factory = GameMain.galaxy?.PlanetById(planet)?.factory;
        if (factory?.defenseSystem?.turrets == null || turret <= 0 || turret >= factory.defenseSystem.turrets.cursor) return false;
        var entity = factory.defenseSystem.turrets.buffer[turret].entityId;
        if (session.AuthorityRuntime.WorldReplica?.TryFindKey(PoolKind.Turret, planet, entity, out var key) != true) return false;
        if (intent.IsInventory) { requests.Enqueue((key, intent)); return true; }
        if (!session.CombatAuthority.SendCommand(key, TurretIntent.Category, intent.Encode(), out var sequence)) return false;
        pending[sequence] = intent; return true;
    }
    public bool Deposit(int planet, int turret, int item, int wanted)
    {
        EnsureClientWorld();
        return Request(planet, turret, new TurretIntent(TurretIntentKind.Deposit, item, Math.Min(100, wanted)));
    }

    public void DispatchPending()
    {
        EnsureClientWorld();
        var session = Multiplayer.Session;
        if (requests.Count == 0 || session.InventoryTransfers.Pending) return;
        if (!session.Life.EnsureInventoryCheckpoint(ref checkpointTicket)) return;
        checkpointTicket = 0;
        var request = requests.Dequeue();
        var intent = request.Intent;
        if (intent.Kind == TurretIntentKind.Deposit)
        {
            var item = intent.Argument; var count = Math.Min(intent.Count, GameMain.mainPlayer.package.GetItemCount(item));
            if (count <= 0) return;
            GameMain.mainPlayer.package.TakeTailItems(ref item, ref count, out var inc);
            intent = new TurretIntent(intent.Kind, item, count, inc);
        }
        if (session.CombatAuthority.SendCommand(request.Key, TurretIntent.Category, intent.Encode(), out var sequence))
            pending[sequence] = intent;
        else if (intent.Kind == TurretIntentKind.Deposit)
            GameMain.mainPlayer.package.AddItemStacked(intent.Argument, intent.Count, intent.Inc, out _);
    }
    private void EnsureClientWorld()
    {
        var session = Multiplayer.Session;
        if (clientEpoch == session.Authority.Epoch && clientConnection == session.Authority.Connection) return;
        pending.Clear(); requests.Clear(); checkpointTicket = 0; lock (gate) { results.Clear(); receiptGate.Clear(); }
        clientEpoch = session.Authority.Epoch; clientConnection = session.Authority.Connection; Multiplayer.Session.InventoryTransfers.EnsureWorld(); importedSkills = null;
    }
    public void ReceiveResult(AuthorityCommandResultPacket packet)
    {
        if (!pending.ContainsKey(packet.Sequence)) return;
        var epoch = new AuthorityEpoch(packet.EpochHigh, packet.EpochLow);
        var connection = new ConnectionEpoch(packet.ConnectionEpoch);
        if (epoch != Multiplayer.Session.Authority.Epoch || connection != Multiplayer.Session.Authority.Connection) return;
        lock (gate)
        {
            if (!receiptGate.TryAccept(new CommandKey(epoch, connection, packet.Sequence))) return;
            results.Enqueue((epoch, connection, packet.Sequence, (CommandResultCode)packet.ResultCode, packet.TransactionId));
        }
    }
    public void TickClient()
    {
        EnsureClientWorld();
        var skills = GameMain.spaceSector?.skillSystem;
        if (skills != null && !ReferenceEquals(skills, importedSkills))
        {
            // Imported host pool slots must not survive as native client combat skills.
            skills.turretGaussProjectiles?.ResetPool(); skills.turretCannonades?.Reset();
            skills.turretLaserContinuous?.ResetPool(); skills.turretPlasmas?.ResetPool();
            skills.turretLocalPlasmas?.ResetPool(); skills.turretMissiles?.Reset(); skills.turretDisturbingWave?.ResetPool();
            importedSkills = skills;
        }
        while (true)
        {
            (AuthorityEpoch Epoch, ConnectionEpoch Connection, long Sequence, CommandResultCode Code, long Transfer) result;
            lock (gate) { if (results.Count == 0) break; result = results.Dequeue(); }
            if (result.Epoch != clientEpoch || result.Connection != clientConnection || !pending.TryGetValue(result.Sequence, out var intent)) continue;
            if (intent.IsInventory)
            {
                var item = (int)((result.Transfer >> 32) & 65535); var count = (int)((result.Transfer >> 16) & 65535); var inc = (int)(result.Transfer & 65535);
                if (intent.Kind == TurretIntentKind.Deposit)
                {
                    if (result.Code != CommandResultCode.Applied) { item = intent.Argument; count = intent.Count; inc = intent.Inc; }
                    else count = 0; // The local package already reserved the accepted deposit.
                }
                if (count > 0)
                {
                    var added = GameMain.mainPlayer.package.AddItemStacked(item, count, inc, out var remainingInc);
                    if (added < count)
                    {
                        // Keep a bounded receipt until inventory room exists; do not discard or spawn shared drops.
                        var remaining = Pack(item, count - added, remainingInc);
                        if (intent.Kind == TurretIntentKind.Deposit) pending[result.Sequence] = new TurretIntent(intent.Kind, item, count - added, remainingInc);
                        lock (gate) results.Enqueue((result.Epoch, result.Connection, result.Sequence, result.Code, remaining));
                        break;
                    }
                }
                if (result.Code == CommandResultCode.Applied) Multiplayer.Session.InventoryTransfers.Applied(result.Sequence);
            }
            pending.TryRemove(result.Sequence, out _);
            lock (gate) receiptGate.Finish(new CommandKey(result.Epoch, result.Connection, result.Sequence));
        }
    }
    public static long Pack(int item, int count, int inc) => ((long)(ushort)item << 32) | ((long)(ushort)count << 16) | (ushort)inc;
    public void Dispose()
    {
        lock (gate) { results.Clear(); receiptGate.Clear(); }
        pending.Clear(); HostView = null;
    }
}

