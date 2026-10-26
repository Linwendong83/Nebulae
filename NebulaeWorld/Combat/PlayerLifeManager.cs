using System;
using System.Collections.Generic;
using NebulaeModel.DataStructures;
using NebulaeModel.Logger;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Combat.Mecha;
using NebulaeWorld.GameStates;

namespace NebulaeWorld.Combat;

internal sealed class PlayerLifeManager : IDisposable
{
    private PlayerLifePacket pending;
    private long lastSend;
    private long lastCheckpoint;
    private long retryAfter;
    private int consecutiveRetries;
    private int lastStage = -1;
    private readonly Dictionary<ushort, long> received = new();

    private long requestedTicket, pendingTicket, nextTicket, acceptedTicket, exitTicket;
    private long requiredCombatRevision = -1;
    private bool exiting;
    private Action afterExitCheckpoint;
    public bool BlocksInventory => requestedTicket != 0 || pending != null;
    public bool IsExiting => exiting;
    internal long AcceptedTicket => acceptedTicket;
    internal long DiagnosticSequence => pendingTicket > acceptedTicket ? pendingTicket : requestedTicket;
    internal string DiagnosticState => $"requested={requestedTicket} pending={pendingTicket} accepted={acceptedTicket} exitTicket={exitTicket} exiting={exiting} revision={pending?.Life.Revision} requiredCombatRevision={requiredCombatRevision} commandAck={pending?.LastCombatCommand} inventorySequence={pending?.InventoryTransferSequence}";

    public long RequestCheckpoint()
    {
        if (requestedTicket == 0) requestedTicket = ++nextTicket;
        return requestedTicket;
    }

    public bool EnsureInventoryCheckpoint(ref long ticket, bool allowExit = false)
    {
        if (Multiplayer.Session.IsServer) return true;
        if (exiting && !allowExit) return false;
        if (ticket == 0) ticket = RequestCheckpoint();
        return acceptedTicket >= ticket && requestedTicket == 0 && pending == null;
    }

    public void Publish()
    {
        RequestCheckpoint();
        ServiceCheckpoint();
    }

    // Called before result draining and after it, so a continuous request stream cannot
    // occupy every idle frame between receipts and starve the personal checkpoint.
    public void ServiceCheckpoint()
    {
        var session = Multiplayer.Session;
        if (!session.IsGameLoaded || session.IsDedicated || session.JoinSync?.CanInteract == false) return;
        var now = DateTime.UtcNow.Ticks;
        if (requestedTicket == 0 && pending == null && now - lastCheckpoint >= TimeSpan.TicksPerSecond * 5)
            RequestCheckpoint();
        if (pending != null)
        {
            if (now - lastSend >= TimeSpan.TicksPerSecond * 2) Send();
            return;
        }
        if (requestedTicket == 0 || now < retryAfter || session.IsClient && session.InventoryTransfers.Pending) return;
        if (requiredCombatRevision >= 0 && session.CombatAuthority.LastAppliedRevision <= requiredCombatRevision) return;
        requiredCombatRevision = -1;
        var bytes = MetadataTransactionManager.CapturePlayer();
        var data = (PlayerData)session.LocalPlayer.Data;
        session.CombatAuthority.RememberPersonalCheckpoint(data.Life.Revision);
        pendingTicket = requestedTicket; requestedTicket = 0;
        pending = new PlayerLifePacket
        {
            PlayerId = data.PlayerId,
            Life = data.Life,
            PlayerSnapshot = bytes,
            EpochHigh = session.Authority.Epoch.High,
            EpochLow = session.Authority.Epoch.Low,
            ConnectionEpoch = session.Authority.Connection.Value,
            CombatRevision = session.CombatAuthority.LastAppliedRevision,
            CoreEnergyDebitAcknowledged = session.CombatAuthority.CoreDebitAcknowledged,
            LastCombatCommand = session.CombatAuthority.LastAppliedCommand,
            InventoryTransferSequence = session.InventoryTransfers.ClientSequence,
            DebitItemsAcknowledged = session.CombatAuthority.DebitItemsAcknowledged,
            DebitTotalsAcknowledged = session.CombatAuthority.DebitTotalsAcknowledged
        };
        lastStage = data.Life.RespawnStage;
        Send();
    }

    private void Send()
    {
        if (pending == null) return;
        lastSend = DateTime.UtcNow.Ticks;
        if (Multiplayer.Session.IsServer)
        {
            pending.PlayerSnapshot = Array.Empty<byte>();
            Multiplayer.Session.Server.SendPacket(pending);
            acceptedTicket = pendingTicket; pending = null; lastCheckpoint = lastSend;
        }
        else Multiplayer.Session.Network.SendPacket(pending);
    }

    public void Acknowledge(PlayerLifePacket receipt)
    {
        if (pending == null || receipt.Life.Revision != pending.Life.Revision) return;
        var status = (CheckpointReceiptStatus)receipt.ReceiptStatus;
        if (status is CheckpointReceiptStatus.Accepted or CheckpointReceiptStatus.Duplicate)
        {
            if (receipt.AcceptedRevision < pending.Life.Revision) return;
            acceptedTicket = Math.Max(acceptedTicket, pendingTicket);
            pending = null; lastCheckpoint = DateTime.UtcNow.Ticks;
            retryAfter = 0; consecutiveRetries = 0;
        }
        else if (status is CheckpointReceiptStatus.Retry or CheckpointReceiptStatus.Rejected)
        {
            requestedTicket = requestedTicket == 0 ? pendingTicket : Math.Max(requestedTicket, pendingTicket);
            var reason = (CheckpointRejectReason)receipt.RejectReason;
            requiredCombatRevision = (reason is CheckpointRejectReason.CombatBaseline or CheckpointRejectReason.InvalidLedger or CheckpointRejectReason.CombatLedger) &&
                Multiplayer.Session.CombatAuthority.LastAppliedRevision > 0
                ? Multiplayer.Session.CombatAuthority.LastAppliedRevision : -1;
            // The host republishes combat facts after a refused checkpoint. Wait for that
            // reconciliation and back off rather than recapturing the same bad ledger every frame.
            retryAfter = DateTime.UtcNow.Ticks + TimeSpan.TicksPerSecond * (1 << Math.Min(consecutiveRetries++, 3));
            var refusedState = DiagnosticState;
            pending = null;
            if (status == CheckpointReceiptStatus.Rejected)
                Diagnostics.Warn("checkpoint:refused:" + receipt.RejectReason, () => $"[checkpoint] refused reason={reason} receiptRevision={receipt.Life.Revision} acceptedRevision={receipt.AcceptedRevision} {refusedState}");
            else Log.Debug($"[checkpoint] retry reason={reason} revision={receipt.Life.Revision} {refusedState}");
        }
    }

    public void BeginExit(Action completed)
    {
        exiting = true; afterExitCheckpoint = completed;
        Multiplayer.Session.Research.TakeBackAfterPending();
    }

    public void GameTick()
    {
        if (!Multiplayer.Session.IsGameLoaded || Multiplayer.Session.IsDedicated) return;
        var action = GameMain.mainPlayer.controller.actionDeath;
        if (!GameMain.mainPlayer.isAlive && action.respawning && action.respawnStage != lastStage) RequestCheckpoint();
        ServiceCheckpoint();
        if (exiting && !Multiplayer.Session.Research.HasUnsettled && !Multiplayer.Session.InventoryTransfers.Pending)
        {
            if (exitTicket == 0) exitTicket = RequestCheckpoint();
            if (afterExitCheckpoint != null && acceptedTicket >= exitTicket && pending == null && requestedTicket == 0)
            {
                var completed = afterExitCheckpoint; afterExitCheckpoint = null;
                NebulaeModel.Utils.UnityDispatchQueue.RunOnMainThread(completed);
            }
        }
    }

    internal static CheckpointReceiptStatus StoreServerResult(PlayerData player, byte[] snapshot,
        out CheckpointRejectReason reason, long commandAck = 0, double debitAck = 0,
        int[] itemAcks = null, int[] debitAcks = null, long combatRevision = 0, long inventoryTransferSequence = 0)
    {
        reason = CheckpointRejectReason.None;
        var checkpoint = TryReadSnapshot(snapshot);
        if (checkpoint?.Mecha?.Inventory == null || checkpoint.PlayerId != player.PlayerId)
        { reason = CheckpointRejectReason.InvalidSnapshot; return CheckpointReceiptStatus.Rejected; }
        if (checkpoint.Life.Revision <= player.Life.Revision) return CheckpointReceiptStatus.Duplicate;
        itemAcks ??= Array.Empty<int>(); debitAcks ??= Array.Empty<int>();
        if (Multiplayer.Session?.IsServer == true &&
            !Multiplayer.Session.CombatAuthority.ValidateCheckpoint(player.PlayerId, commandAck, debitAck, itemAcks, debitAcks))
        { reason = CheckpointRejectReason.CombatLedger; return CheckpointReceiptStatus.Retry; }
        var requestedRespawn = checkpoint.Life.IsAlive;
        var personalFight = checkpoint.Mecha.FightData;
        if (player.CombatAuthoritative && !Multiplayer.Session.CombatAuthority.ValidatePersonalStocks(player.PlayerId, combatRevision, personalFight))
        { reason = CheckpointRejectReason.CombatBaseline; return CheckpointReceiptStatus.Retry; }
        if (Multiplayer.Session?.IsServer == true && !Multiplayer.Session.InventoryTransfers.Replay(player.PlayerId, inventoryTransferSequence, checkpoint.Mecha))
        { reason = CheckpointRejectReason.InventoryLedger; return CheckpointReceiptStatus.Retry; }
        if (!player.TryApplyPersonalSnapshot(checkpoint))
        { reason = CheckpointRejectReason.InvalidSnapshot; return CheckpointReceiptStatus.Rejected; }
        if (Multiplayer.Session?.IsServer == true && player.CombatAuthoritative)
            Multiplayer.Session.CombatAuthority.AcceptCheckpoint(player.PlayerId, player, checkpoint, commandAck, debitAck,
                itemAcks, debitAcks, requestedRespawn, personalFight, combatRevision);
        if (Multiplayer.Session?.IsServer == true) Multiplayer.Session.InventoryTransfers.Acknowledge(player.PlayerId, inventoryTransferSequence);
        return CheckpointReceiptStatus.Accepted;
    }

    /// <summary>Loads saved life state. Snapshots with an incompatible PlayerData layout are treated as absent
    /// so joining can continue.</summary>
    private static PlayerData TryReadSnapshot(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024) return null;
        try
        {
            var snapshot = new PlayerData();
            var reader = new NetDataReader(bytes);
            snapshot.Deserialize(reader);
            return reader.AvailableBytes == 0 ? snapshot : null;
        }
        catch (Exception e)
        {
            Diagnostics.Warn("checkpoint:decode", () => $"[checkpoint] unreadable player snapshot bytes={bytes?.Length ?? 0}", e);
            return null;
        }
    }

    public void RestoreLocal(PlayerData data)
    {
        // A cached local death is not evidence that the player is still dead. The server's
        // selected save already supplied the matching inventory and latest life state.
        var life = data.Life;
        PlayerLifeData.NextRevision(life.Revision);
        PlayerLifeData.CurrentTransactionId = life.TransactionId;
        PlayerLifeData.CurrentRedeployItemsDropped = life.RedeployItemsDropped;
        var player = GameMain.mainPlayer;
        player.isAlive = life.IsAlive;
        player.deathCount = life.DeathCount;
        player.timeSinceKilled = life.TimeSinceKilled;
        player.invincibleTicks = life.InvincibleTicks;
        var action = player.controller.actionDeath;
        action.ResetRespawnState();
        action.selectedRespawnOption = -1;
        lastStage = -1;
        if (life.IsAlive) return;
        player.mecha.hp = 0;
        player.mechaArmorModel.Kill();
        // The wreckage this restore explodes into existence is positioned in whatever relative
        // frame the load left active, which belongs to the seat that wrote the save. While the seat
        // is dead, vanilla makes the wreckage the seat's position every tick, so an unanchored
        // wreckage drags the rejoining player back to that stale origin on the very first tick -
        // into the middle of a star when the seat that wrote the save was sitting at one.
        AnchorWreckage(data, player);
        player.mechaArmorModel.wreckagesCenterUPos = player.uPosition;
        // Restart positioning/loading, but the durable redeploy flag prevents a second inventory drop.
        action.respawnMode = life.RespawnMode;
    }

    /// <summary>
    ///     Puts the wreckage of a restored dead seat exactly where that seat died. The pieces are
    ///     created in the relative frame that was active when the save was loaded, so they are moved
    ///     onto the restored seat here; <see cref="MechaArmorModel.GameTickWreckages"/> then averages
    ///     them around the seat instead of around the save writer's position, and
    ///     <see cref="PlayerAction_Death.GameTick"/> keeps adopting that average as the position.
    /// </summary>
    internal static void AnchorWreckage(PlayerData data, global::Player player)
    {
        var wreckages = MechaArmorModel.all_wreckages;
        if (wreckages == null) return;
        // The seat's last accepted velocity is what the wreckage should drift with. The sample the
        // load left in the player belongs to the save writer, and a wreckage drifting at that
        // velocity would carry the dead seat along with it.
        var accepted = data.AcceptedVelocityU;
        var velocity = Finite(accepted.x) && Finite(accepted.y) && Finite(accepted.z)
            ? new VectorLF3(accepted.x, accepted.y, accepted.z)
            : VectorLF3.zero;
        foreach (var wreckage in wreckages)
        {
            // Only plain fields are written below, so a reference check is enough here - and unlike
            // a Unity null check it also covers a wreckage whose native side is already gone. The
            // transforms are re-synced from these positions by SyncWreckagesTrans() on the next
            // tick, so there is nothing to render yet.
            if (wreckage is null) continue;
            wreckage.uPos = player.uPosition;
            wreckage.uVel = velocity;
        }
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>
    ///     True when the wreckage list holds nothing usable. Vanilla averages whatever the list has
    ///     and leaves <see cref="MechaArmorModel.wreckagesCenterUPos"/> at the origin when there is
    ///     nothing to average, which is where the home star sits.
    /// </summary>
    internal static bool WreckageIsMissing()
    {
        var wreckages = MechaArmorModel.all_wreckages;
        if (wreckages == null) return true;
        foreach (var wreckage in wreckages)
        {
            if (wreckage != null) return false;
        }
        return true;
    }

    public void ApplyRemote(ushort playerId, PlayerLifeData life)
    {
        if (life == null || playerId == Multiplayer.Session.LocalPlayer.Id) return;
        if (received.TryGetValue(playerId, out var revision) && revision >= life.Revision) return;
        using (Multiplayer.Session.World.GetRemotePlayersModels(out var models))
        {
            if (!models.TryGetValue(playerId, out var model)) return;
            using var scope = new RemoteWreckageScope(model);
            var player = model.PlayerInstance;
            // The combat channel (or the host actor) may already have changed player.isAlive.
            // Model transitions follow their own last presented state, which cannot alias Life.
            if (!life.IsAlive && (model.RespawnVisual.IsAlive || model.RespawnVisual.DeathCount != life.DeathCount))
            {
                player.mechaArmorModel.ClearWreckages();
                player.mecha.Kill();
                player.mechaArmorModel.Kill();
            }
            else if (life.IsAlive && (!model.RespawnVisual.IsAlive || !player.isAlive))
            {
                player.mechaArmorModel.ClearWreckages();
                // Preserve combat facts if that channel has already applied this respawn.
                if (!player.isAlive) player.mecha.Respawn();
                player.mechaArmorModel.Respawn();
            }
            player.isAlive = life.IsAlive;
            player.deathCount = life.DeathCount;
            player.timeSinceKilled = life.TimeSinceKilled;
            player.invincibleTicks = life.InvincibleTicks;
            if (model.RespawnVisual.Observe(life)) player.mechaArmorModel.PrepareRespawn();
            model.Life = life;
            received[playerId] = life.Revision;
        }
    }

    public static void TickRemote(RemotePlayerModel model)
    {
        var life = model.Life;
        if (model.RespawnVisual.IsAlive) return;
        using var scope = new RemoteWreckageScope(model);
        var armor = model.PlayerInstance.mechaArmorModel;
        if (life.RespawnMode == 2) armor.WreckagesRespawnLogic(model.RespawnVisual.Advance());
        else
        {
            armor.GameTickWreckages();
            armor.SyncWreckagesTrans();
        }
    }

    public void Remove(ushort playerId) => received.Remove(playerId);
    public void Dispose()
    {
        afterExitCheckpoint = null;
        pending = null; received.Clear();
        PlayerLifeData.CurrentTransactionId = "";
        PlayerLifeData.CurrentRedeployItemsDropped = false;
    }
}

internal sealed class RemoteWreckageScope : IDisposable
{
    private readonly List<ArmorWreckage> previous;
    private readonly RemotePlayerModel model;
    public RemoteWreckageScope(RemotePlayerModel remote)
    {
        model = remote;
        previous = MechaArmorModel.all_wreckages;
        MechaArmorModel.all_wreckages = remote.Wreckages;
    }
    public void Dispose()
    {
        model.Wreckages = MechaArmorModel.all_wreckages ?? new List<ArmorWreckage>();
        MechaArmorModel.all_wreckages = previous;
    }
}
