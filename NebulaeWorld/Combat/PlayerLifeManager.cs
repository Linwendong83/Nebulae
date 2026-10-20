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
    private int lastStage = -1;
    private readonly Dictionary<ushort, long> received = new();
    private readonly HashSet<ushort> refusedSnapshotPlayers = new();

    public bool RecordSnapshotRefusal(ushort playerId) => refusedSnapshotPlayers.Add(playerId);

    public void Publish()
    {
        if (!Multiplayer.Session.IsGameLoaded || Multiplayer.Session.IsDedicated || Multiplayer.Session.JoinSync?.CanInteract == false) return;
        if (Multiplayer.Session.IsClient && Multiplayer.Session.InventoryTransfers.Pending) return;
        var bytes = MetadataTransactionManager.CapturePlayer();
        var data = (PlayerData)Multiplayer.Session.LocalPlayer.Data;
        Multiplayer.Session.CombatAuthority.RememberPersonalCheckpoint(data.Life.Revision);
        lastCheckpoint = DateTime.UtcNow.Ticks;
        pending = new PlayerLifePacket
        {
            PlayerId = data.PlayerId,
            Life = data.Life,
            PlayerSnapshot = bytes,
            CombatRevision = Multiplayer.Session.CombatAuthority.LastAppliedRevision,
            CoreEnergyDebitAcknowledged = Multiplayer.Session.CombatAuthority.CoreDebitAcknowledged,
            LastCombatCommand = Multiplayer.Session.CombatAuthority.LastAppliedCommand,
            InventoryTransferSequence = Multiplayer.Session.InventoryTransfers.ClientSequence,
            DebitItemsAcknowledged = Multiplayer.Session.CombatAuthority.DebitItemsAcknowledged,
            DebitTotalsAcknowledged = Multiplayer.Session.CombatAuthority.DebitTotalsAcknowledged
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
            StoreServer((PlayerData)Multiplayer.Session.LocalPlayer.Data, pending.PlayerSnapshot);
            pending.PlayerSnapshot = Array.Empty<byte>();
            Multiplayer.Session.Server.SendPacket(pending);
            pending = null;
        }
        else Multiplayer.Session.Network.SendPacket(pending);
    }

    public void Acknowledge(long revision)
    {
        if (pending?.Life.Revision <= revision) pending = null;
    }

    public void GameTick()
    {
        if (!Multiplayer.Session.IsGameLoaded || Multiplayer.Session.IsDedicated) return;
        var action = GameMain.mainPlayer.controller.actionDeath;
        if (!GameMain.mainPlayer.isAlive && action.respawning && action.respawnStage != lastStage) Publish();
        else if (pending != null && DateTime.UtcNow.Ticks - lastSend > TimeSpan.TicksPerSecond * 2) Send();
        else if (DateTime.UtcNow.Ticks - lastCheckpoint >= TimeSpan.TicksPerSecond * 5) Publish();
    }

    public static bool StoreServer(PlayerData player, byte[] snapshot, long commandAck = 0, double debitAck = 0,
        int[] itemAcks = null, int[] debitAcks = null, long combatRevision = 0, long inventoryTransferSequence = 0)
    {
        // Only the processor for an authenticated connection may call this. Personal inventory,
        // energy and life still have a local game adapter; the authority sidecar stores none of
        // them. Persist their latest checkpoint in the same .server save as the world.
        var checkpoint = TryReadSnapshot(snapshot);
        itemAcks ??= Array.Empty<int>(); debitAcks ??= Array.Empty<int>();
        if (Multiplayer.Session?.IsServer == true &&
            !Multiplayer.Session.CombatAuthority.ValidateCheckpoint(player.PlayerId, commandAck, debitAck, itemAcks, debitAcks)) return false;
        var requestedRespawn = checkpoint?.Life?.IsAlive == true;
        var personalFight = checkpoint?.Mecha?.FightData;
        if (player.CombatAuthoritative && !Multiplayer.Session.CombatAuthority.ValidatePersonalStocks(player.PlayerId, combatRevision, personalFight)) return false;
        if (checkpoint?.Mecha?.Inventory == null) return false;
        if (Multiplayer.Session?.IsServer == true && !Multiplayer.Session.InventoryTransfers.Replay(player.PlayerId, inventoryTransferSequence, checkpoint.Mecha)) return false;
        if (!player.TryApplyPersonalSnapshot(checkpoint)) return false;
        if (Multiplayer.Session?.IsServer == true && player.CombatAuthoritative)
            Multiplayer.Session.CombatAuthority.AcceptCheckpoint(player.PlayerId, player, checkpoint, commandAck, debitAck,
                itemAcks, debitAcks, requestedRespawn, personalFight, combatRevision);
        if (Multiplayer.Session?.IsServer == true) Multiplayer.Session.InventoryTransfers.Acknowledge(player.PlayerId, inventoryTransferSequence);
        return true;
    }

    /// <summary>Loads saved life state. Snapshots with an incompatible PlayerData layout are treated as absent
    /// so joining can continue.</summary>
    private static PlayerData TryReadSnapshot(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024) return null;
        try
        {
            var snapshot = new PlayerData();
            snapshot.Deserialize(new NetDataReader(bytes));
            return snapshot;
        }
        catch (Exception e)
        {
            Log.Warn($"Ignoring unreadable player snapshot: {e.Message}");
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
            received[playerId] = life.Revision;
            using var scope = new RemoteWreckageScope(model);
            var player = model.PlayerInstance;
            if (!life.IsAlive && player.isAlive)
            {
                player.mecha.Kill();
                player.mechaArmorModel.Kill();
            }
            else if (life.IsAlive && !player.isAlive)
            {
                player.mechaArmorModel.ClearWreckages();
                player.mecha.Respawn();
                player.mechaArmorModel.Respawn();
            }
            player.isAlive = life.IsAlive;
            player.deathCount = life.DeathCount;
            player.timeSinceKilled = life.TimeSinceKilled;
            player.invincibleTicks = life.InvincibleTicks;
            if (model.RespawnVisual.Observe(model.Life, life)) player.mechaArmorModel.PrepareRespawn();
            model.Life = life;
        }
    }

    public static void TickRemote(RemotePlayerModel model)
    {
        var life = model.Life;
        if (model.PlayerInstance.isAlive) return;
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
        pending = null; received.Clear();
        refusedSnapshotPlayers.Clear();
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
