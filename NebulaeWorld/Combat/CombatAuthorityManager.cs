using System;
using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Logger;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Combat;
using NebulaeModel.Packets.Combat.Mecha;
using NebulaeWorld.Authority;
using UnityEngine;
using NebulaeModel.Utils;
using System.Linq;

namespace NebulaeWorld.Combat;

/// <summary>Host-owned native combat actors. Personal movement and inventory remain checkpoints.</summary>
internal sealed class CombatAuthorityManager : IHostCommandExecutor, IHostTickAware, IDisposable
{
    [ThreadStatic] private static NetDataWriter fightWriter;
    [ThreadStatic] public static bool ReclaimingFleets;
    private sealed class Actor
    {
        public global::Player Player;
        public Mecha Mecha;
        public NebulaeModel.DataStructures.IMechaFightData DetachedFight;
        public PlayerData Data;
        public PlayerAction_Combat Action;
        public RemoteCombatPose.Frame Pose;
        public long LastCommand;
        public long Revision;
        public ConnectionEpoch Connection;
        public bool FighterPortsDirty;
        public double CoreDebit;
        public long LastBombTick = -10000;
        public long LastBurstTick;
        public readonly CombatConsumptionLedger Debits = new();
        public readonly Dictionary<int, int> InventoryShadow = new();
        public readonly Dictionary<int, int> InventoryCounts = new();
        public readonly Queue<(ObjectKey Target, EAmmoType Ammo, long Tick, PlayerShotId Shot, bool Manual, int Item, int Slot)> AmmoIntents = new();
        public readonly Queue<(ObjectKey Target, EAmmoType Ammo, long Tick, PlayerShotId Shot, bool Manual, int Item, int Slot)> LaserIntents = new();
        public int CombatPlanet;
        public double FrameEnergy;
        public double FrameDebit;
        /// <summary>Last player-combat packet actually published, for change gating.</summary>
        public AuthorityPlayerCombatStatePacket LastPublishedCombat;
        public readonly Dictionary<long, StorageComponent[]> StockHistory = new();
        public readonly Dictionary<long, long> StockPersonalRevisions = new();
    }
    private readonly Dictionary<ushort, Actor> actors = new();
    private sealed class Checkpoint
    {
        public PlayerData Data;
        public byte[] Snapshot;
        public ConnectionEpoch Connection;
        public long Command;
        public long CombatRevision;
        public long InventoryTransferSequence;
        public double CoreDebit;
        public int[] Items;
        public int[] Totals;
        public Action<CheckpointReceiptStatus, CheckpointRejectReason> Finish;
    }
    private readonly object checkpointGate = new();
    private readonly Dictionary<ushort, Checkpoint> checkpoints = new();
    private long checkpointBytes;
    private readonly Dictionary<ushort, long> received = new();
    private readonly Dictionary<ushort, ulong> receivedConnections = new();
    private long sequence;
    private AuthorityEpoch sendingEpoch;
    private ConnectionEpoch sendingConnection;
    private AuthorityEpoch receivingEpoch;
    private long lastSettingsTick = -1000;
    /// <summary>Last ground-fleet enable mask the host was told, or -1 before the first report.</summary>
    private int lastGroundFleetMask = -1;
    private long lastGroundFleetMaskTick = long.MinValue;

    /// <summary>The "nothing sent yet" sentinel: a combination no real settings report can produce.</summary>
    internal static readonly (int Flags, double Energy, double DebitAck, double ShieldSpeed) NoSettingsSent =
        (int.MinValue, double.NaN, double.NaN, double.NaN);

    /// <summary>
    /// The last settings report this client actually sent.
    /// </summary>
    /// <remarks>
    /// The report is a reconciliation channel for configuration and energy, so it is only worth a
    /// packet when one of those moved. A player sitting at full energy with modules enabled is the
    /// common idle case in a session: every value here is then unchanged, and re-sending the same
    /// 29 bytes every fifteen ticks would cost a packet per player forever.
    /// </remarks>
    private (int Flags, double Energy, double DebitAck, double ShieldSpeed) lastSettings = NoSettingsSent;
    private long lastCompletedTick = -1;
    private AuthorityEpoch hostEpoch;
    private readonly Dictionary<int, int> acknowledgedDebits = new();
    private StorageComponent[] observedStocks;
    private readonly Dictionary<long, StorageComponent[]> publishedStockHistory = new();
    private long publishedPersonalRevision;
    private long observedPersonalRevision;
    private bool personalStocksDirty;

    public void InitializePersonalBaseline(PlayerData data)
    {
        receivingEpoch = Multiplayer.Session.Authority.Epoch;
        received.Clear(); receivedConnections.Clear(); acknowledgedDebits.Clear();
        CoreDebitAcknowledged = 0; LastAppliedRevision = LastAppliedCommand = 0;
        lastGroundFleetMask = -1;
        publishedStockHistory.Clear(); publishedPersonalRevision = 0;
        observedPersonalRevision = data.Life.Revision;
        observedStocks = CopyStocks(GameMain.mainPlayer.mecha);
        personalStocksDirty = false;
        lastSettings = NoSettingsSent;
    }
    public void MarkPersonalStockChange(StorageComponent storage)
    {
        if (PlayerWeaponResources.Active || Multiplayer.Session.IsServer || Multiplayer.Session.AuthorityRuntime.ApplyContext.IsActiveOnCurrentThread) return;
        var mecha = GameMain.mainPlayer?.mecha;
        if (storage == mecha?.ammoStorage || storage == mecha?.bombStorage || storage == mecha?.fighterStorage)
            personalStocksDirty = true;
    }
    public void RememberPersonalCheckpoint(long revision)
    {
        var mecha = GameMain.mainPlayer.mecha;
        publishedPersonalRevision = revision;
        publishedStockHistory[revision] = CopyStocks(mecha);
        foreach (var old in publishedStockHistory.Keys.OrderBy(x => x).Take(Math.Max(0, publishedStockHistory.Count - 128)).ToArray()) publishedStockHistory.Remove(old);
        personalStocksDirty = false;
    }
    public int[] DebitItemsAcknowledged => acknowledgedDebits.Keys.ToArray();
    public int[] DebitTotalsAcknowledged => acknowledgedDebits.Values.ToArray();

    /// <summary>
    /// Host ticks a player stays "in a fight" after their last combat action or taken hit.
    /// </summary>
    /// <remarks>
    /// The window is what turns engagement into a delivery tier: while it is open the player's
    /// scopes are sampled at the hot cadence, and once it lapses they drop to the cold one. It is
    /// deliberately longer than a fight's pauses, so a player who stops shooting for a couple of
    /// seconds does not watch enemies freeze mid-approach.
    /// </remarks>
    public const int EngagementWindowTicks = 300;

    /// <summary>Records that this player just did something combative, or was hit.</summary>
    public void NoteEngagement(ushort playerId)
    {
        if (!engagementWatches.TryGetValue(playerId, out var watch)) engagementWatches[playerId] = watch = new EngagementWatch();
        watch.LastEngagementTick = HostTick;
    }

    /// <summary>
    /// Whether the host's combat simulation currently considers this player to be in a fight.
    /// Drives <see cref="NebulaeModel.Authority.HostWorldReplicator.EngagementProvider"/>.
    /// </summary>
    public bool IsEngaged(ushort playerId) =>
        engagementWatches.TryGetValue(playerId, out var watch) && HostTick - watch.LastEngagementTick <= EngagementWindowTicks;

    /// <summary>
    /// Watches one mecha's consumables and reports the frames on which they moved in a way only
    /// combat produces: ammunition spent, laser burnt, shield drained, hull lost.
    /// </summary>
    /// <remarks>
    /// Watching values rather than hooks means it works uniformly for the host's own mecha, which
    /// is simulated natively and has no authority actor, and for every remote actor. Firing and
    /// being hit are the two directions of a fight; either one keeps the player's scopes hot.
    /// </remarks>
    private sealed class EngagementWatch
    {
        public long LastEngagementTick = long.MinValue;
        private int hp = -1;
        private long shield = -1;
        private int bullets = -1;
        private long laser = -1;

        public bool Observe(Mecha mecha)
        {
            if (mecha == null) return false;
            var engaged = hp >= 0 && mecha.hp < hp;
            engaged |= shield >= 0 && mecha.energyShieldEnergy < shield;
            engaged |= bullets >= 0 && mecha.ammoBulletCount < bullets;
            engaged |= laser >= 0 && mecha.laserEnergy < laser;
            hp = mecha.hp;
            shield = mecha.energyShieldEnergy;
            bullets = mecha.ammoBulletCount;
            laser = mecha.laserEnergy;
            return engaged;
        }
    }

    private readonly Dictionary<ushort, EngagementWatch> engagementWatches = [];

    /// <summary>Observes one mecha and records engagement when a combat-only value moved.</summary>
    private void ObserveEngagement(ushort playerId, Mecha mecha)
    {
        if (mecha == null) return;
        if (!engagementWatches.TryGetValue(playerId, out var watch)) engagementWatches[playerId] = watch = new EngagementWatch();
        if (watch.Observe(mecha)) watch.LastEngagementTick = HostTick;
    }
    public IHostWorldView TargetWorld { get; set; }
    public long HostTick { get; set; }
    public long LastAppliedRevision { get; private set; }
    public double CoreDebitAcknowledged { get; private set; }
    public long LastAppliedCommand { get; private set; }
    public bool Owns(global::Player player)
    {
        foreach (var actor in actors.Values) if (actor.Player == player) return true;
        return false;
    }
    public Mecha MechaFor(ushort playerId) => actors.TryGetValue(playerId, out var actor) && actor.DetachedFight == null ? actor.Player.mecha : null;

    // Share the combat energy watermark so a repair debit is applied once, including across checkpoints.
    public void RecordRepairEnergy(ushort playerId, double amount)
    {
        if (amount > 0 && actors.TryGetValue(playerId, out var actor)) actor.CoreDebit += amount;
    }

    public ushort PlayerIdFor(Mecha mecha)
    {
        foreach (var pair in actors)
            if (pair.Value.DetachedFight == null && pair.Value.Mecha == mecha) return pair.Key;
        return 0;
    }

    public bool TryGetPose(Mecha mecha, out RemoteCombatPose.Frame pose)
    {
        foreach (var actor in actors.Values)
            if (actor.DetachedFight == null && actor.Mecha == mecha) { pose = actor.Pose; return true; }
        pose = default;
        return false;
    }

    public void PreserveDisconnectedActor(ushort playerId)
    {
        if (!Multiplayer.Session.IsServer || !actors.TryGetValue(playerId, out var actor) || actor.DetachedFight != null) return;
        // Native Player.Free releases every storage/module by reference. Server checkpoints
        // must own independent copies before the Unity presentation model is destroyed.
        actor.Data.Mecha.CoreEnergy = actor.Mecha.coreEnergy;
        actor.Data.Mecha.FightData = new MechaFightData(actor.Player);
        var writer = new NetDataWriter(); actor.Data.Mecha.Serialize(writer);
        var retained = new MechaData(); retained.Deserialize(new NetDataReader(writer.CopyData()));
        actor.Data.Mecha = retained; actor.DetachedFight = retained.FightData;
    }

    public bool QueueCheckpoint(PlayerData data, byte[] snapshot, long command, double debit, int[] items, int[] totals,
        Action<CheckpointReceiptStatus, CheckpointRejectReason> finish, long combatRevision = 0, long inventoryTransferSequence = 0)
    {
        if (snapshot == null || snapshot.Length > 4 * 1024 * 1024 || items == null || totals == null || items.Length != totals.Length || items.Length > 512) return false;
        lock (checkpointGate)
        {
            checkpoints.TryGetValue(data.PlayerId, out var previous);
            var bytes = checkpointBytes - (previous?.Snapshot.Length ?? 0) + snapshot.Length;
            if (bytes > 8 * 1024 * 1024 || previous == null && checkpoints.Count >= 128) return false;
            checkpoints[data.PlayerId] = new Checkpoint
            {
                Data = data,
                Snapshot = (byte[])snapshot.Clone(),
                Connection = Multiplayer.Session.AuthorityRuntime.ConnectionEpochFor(data.PlayerId),
                Command = command,
                CoreDebit = debit,
                Items = (int[])items.Clone(),
                Totals = (int[])totals.Clone(),
                Finish = finish,
                CombatRevision = combatRevision,
                InventoryTransferSequence = inventoryTransferSequence
            };
            checkpointBytes = bytes;
            return true;
        }
    }

    private Actor RetainActor(ushort playerId, ConnectionEpoch connection)
    {
        if (!actors.TryGetValue(playerId, out var actor) || actor.Connection != connection)
            actors[playerId] = actor = new Actor { Connection = connection };
        return actor;
    }

    public bool ValidateCheckpoint(ushort id, long commandAck, double debitAck, int[] itemAcks, int[] debitAcks)
    {
        if (!actors.TryGetValue(id, out var actor)) return true;
        bool Refuse(string reason, int item = 0, int acknowledged = 0, int total = 0)
        {
            Diagnostics.Warn("combat:checkpoint:" + id + ":" + reason, () =>
                $"[combat] checkpoint ledger refused player={id} reason={reason} command={commandAck}/{actor.LastCommand} core={debitAck}/{actor.CoreDebit} item={item} debit={acknowledged}/{total}");
            return false;
        }
        if (commandAck < 0 || commandAck > actor.LastCommand) return Refuse("command-watermark");
        if (double.IsNaN(debitAck) || double.IsInfinity(debitAck) || debitAck < 0 || debitAck > actor.CoreDebit) return Refuse("core-watermark");
        if (itemAcks == null || debitAcks == null || itemAcks.Length != debitAcks.Length || itemAcks.Length > 512) return Refuse("item-layout");
        var seen = new HashSet<int>();
        for (var i = 0; i < itemAcks.Length; i++)
        {
            actor.Debits.Totals.TryGetValue(itemAcks[i], out var total);
            if (itemAcks[i] <= 0 || !seen.Add(itemAcks[i]) || debitAcks[i] < 0 || debitAcks[i] > total)
                return Refuse("item-watermark", itemAcks[i], debitAcks[i], total);
        }
        return true;
    }

    public void BeginFrame(long tick)
    {
        HostTick = tick;
        var session = Multiplayer.Session;
        if (!session.IsServer || !session.IsGameLoaded) return;
        if (!hostEpoch.Equals(session.Authority.Epoch))
        { Dispose(); hostEpoch = session.Authority.Epoch; lastCompletedTick = -1; }
        if (!GameMain.data.gameDesc.isCombatMode) { DrainCheckpoints(); return; }
        var online = new HashSet<ushort>();
        using (session.World.GetRemotePlayersModels(out var models))
            foreach (var pair in session.Server.Players.Connected)
            {
                var data = (PlayerData)pair.Value.Data;
                online.Add(data.PlayerId);
                if (!models.TryGetValue(data.PlayerId, out var model) || model.PlayerTransform == null || data.Mecha.ReactorStorage == null) continue;
                var actorConnection = session.AuthorityRuntime.ConnectionEpochFor(data.PlayerId);
                var retain = actors.TryGetValue(data.PlayerId, out var actor) && actor.Connection == actorConnection;
                if (retain && actor.DetachedFight == null && actor.Player == model.PlayerInstance) continue;
                if (retain)
                {
                    // Recreating a Unity body does not start a new consumption ledger. Restore
                    // its latest host resources along with that ledger, not an older checkpoint.
                    if (actor.DetachedFight == null)
                    {
                        actor.Data.Mecha.CoreEnergy = actor.Mecha.coreEnergy;
                        actor.Data.Mecha.FightData = new MechaFightData(actor.Player);
                        actor.Data.Life.IsAlive = actor.Player.isAlive;
                        actor.Data.Life.DeathCount = actor.Player.deathCount;
                        actor.Data.Life.InvincibleTicks = actor.Player.invincibleTicks;
                    }
                    data.Mecha = actor.Data.Mecha;
                    data.Life = actor.Data.Life;
                }
                // Remote Player has no local inventory UI initialization. Install the approved
                // checkpoint directly rather than invoking the local-player restore adapter.
                model.PlayerInstance.SetHiddenProperty(nameof(global::Player.package), data.Mecha.Inventory);
                (data.Mecha as MechaData)?.ApplyHand(model.PlayerInstance);
                model.PlayerInstance.SetHiddenProperty(nameof(global::Player.deliveryPackage), data.Mecha.DeliveryPackage);
                // Remote bodies bypass Player.SetForNewGame/Import, which normally creates
                // this helper. Native hangar/ammo replenishment still needs it on the host.
                model.PlayerInstance.SetHiddenProperty(nameof(global::Player.packageUtility), new PlayerPackageUtility(model.PlayerInstance));
                model.MechaInstance.coreEnergy = data.Mecha.CoreEnergy;
                model.MechaInstance.reactorEnergy = data.Mecha.ReactorEnergy;
                if (data.Mecha is MechaData reactor)
                {
                    model.MechaInstance.reactorItemId = reactor.ReactorItemId;
                    model.MechaInstance.reactorItemInc = reactor.ReactorItemInc;
                }
                model.MechaInstance.reactorStorage = data.Mecha.ReactorStorage;
                model.MechaInstance.warpStorage = data.Mecha.WarpStorage;
                data.Mecha.FightData.UpdateMech(model.PlayerInstance);
                // Personal checkpoints intentionally omit tech bonuses; derive combat limits from host research.
                new PlayerTechBonuses(GameMain.mainPlayer.mecha).UpdateMech(model.MechaInstance);
                RefreshCombatLimits(model.MechaInstance, GameMain.mainPlayer.mecha);
                if (data.Appearance != null)
                {
                    data.Appearance.CopyTo(model.MechaInstance.appearance);
                    model.PlayerInstance.mechaArmorModel.RefreshAllPartObjects();
                    model.PlayerInstance.mechaArmorModel.RefreshAllBoneObjects();
                    MechaCustomizationProperties.Refresh(model.PlayerInstance, preserveRatio: false);
                }
                model.PlayerInstance.isAlive = data.Life.IsAlive;
                model.PlayerInstance.deathCount = data.Life.DeathCount;
                model.PlayerInstance.invincibleTicks = data.Life.InvincibleTicks;
                var action = new PlayerAction_Combat(); action.Init(model.PlayerInstance);
                actor = RetainActor(data.PlayerId, actorConnection);
                actor.Player = model.PlayerInstance; actor.Mecha = model.MechaInstance; actor.Data = data;
                actor.Action = action; actor.CombatPlanet = data.LocalPlanetId;
                actor.DetachedFight = null; actor.LastPublishedCombat = null;
                RememberStocks(actors[data.PlayerId]);
                data.CombatAuthoritative = true;
                ResetInventoryShadow(actors[data.PlayerId]);
            }
        foreach (var id in new List<ushort>(actors.Keys))
            if (!online.Contains(id))
            {
                ReclaimFleets(actors[id]);
                actors[id].Data.CombatAuthoritative = false; actors.Remove(id);
                engagementWatches.Remove(id);
            }
        DrainCheckpoints();
        foreach (var actor in actors.Values)
        {
            if (actor.DetachedFight != null || actor.Player.transform == null) continue;
            actor.FrameEnergy = actor.Player.mecha.coreEnergy;
            actor.FrameDebit = actor.CoreDebit;
            if (!actor.Player.isAlive) continue;
            Prepare(actor);
            var factory = actor.Action.localFactory;
            if (factory != null)
                for (var id = 1; id < factory.enemySystem.bases.cursor; id++)
                {
                    var item = factory.enemySystem.bases.buffer[id];
                    if (item?.id == id && item.enemyId > 0 && item.enemyId < factory.enemyCursor &&
                        factory.enemyPool[item.enemyId].id == item.enemyId &&
                        (factory.enemyPool[item.enemyId].pos - (VectorLF3)actor.Player.position).sqrMagnitude < 8100)
                        item.UnderAttack(actor.Player.position, 50f);
                }
            var star = actor.Action.localStar;
            if (star != null)
                for (var hive = GameMain.spaceSector.dfHives[star.index]; hive != null; hive = hive.nextSibling)
                    if (!hive.realized && !hive.isEmpty &&
                        (GameMain.spaceSector.astros[hive.hiveAstroId - 1000000].uPos - actor.Player.uPosition).sqrMagnitude < 400000000)
                        hive.Realize();
            actor.Action.ActivateNearbyEnemyHive();
        }
    }

    private void DrainCheckpoints()
    {
        var session = Multiplayer.Session;
        List<Checkpoint> pending;
        lock (checkpointGate) { pending = checkpoints.Values.ToList(); checkpoints.Clear(); checkpointBytes = 0; }
        foreach (var checkpoint in pending)
        {
            var current = session.Server.Players.Get(checkpoint.Data.PlayerId);
            var valid = current != null && ReferenceEquals(current.Data, checkpoint.Data) &&
                session.AuthorityRuntime.ConnectionEpochFor(checkpoint.Data.PlayerId).Equals(checkpoint.Connection);
            try
            {
                if (!valid) continue;
                if (actors.TryGetValue(checkpoint.Data.PlayerId, out var unavailable) &&
                    (unavailable.DetachedFight != null || unavailable.Connection != checkpoint.Connection))
                {
                    checkpoint.Finish?.Invoke(CheckpointReceiptStatus.Retry, CheckpointRejectReason.Busy);
                    continue;
                }
                var status = PlayerLifeManager.StoreServerResult(checkpoint.Data, checkpoint.Snapshot, out var reason,
                    checkpoint.Command, checkpoint.CoreDebit, checkpoint.Items, checkpoint.Totals,
                    checkpoint.CombatRevision, checkpoint.InventoryTransferSequence);
                if (status == CheckpointReceiptStatus.Retry && actors.TryGetValue(checkpoint.Data.PlayerId, out var staleActor))
                    staleActor.LastPublishedCombat = null;
                checkpoint.Finish?.Invoke(status, reason);
            }
            catch (Exception error) { NebulaeModel.Logger.Log.Warn("[authority] personal checkpoint failed: " + error); }
        }
    }

    public void PumpPausedCheckpoints()
    {
        var session = Multiplayer.Session;
        if (!hostEpoch.Equals(session.Authority.Epoch))
        { Dispose(); hostEpoch = session.Authority.Epoch; lastCompletedTick = -1; }
        DrainCheckpoints();
        foreach (var pair in actors)
            if (pair.Value.DetachedFight == null && (pair.Value.LastPublishedCombat == null ||
                pair.Value.LastPublishedCombat.PersonalRevision != pair.Value.Data.Life.Revision))
                PublishActor(pair.Key, pair.Value, GameMain.gameTick, force: true);
    }

    private static void Prepare(Actor actor)
    {
        if (!actor.Player.isAlive || actor.CombatPlanet != actor.Data.LocalPlanetId)
        { CancelShots(actor); actor.CombatPlanet = actor.Data.LocalPlanetId; }
        var planet = GameMain.galaxy.PlanetById(actor.Data.LocalPlanetId);
        var star = GameMain.galaxy.StarById(actor.Data.LocalStarId);
        NativeGameAccess.SetSimulationPlanet(actor.Player, planet);
        actor.Player.position = new Vector3(actor.Data.LocalPlanetPosition.x, actor.Data.LocalPlanetPosition.y, actor.Data.LocalPlanetPosition.z);
        actor.Player.uPosition = planet == null ? new VectorLF3(actor.Data.UPosition.x, actor.Data.UPosition.y, actor.Data.UPosition.z)
            : RemoteCombatPose.UniversalPoint(planet.uPosition, planet.runtimeRotation, actor.Player.position);
        actor.Player.movementState = actor.Data.AcceptedMovementState;
        actor.Player.warpCommand = actor.Data.AcceptedWarping;
        actor.Player.uVelocity = new VectorLF3(actor.Data.AcceptedVelocityU.x, actor.Data.AcceptedVelocityU.y, actor.Data.AcceptedVelocityU.z);
        actor.Player.controller.velocity = new Vector3(actor.Data.AcceptedVelocityL.x, actor.Data.AcceptedVelocityL.y, actor.Data.AcceptedVelocityL.z);
        // The mecha's facing lives on the Model child; the root transform only carries the
        // gravity-aligned frame. uRotation must be the real facing — it aims bombs, lasers,
        // missiles and fleet launches — while the pose frame keeps describing the root frame
        // that RemoteCombatPose.Offset measures bones against (Offset reads the root transform,
        // and LocalBone/UniversalBone only come out right while the frame's rotations match it).
        var rootRotation = Quaternion.Euler(actor.Data.Rotation.x, actor.Data.Rotation.y, actor.Data.Rotation.z);
        var facing = Quaternion.Euler(actor.Data.BodyRotation.x, actor.Data.BodyRotation.y, actor.Data.BodyRotation.z);
        actor.Player.uRotation = planet == null ? facing : planet.runtimeRotation * facing;
        actor.Player.transform.localRotation = rootRotation;
        actor.Pose = new RemoteCombatPose.Frame(actor.Player.position, rootRotation, actor.Player.uPosition,
            planet == null ? rootRotation : planet.runtimeRotation * rootRotation);
        actor.Player.mecha.UpdateSkillColliders();
        actor.Action.localPlanet = planet;
        actor.Action.localStar = star;
        actor.Action.localFactory = FactoryForCombat(planet);
        actor.Action.localAstroId = planet?.astroId ?? 0;
        actor.Action.localPlayerPos = actor.Player.position;
        GameMain.history.GetCombatUpgradeData(ref actor.Action.combatUpgradeData);
    }

    /// <summary>Combat needs simulation data, including planets with no host-side renderer.</summary>
    public static PlanetFactory FactoryForCombat(PlanetData planet) => planet?.factory;

    private static void CountInventory(Actor actor)
    {
        actor.InventoryCounts.Clear();
        foreach (var grid in actor.Player.package.grids)
            if (grid.itemId > 0 && grid.count > 0)
            { actor.InventoryCounts.TryGetValue(grid.itemId, out var count); actor.InventoryCounts[grid.itemId] = count + grid.count; }
    }
    private static void ResetInventoryShadow(Actor actor)
    {
        CountInventory(actor); actor.InventoryShadow.Clear();
        foreach (var pair in actor.InventoryCounts) actor.InventoryShadow[pair.Key] = pair.Value;
    }
    private static void CaptureInventoryDebits(Actor actor)
    {
        CountInventory(actor);
        foreach (var pair in actor.InventoryShadow)
        {
            actor.InventoryCounts.TryGetValue(pair.Key, out var count);
            if (count < pair.Value) actor.Debits.Debit(pair.Key, pair.Value - count);
        }
        actor.InventoryShadow.Clear();
        foreach (var pair in actor.InventoryCounts) actor.InventoryShadow[pair.Key] = pair.Value;
    }

    public void SendSettings(long tick, bool immediate = false)
    {
        if (personalStocksDirty && Multiplayer.Session.IsGameLoaded) Multiplayer.Session.Life.Publish();
        if (!Multiplayer.Session.IsGameLoaded) return;
        var mecha = GameMain.mainPlayer.mecha;
        // Ground fleets need one bit each, and the settings word has no room left for that:
        // bits 17+ already mean shield, relay, building attack and weapon modes. Reporting the
        // mask separately keeps every ground fleet's enable state its own, instead of aliasing
        // fleet B/C/D onto those unrelated toggles.
        PublishGroundFleetMask(mecha.groundCombatModule, tick);
        var flags = mecha.ammoSelectSlot & 3;
        if (mecha.laserActive) flags |= 4;
        if (mecha.groundCombatModule.moduleEnabled) flags |= 8;
        if (mecha.spaceCombatModule.moduleEnabled) flags |= 16;
        if (mecha.autoReplenishAmmo) flags |= 32;
        if (mecha.autoReplenishHangar) flags |= 64;
        if (mecha.autoReplenishFuel) flags |= 128;
        for (var i = 0; i < mecha.spaceCombatModule.fleetCount; i++) if (mecha.spaceCombatModule.moduleFleets[i].fleetEnabled) flags |= 1 << (8 + i);
        flags = CombatShieldSettings.Encode(flags, mecha.energyShieldRechargeEnabled);
        flags = CombatFleetSettings.Encode(flags, mecha.groundCombatModule, mecha.spaceCombatModule);
        flags = CombatWeaponSettings.Encode(flags, mecha);
        // Send configuration changes before the next shot, while keeping energy-only reports throttled.
        if (tick - lastSettingsTick < 15 && (!immediate || flags == lastSettings.Flags && mecha.energyShieldRechargeSpeed == lastSettings.ShieldSpeed)) return;
        lastSettingsTick = tick;
        var report = (flags, mecha.coreEnergy, CoreDebitAcknowledged, mecha.energyShieldRechargeSpeed);
        if (IsUnchangedSettingsReport(lastSettings, report)) return;
        lastSettings = report;
        Send(new CombatIntent(CombatIntentKind.Settings, flags, mecha.coreEnergy, CoreDebitAcknowledged, mecha.energyShieldRechargeSpeed));
    }

    /// <summary>
    /// Reports the ground-fleet enable mask when it changes, on its own command.
    /// </summary>
    /// <remarks>
    /// A fleet's own enable button used to be transmitted only for fleet A, while the host
    /// decoded fleets B..H from bits that actually mean shield recharge, relay/building attack
    /// and weapon mode. A player toggling the fourth formation therefore either could not turn
    /// it on, or had the host force it off on the next settings report. The mask carries all
    /// eight slots independently and bypasses the settings throttle because a click is an
    /// explicit configuration change.
    /// </remarks>
    private void PublishGroundFleetMask(CombatModuleComponent module, long tick)
    {
        var session = Multiplayer.Session;
        if (!sendingEpoch.Equals(session.Authority.Epoch) || !sendingConnection.Equals(session.Authority.Connection))
        {
            lastGroundFleetMask = -1;
            lastGroundFleetMaskTick = long.MinValue;
        }
        var mask = GroundFleetMask(module);
        // Re-report periodically as well as on change: the first report can race the host's
        // actor creation and be rejected, and a lost mask would silently disable formations.
        if (mask == lastGroundFleetMask && long.MinValue != lastGroundFleetMaskTick &&
            tick - lastGroundFleetMaskTick < 300) return;
        if (Send(new CombatIntent(CombatIntentKind.GroundFleetMask, mask)))
        {
            lastGroundFleetMask = mask;
            lastGroundFleetMaskTick = tick;
        }
    }

    internal static int GroundFleetMask(CombatModuleComponent module)
    {
        var mask = 0;
        var count = Math.Min(module.fleetCount, Math.Min(module.moduleFleets.Length, 8));
        for (var i = 0; i < count; i++) if (module.moduleFleets[i].fleetEnabled) mask |= 1 << i;
        return mask;
    }

    public CommandOutcome Execute(in QueuedHostCommand command)
    {
        if (actors.TryGetValue(command.ConnectionPlayerId, out var commandActor))
        {
            if (commandActor.DetachedFight != null) return new CommandOutcome(CommandResultCode.RejectedNotReady);
            commandActor.LastCommand = Math.Max(commandActor.LastCommand, command.Key.Sequence);
        }
        if (command.Packet.Category == NebulaeModel.Packets.Factory.ManualBuildIntent.Category)
        {
            actors.TryGetValue(command.ConnectionPlayerId, out var builder);
            var built = Multiplayer.Session.ManualBuilds.Execute(command, builder?.Player);
            if (builder != null) ResetInventoryShadow(builder);
            return built;
        }
        if (command.Packet.Category == NebulaeModel.Packets.GameHistory.ResearchContributionIntent.Category)
            return Multiplayer.Session.Research.Execute(command);
        if (command.Packet.Category == NebulaeModel.Packets.Factory.Assembler.FactoryProductIntent.Category)
        {
            actors.TryGetValue(command.ConnectionPlayerId, out var factoryActor);
            if (factoryActor == null && GameMain.data.gameDesc.isCombatMode) return new CommandOutcome(CommandResultCode.RejectedNotReady);
            if (factoryActor != null) factoryActor.LastCommand = Math.Max(factoryActor.LastCommand, command.Key.Sequence);
            var outcome = Multiplayer.Session.FactoryProducts.Execute(command, factoryActor?.Player);
            if (factoryActor != null) ResetInventoryShadow(factoryActor);
            return outcome;
        }
        if (command.Packet.Category == TurretIntent.Category)
        {
            if (!actors.TryGetValue(command.ConnectionPlayerId, out var turretActor)) return new CommandOutcome(CommandResultCode.RejectedNotReady);
            turretActor.LastCommand = Math.Max(turretActor.LastCommand, command.Key.Sequence);
            var result = Multiplayer.Session.Turrets.Execute(command, turretActor.Player);
            ResetInventoryShadow(turretActor);
            return result;
        }
        if (command.Packet.Category != CombatIntent.Category || !CombatIntent.TryDecode(command.Packet.Payload, out var intent))
            return new CommandOutcome(CommandResultCode.RejectedInvalid);
        if (!actors.TryGetValue(command.ConnectionPlayerId, out var actor)) return new CommandOutcome(CommandResultCode.RejectedNotReady);
        actor.LastCommand = Math.Max(actor.LastCommand, command.Key.Sequence);
        if (!actor.Player.isAlive) return new CommandOutcome(CommandResultCode.RejectedNotReady);
        Prepare(actor);
        // Every intent except the configuration reports is a combat action — shooting, bombing,
        // bursting a shield, waking a hive, launching a fleet. Settings and the fleet enable mask
        // say nothing about a fight, so they must not hold a player's scopes at the hot cadence.
        if (intent.Kind is not (CombatIntentKind.Settings or CombatIntentKind.GroundFleetMask))
            NoteEngagement(command.ConnectionPlayerId);
        var mecha = actor.Player.mecha;
        var beforeEnergy = mecha.coreEnergy;
        bool applied;
        using (CombatManager.CombatAs(command.ConnectionPlayerId))
        {
            switch (intent.Kind)
            {
                case CombatIntentKind.Settings:
                    if (intent.X < 0 || intent.X > mecha.coreEnergyCap || intent.Y < 0 || intent.Y > actor.CoreDebit)
                        return new CommandOutcome(CommandResultCode.RejectedInvalid);
                    if (!CombatShieldSettings.TryDecode(intent, out var shieldEnabled, out var shieldSpeed))
                        return new CommandOutcome(CommandResultCode.RejectedInvalid);
                    if (!CombatWeaponSettings.TryDecode(intent.Argument, out var ammoMode, out var laserMode, out var bombActive))
                        return new CommandOutcome(CommandResultCode.RejectedInvalid);
                    mecha.energyShieldRechargeEnabled = shieldEnabled;
                    mecha.energyShieldRechargeSpeed = shieldSpeed;
                    CombatFleetSettings.Apply(intent.Argument, mecha.groundCombatModule, mecha.spaceCombatModule);
                    mecha.coreEnergy = Math.Max(0, intent.X - (actor.CoreDebit - intent.Y));
                    actor.FrameEnergy += mecha.coreEnergy - beforeEnergy;
                    mecha.ammoSelectSlot = intent.Argument & 3;
                    mecha.laserActive = (intent.Argument & 4) != 0;
                    mecha.ammoSelectSlotState = ammoMode; mecha.laserActiveState = laserMode; mecha.bombActive = bombActive;
                    mecha.autoReplenishAmmo = (intent.Argument & 32) != 0;
                    mecha.autoReplenishHangar = (intent.Argument & 64) != 0;
                    mecha.autoReplenishFuel = (intent.Argument & 128) != 0;
                    ApplyFleetEnabled(mecha.spaceCombatModule, (intent.Argument & 16) != 0, intent.Argument >> 8);
                    // Ground fleet enablement arrives in its own mask; Settings only owns the
                    // module enable bit here.
                    ApplyModuleEnabled(mecha.groundCombatModule, (intent.Argument & 8) != 0);
                    CancelDisallowedShots(actor.AmmoIntents, mecha, false);
                    CancelDisallowedShots(actor.LaserIntents, mecha, true);
                    return new CommandOutcome(CommandResultCode.Applied, HostTick);
                case CombatIntentKind.GroundFleetMask:
                    if (!TryApplyGroundFleetMask(mecha.groundCombatModule, intent.Argument))
                        return new CommandOutcome(CommandResultCode.RejectedInvalid);
                    applied = true;
                    break;
                case CombatIntentKind.Shoot:
                case CombatIntentKind.ManualShoot:
                case CombatIntentKind.Wake:
                    if (actor.Player.warping) return new CommandOutcome(CommandResultCode.RejectedNotReady);
                    if (!command.Packet.TryGetTargetKey(out var key) || TargetWorld == null ||
                        !TargetWorld.TryReadState(key, out _) || (key.Kind != PoolKind.GroundEnemy && key.Kind != PoolKind.SpaceEnemy))
                        return new CommandOutcome(CommandResultCode.RejectedTarget);
                    var target = new SkillTarget
                    {
                        type = ETargetType.Enemy,
                        id = key.NativeId,
                        astroId = key.Kind == PoolKind.GroundEnemy ? key.Scope : GameMain.spaceSector.enemyPool[key.NativeId].originAstroId
                    };
                    if (key.Kind == PoolKind.GroundEnemy && key.Scope != actor.Data.LocalPlanetId)
                        return new CommandOutcome(CommandResultCode.RejectedTarget);
                    if (intent.Kind == CombatIntentKind.Wake)
                    {
                        if (key.Kind == PoolKind.SpaceEnemy)
                        {
                            if (!actor.Player.sailing) return new CommandOutcome(CommandResultCode.RejectedTarget);
                            GameMain.spaceSector.skillSystem.GetObjectUPositionAndVelocity(ref target, out var wakePosition, out _);
                            if ((wakePosition - actor.Player.uPosition).sqrMagnitude > 400000000) return new CommandOutcome(CommandResultCode.RejectedTarget);
                            GameMain.spaceSector.GetHiveByAstroId(target.astroId)?.Realize();
                        }
                        else
                        {
                            var wakeFactory = actor.Action.localFactory;
                            if (wakeFactory == null) return new CommandOutcome(CommandResultCode.RejectedNotReady);
                            ref var wakeEnemy = ref wakeFactory.enemyPool[key.NativeId];
                            if ((wakeEnemy.pos - (VectorLF3)actor.Player.position).sqrMagnitude > 8100) return new CommandOutcome(CommandResultCode.RejectedTarget);
                            var owner = wakeEnemy.owner;
                            var wakeBase = owner > 0 && owner < wakeFactory.enemySystem.bases.cursor ? wakeFactory.enemySystem.bases.buffer[owner] : null;
                            if (wakeBase?.id != owner) return new CommandOutcome(CommandResultCode.RejectedTarget);
                            wakeBase.UnderAttack();
                        }
                        return new CommandOutcome(CommandResultCode.Applied, HostTick);
                    }
                    var ammo = (EAmmoType)intent.Argument;
                    if (!CombatWeaponSettings.Allows(mecha, ammo == EAmmoType.Laser, intent.Kind == CombatIntentKind.ManualShoot))
                        return new CommandOutcome(CommandResultCode.RejectedNotReady);
                    if (!ValidWeaponTarget(actor, key, ammo, intent.Kind == CombatIntentKind.ManualShoot)) return new CommandOutcome(CommandResultCode.RejectedTarget);
                    if (!PlayerWeaponTargets.IsMechaAmmo(ammo)) return new CommandOutcome(CommandResultCode.RejectedInvalid);
                    if (!WithinWeaponRange(actor, key, ammo, intent.Kind == CombatIntentKind.ManualShoot))
                        return new CommandOutcome(CommandResultCode.RejectedTarget);
                    if (ammo != EAmmoType.Laser && (intent.X < 0 || intent.X >= 12000 || intent.X != (int)intent.X ||
                        intent.Y < 0 || intent.Y > mecha.ammoStorage.size || intent.Y != (int)intent.Y || intent.X > 0 && intent.Y == 0))
                        return new CommandOutcome(CommandResultCode.RejectedInvalid);
                    var inputs = ammo == EAmmoType.Laser ? actor.LaserIntents : actor.AmmoIntents;
                    if (inputs.Count >= 64) return new CommandOutcome(CommandResultCode.RejectedNotReady);
                    inputs.Enqueue((key, ammo, HostTick, new PlayerShotId(command.ConnectionPlayerId, command.Key.Connection.Value, command.Key.Sequence), intent.Kind == CombatIntentKind.ManualShoot, (int)intent.X, (int)intent.Y));
                    applied = true;
                    break;
                case CombatIntentKind.ActivateGroundFormation:
                    if (actor.Player.warping || !command.Packet.TryGetTargetKey(out var baseKey) || baseKey.Kind != PoolKind.Base ||
                        baseKey.Scope != actor.Data.LocalPlanetId || TargetWorld == null || !TargetWorld.TryReadState(baseKey, out _))
                        return new CommandOutcome(CommandResultCode.RejectedTarget);
                    var formId = intent.Argument;
                    var portId = (int)intent.X;
                    if (formId < 0 || formId > 2 || portId != intent.X || portId <= 0 || portId > GroundBaseStateCodec.PortCount(formId) || intent.Y != 0 || intent.Z != 0)
                        return new CommandOutcome(CommandResultCode.RejectedInvalid);
                    var activationFactory = actor.Action.localFactory;
                    if (activationFactory == null) return new CommandOutcome(CommandResultCode.RejectedNotReady);
                    var activationBase = baseKey.NativeId < activationFactory.enemySystem.bases.cursor ? activationFactory.enemySystem.bases.buffer[baseKey.NativeId] : null;
                    if (activationBase?.id != baseKey.NativeId || activationBase.enemyId <= 0 || activationBase.enemyId >= activationFactory.enemyCursor ||
                        activationFactory.enemyPool[activationBase.enemyId].id != activationBase.enemyId || activationBase.forms[formId].units[portId] != 1)
                        return new CommandOutcome(CommandResultCode.RejectedTarget);
                    var formationEnemy = new EnemyData { protoId = (short)(8128 + formId), owner = checked((short)baseKey.NativeId), port = (short)portId };
                    var formPosition = VectorLF3.zero; var formRotation = Quaternion.identity; var formVelocity = Vector3.zero;
                    formationEnemy.Formation((int)((activationFactory.planet.seed + HostTick) % 151200),
                        ref activationFactory.enemyPool[activationBase.enemyId], activationFactory.planet.realRadius, ref formPosition, ref formRotation, ref formVelocity);
                    if ((formPosition - (VectorLF3)actor.Player.position).sqrMagnitude > 8100) return new CommandOutcome(CommandResultCode.RejectedTarget);
                    activationBase.UnderAttack(formId);
                    applied = true;
                    break;
                case CombatIntentKind.Bomb:
                    if (actor.Player.warping || !mecha.bombActive) return new CommandOutcome(CommandResultCode.RejectedNotReady);
                    if (HostTick - actor.LastBombTick < 10 || mecha.bombStorage.grids[0].count <= 0)
                        return new CommandOutcome(CommandResultCode.RejectedResource);
                    var velocity = new VectorLF3(intent.X, intent.Y, intent.Z);
                    var velocityLimit = Math.Max(2000, mecha.maxSailSpeed + 1000);
                    if (velocity.sqrMagnitude > velocityLimit * velocityLimit) return new CommandOutcome(CommandResultCode.RejectedInvalid);
                    var cast = mecha.skillBombingUCenter;
                    using (new PlayerWeaponManager.HostScope(new PlayerShotId(command.ConnectionPlayerId, command.Key.Connection.Value, command.Key.Sequence)))
                        actor.Action.Bombing(ref velocity, ref cast, HostTick);
                    actor.LastBombTick = HostTick;
                    applied = true;
                    break;
                case CombatIntentKind.ShieldBurst:
                    // Progress is an input hint bounded by elapsed host time and the actual shield.
                    mecha.energyShieldBurstProgress = Math.Min(mecha.energyShieldPercentage,
                        Math.Min(Math.Max(0, Math.Min(1, intent.X)), Math.Max(0, HostTick - actor.LastBurstTick) / 75.0));
                    if (!mecha.energyShieldBurstUnlocked || !mecha.energyShieldBurstReady) return new CommandOutcome(CommandResultCode.RejectedResource);
                    using (new PlayerWeaponManager.HostScope(new PlayerShotId(command.ConnectionPlayerId, command.Key.Connection.Value, command.Key.Sequence)))
                        actor.Action.ShieldBurst();
                    mecha.ResetShieldBurstProgress(); actor.LastBurstTick = HostTick; applied = true;
                    break;
                case CombatIntentKind.FleetLaunch:
                case CombatIntentKind.FleetRecall:
                case CombatIntentKind.FleetConfigure:
                case CombatIntentKind.FleetCommand:
                case CombatIntentKind.FleetAttackTarget:
                case CombatIntentKind.FleetAttackPosition:
                case CombatIntentKind.FleetOrbit:
                case CombatIntentKind.FleetCancel:
                    var module = intent.Argument >= 100 ? mecha.spaceCombatModule : mecha.groundCombatModule;
                    var fleet = intent.Argument % 100;
                    if (intent.Argument < 0 || intent.Argument >= 200 || fleet < 0 || fleet >= module.fleetCount)
                        return new CommandOutcome(CommandResultCode.RejectedInvalid);
                    if (intent.Kind >= CombatIntentKind.FleetAttackTarget)
                    {
                        if (!module.moduleEnabled || !module.moduleFleets[fleet].fleetEnabled ||
                            !module.isSpace && actor.Action.localFactory?.planetId != module.moduleFleets[fleet].fleetAstroId ||
                            !FleetCommandMode.TryGetPool(module, fleet, out var orderPool))
                            return new CommandOutcome(CommandResultCode.RejectedNotReady);
                        ref var orderedFleet = ref orderPool.buffer[module.moduleFleets[fleet].fleetId];
                        if (intent.Kind == CombatIntentKind.FleetCancel) orderedFleet.CancelOrder();
                        else
                        {
                            if (!module.moduleFleets[fleet].inCommand) return new CommandOutcome(CommandResultCode.RejectedNotReady);
                            if (intent.Kind == CombatIntentKind.FleetAttackTarget)
                            {
                                if (!command.Packet.TryGetTargetKey(out var orderKey) || TargetWorld == null || !TargetWorld.TryReadState(orderKey, out _) ||
                                    orderKey.Kind != (module.isSpace ? PoolKind.SpaceEnemy : PoolKind.GroundEnemy) ||
                                    !module.isSpace && orderKey.Scope != actor.Data.LocalPlanetId)
                                    return new CommandOutcome(CommandResultCode.RejectedTarget);
                                var enemy = module.isSpace ? GameMain.spaceSector.enemyPool[orderKey.NativeId] : actor.Action.localFactory.enemyPool[orderKey.NativeId];
                                if (enemy.id != orderKey.NativeId || enemy.isInvincible)
                                    return new CommandOutcome(CommandResultCode.RejectedTarget);
                                var orderTarget = new SkillTarget { type = ETargetType.Enemy, id = enemy.id, astroId = enemy.originAstroId };
                                orderedFleet.GiveAttackTargetOrder(ref orderTarget);
                            }
                            else if (intent.Kind == CombatIntentKind.FleetOrbit)
                            {
                                var orbit = GameMain.galaxy.PlanetById((int)intent.X);
                                if (!module.isSpace || intent.X != (int)intent.X || orbit == null || orbit.star.id != actor.Data.LocalStarId)
                                    return new CommandOutcome(CommandResultCode.RejectedTarget);
                                orderedFleet.GiveOrbitingAstroOrder(orbit.astroId);
                            }
                            else
                            {
                                var destination = new VectorLF3(intent.X, intent.Y, intent.Z);
                                VectorLF3 origin = module.isSpace ? actor.Player.uPosition : actor.Player.position;
                                var rotation = module.isSpace ? actor.Player.uRotation : Quaternion.Inverse(actor.Player.planetData.runtimeRotation) * actor.Player.uRotation;
                                if (!module.isSpace && (destination - origin).sqrMagnitude >
                                    Configs.freeMode.mechaCommandGroundFleetRange * Configs.freeMode.mechaCommandGroundFleetRange)
                                    return new CommandOutcome(CommandResultCode.RejectedTarget);
                                orderedFleet.GiveAttackPosOrder(ref origin, ref rotation, destination, module.isSpace);
                            }
                        }
                    }
                    else if (intent.Kind == CombatIntentKind.FleetCommand)
                    {
                        if (intent.X != 0 && intent.X != 1) return new CommandOutcome(CommandResultCode.RejectedInvalid);
                        if (intent.X == 1 && (!module.moduleEnabled || !module.moduleFleets[fleet].fleetEnabled) ||
                            module.moduleFleets[fleet].fleetId > 0 && !module.isSpace &&
                            actor.Action.localFactory?.planetId != module.moduleFleets[fleet].fleetAstroId)
                            return new CommandOutcome(CommandResultCode.RejectedNotReady);
                        if (!FleetCommandMode.Apply(module, fleet, intent.X == 1))
                            return new CommandOutcome(CommandResultCode.RejectedNotReady);
                    }
                    else if (intent.Kind == CombatIntentKind.FleetConfigure)
                    {
                        var configuration = (int)intent.X;
                        if (configuration != intent.X || (module.isSpace
                            ? !FleetProto.kMechaFleetSpaceIds.Contains(configuration)
                            : LDB.items.Select(configuration)?.isGroundFighter != true))
                            return new CommandOutcome(CommandResultCode.RejectedInvalid);
                        if (!FleetConfiguration.IsCurrent(module, fleet, configuration) && !FleetConfiguration.TryPrepare(module, fleet))
                            return new CommandOutcome(CommandResultCode.RejectedNotReady);
                        var enabledBeforeConfiguration = module.moduleFleets[fleet].fleetEnabled;
                        module.ChangeFleetConfig(fleet, configuration, mecha.fighterStorage, actor.Player);
                        module.moduleFleets[fleet].fleetEnabled = enabledBeforeConfiguration;
                    }
                    else if (intent.Kind == CombatIntentKind.FleetRecall)
                    { module.RecycleFleet(fleet); }
                    else
                    {
                        if (!module.moduleEnabled || !module.moduleFleets[fleet].fleetEnabled || actor.Player.warping ||
                            !module.isSpace && (actor.Player.sailing || actor.Action.localFactory == null))
                            return new CommandOutcome(CommandResultCode.RejectedNotReady);
                        if (!module.FleetHasFighter(fleet)) return new CommandOutcome(CommandResultCode.RejectedResource);
                        module.LaunchFleet(fleet, module.isSpace ? null : actor.Action.localFactory, actor.Player);
                        module.moduleFleets[fleet].inCommand = true;
                    }
                    applied = true;
                    break;
                default: return new CommandOutcome(CommandResultCode.RejectedInvalid);
            }
        }
        actor.CoreDebit += Math.Max(0, beforeEnergy - mecha.coreEnergy);
        CaptureInventoryDebits(actor);
        return new CommandOutcome(applied ? CommandResultCode.Applied : CommandResultCode.RejectedTarget, HostTick);
    }

    private static bool WithinWeaponRange(Actor actor, ObjectKey key, EAmmoType ammo, bool manual)
    {
        if (key.Kind == PoolKind.GroundEnemy)
        {
            var delta = actor.Action.localFactory.enemyPool[key.NativeId].pos - (VectorLF3)actor.Player.mecha.skillTargetLCenter;
            var range = PlayerWeaponTargets.Range(actor.Action.GetAmmoLocalAttackRange(ammo), manual);
            return delta.sqrMagnitude <= range * range * 1.05;
        }
        ref var enemy = ref GameMain.spaceSector.enemyPool[key.NativeId];
        if (!actor.Player.sailing && PlayerWeaponTargets.IsGroundRelay(actor.Action.localFactory, enemy))
            return PlayerWeaponTargets.GroundRelayInRange(actor.Action, actor.Mecha, enemy, ammo, manual);
        var target = new SkillTarget { type = ETargetType.Enemy, id = key.NativeId, astroId = enemy.originAstroId };
        if (!GameMain.spaceSector.skillSystem.GetObjectUPositionAndVelocity(ref target, out var position, out _)) return false;
        var spaceRange = PlayerWeaponTargets.Range(actor.Action.GetAmmoSpaceAttackRange(ammo), manual);
        return (position - actor.Player.mecha.skillTargetUCenter).sqrMagnitude <= spaceRange * spaceRange * 1.05;
    }

    private static bool ValidWeaponTarget(Actor actor, ObjectKey key, EAmmoType ammo, bool manual)
    {
        if (key.Kind == PoolKind.GroundEnemy)
        {
            var factory = actor.Action.localFactory;
            return factory != null && key.Scope == factory.planetId && key.NativeId < factory.enemyCursor &&
                factory.enemyPool[key.NativeId].id == key.NativeId && !factory.enemyPool[key.NativeId].isInvincible &&
                actor.Player.position.magnitude - factory.planet.realRadius < 150f;
        }
        if (actor.Player.warping || key.NativeId >= GameMain.spaceSector.enemyCursor) return false;
        ref var enemy = ref GameMain.spaceSector.enemyPool[key.NativeId];
        if (enemy.id != key.NativeId || enemy.isInvincible) return false;
        if (!actor.Player.sailing)
            return PlayerWeaponTargets.CanAttackGroundRelay(actor.Player, actor.Action.localFactory, enemy, manual);
        var hive = GameMain.spaceSector.GetHiveByAstroId(enemy.originAstroId);
        if (enemy.dfTinderId == 0 && hive?.starData != actor.Action.localStar) return false;
        if (manual || enemy.dfRelayId == 0) return true;
        var hatred = ammo == EAmmoType.Laser ? actor.Mecha.laserHatredTarget : actor.Mecha.ammoHatredTarget;
        return hatred.notNull && hatred.id == enemy.id && hatred.astroId == enemy.originAstroId;
    }

    private static void CancelQueue(Queue<(ObjectKey Target, EAmmoType Ammo, long Tick, PlayerShotId Shot, bool Manual, int Item, int Slot)> queue)
    {
        while (queue.Count > 0) Multiplayer.Session.PlayerWeapons.Cancel(queue.Dequeue().Shot);
    }
    private static void CancelShots(Actor actor) { CancelQueue(actor.AmmoIntents); CancelQueue(actor.LaserIntents); }
    private static void CancelDisallowedShots(Queue<(ObjectKey Target, EAmmoType Ammo, long Tick, PlayerShotId Shot, bool Manual, int Item, int Slot)> queue, Mecha mecha, bool laser)
    {
        for (var remaining = queue.Count; remaining > 0; remaining--)
        {
            var input = queue.Dequeue();
            if (CombatWeaponSettings.Allows(mecha, laser, input.Manual)) queue.Enqueue(input);
            else Multiplayer.Session.PlayerWeapons.Cancel(input.Shot);
        }
    }
    private static void ExpireShots(Queue<(ObjectKey Target, EAmmoType Ammo, long Tick, PlayerShotId Shot, bool Manual, int Item, int Slot)> queue, long tick)
    {
        while (queue.Count > 0 && tick - queue.Peek().Tick > 120) Multiplayer.Session.PlayerWeapons.Cancel(queue.Dequeue().Shot);
    }


    private SkillTarget FirePending(Actor actor, bool laser)
    {
        var queue = laser ? actor.LaserIntents : actor.AmmoIntents;
        while (queue.Count > 0)
        {
            var input = queue.Dequeue(); var key = input.Target;
            var fired = false;
            var selectedSlot = actor.Mecha.ammoSelectSlot;
            try
            {
                if (!CombatWeaponSettings.Allows(actor.Mecha, laser, input.Manual)) continue;
                if (HostTick - input.Tick > 120 || !actor.Player.isAlive || actor.Player.warping ||
                    TargetWorld == null || !TargetWorld.TryReadState(key, out _) ||
                    key.Kind == PoolKind.GroundEnemy && key.Scope != actor.Data.LocalPlanetId) continue;
                if (!ValidWeaponTarget(actor, key, input.Ammo, input.Manual)) continue;
                var target = new SkillTarget
                {
                    type = ETargetType.Enemy,
                    id = key.NativeId,
                    astroId = key.Kind == PoolKind.GroundEnemy ? key.Scope : GameMain.spaceSector.enemyPool[key.NativeId].originAstroId
                };
                if (!WithinWeaponRange(actor, key, input.Ammo, input.Manual)) continue;
                var mecha = actor.Player.mecha;
                if (!laser)
                {
                    if (input.Slot > 0) mecha.ammoSelectSlot = input.Slot;
                    if (mecha.ammoBulletCount <= 0 || input.Item > 0 && mecha.ammoItemId != input.Item)
                    {
                        if (input.Item > 0 && mecha.ammoStorage.grids[input.Slot - 1].itemId != input.Item) continue;
                        mecha.LoadAmmo(findAnother: input.Item == 0);
                    }
                    if (input.Ammo != mecha.activeAmmoType || mecha.ammoBulletCount <= 0 || input.Item > 0 && mecha.ammoItemId != input.Item) continue;
                }
                using (new PlayerWeaponManager.HostScope(input.Shot)) fired = actor.Action.ShootTarget(input.Ammo, target);
                if (!fired) continue;
                if (!laser) PlayerWeaponResources.ConsumeRound(mecha);
                return target;
            }
            finally
            {
                // An older queued shot may load its own slot, but cannot change the pilot's selection.
                actor.Mecha.ammoSelectSlot = selectedSlot;
                if (!fired) Multiplayer.Session.PlayerWeapons.Cancel(input.Shot);
            }
        }
        return SkillTarget.none;
    }

    public void CompleteFrame(long tick)
    {
        if (!Multiplayer.Session.IsServer || !Multiplayer.Session.IsGameLoaded) return;
        if (lastCompletedTick == tick) return;
        lastCompletedTick = tick;
        // The host's own mecha is simulated natively and has no authority actor, so it is watched
        // directly: the host's scopes must go hot when the host is the one fighting.
        if (!Multiplayer.Session.IsDedicated) ObserveEngagement(Multiplayer.Session.LocalPlayer.Id, GameMain.mainPlayer?.mecha);
        foreach (var pair in actors)
        {
            var actor = pair.Value;
            if (actor.DetachedFight != null) continue;
            var player = actor.Player; var mecha = player.mecha;
            ObserveEngagement(pair.Key, mecha);
            // Disconnect removes the Unity model before the next completed frame is drained.
            // Let BeginFrame reclaim the actor instead of blocking that cleanup behind a dead transform.
            if (player.transform == null) continue;
            Prepare(actor);
            RefreshCombatLimits(mecha, GameMain.mainPlayer.mecha);
            ExpireShots(actor.AmmoIntents, tick); ExpireShots(actor.LaserIntents, tick);
            if (player.isAlive)
            {
                if (player.invincibleTicks > 0) player.invincibleTicks--;
                mecha.UpdateCombatStats(1.0 / 60);
                if (mecha.ammoBulletCount <= 0 && mecha.ammoSelectSlot > 0) mecha.LoadAmmo();
                var upgrade = default(CombatUpgradeData); GameMain.history.GetCombatUpgradeData(ref upgrade);
                using (CombatManager.CombatAs(pair.Key))
                {
                    if (mecha.ammoSelectSlot > 0) mecha.TickAmmoFireCondition(() => FirePending(actor, false));
                    if (mecha.laserActive) mecha.TickLaserFireCondition(() => FirePending(actor, true));
                    if (actor.Action.localFactory != null) mecha.groundCombatModule.GameTick(tick, actor.Action.localFactory, player, ref upgrade);
                    mecha.spaceCombatModule.GameTick(tick, actor.Action.localFactory, player, ref upgrade);
                }
            }
            actor.CoreDebit = actor.FrameDebit + Math.Max(actor.CoreDebit - actor.FrameDebit, Math.Max(0, actor.FrameEnergy - mecha.coreEnergy));
            CaptureInventoryDebits(actor);
            PublishActor(pair.Key, actor, tick);
        }
    }

    public void FighterPortChanged(ushort playerId)
    {
        if (actors.TryGetValue(playerId, out var actor)) actor.FighterPortsDirty = true;
    }

    // Publish completed gestures while paused without running another combat or production tick.
    public void FlushFighterPorts()
    {
        if (!Multiplayer.Session.IsServer || !Multiplayer.Session.IsGameLoaded) return;
        foreach (var pair in actors)
            if (pair.Value.FighterPortsDirty && pair.Value.DetachedFight == null)
                PublishActor(pair.Key, pair.Value, GameMain.gameTick);
    }

    private void PublishActor(ushort playerId, Actor actor, long tick, bool force = false)
    {
        var player = actor.Player; var mecha = player.mecha;
        actor.Data.Mecha.FightData = new MechaFightData(player);
        actor.Data.Mecha.CoreEnergy = mecha.coreEnergy;
        actor.Data.Life.IsAlive = player.isAlive; actor.Data.Life.DeathCount = player.deathCount;
        actor.Data.Life.InvincibleTicks = player.invincibleTicks;
        if (!force && tick % 6 != 0 && !actor.FighterPortsDirty && !Multiplayer.Session.PlayerWeapons.NeedsSettlement(playerId)) return;
        var previous = actor.LastPublishedCombat;
        var payload = SerializeReplicableFightData(actor.Data.Mecha.FightData, previous?.CombatData);
        if (previous != null && ReferenceEquals(previous.CombatData, payload) &&
            previous.LastCommandSequence == actor.LastCommand && previous.PersonalRevision == actor.Data.Life.Revision &&
            previous.CoreEnergyDebitTotal == actor.CoreDebit && previous.IsAlive == player.isAlive &&
            previous.DeathCount == player.deathCount && previous.InvincibleTicks == player.invincibleTicks &&
            previous.HpRecoverCD == mecha.hpRecoverCD && previous.ShieldRecoverCD == mecha.energyShieldRecoverCD &&
            SameDebits(previous, actor.Debits.Totals) && !Multiplayer.Session.PlayerWeapons.NeedsSettlement(playerId)) { actor.FighterPortsDirty = false; return; }
        var packet = new AuthorityPlayerCombatStatePacket
        {
            PlayerId = playerId,
            LastCommandSequence = actor.LastCommand,
            PersonalRevision = actor.Data.Life.Revision,
            ActorConnection = Multiplayer.Session.AuthorityRuntime.ConnectionEpochFor(playerId).Value,
            CoreEnergyDebitTotal = actor.CoreDebit,
            IsAlive = player.isAlive,
            DeathCount = player.deathCount,
            InvincibleTicks = player.invincibleTicks,
            HpRecoverCD = mecha.hpRecoverCD,
            ShieldRecoverCD = mecha.energyShieldRecoverCD,
            CombatData = payload
        };
        packet.DebitItems = actor.Debits.Totals.Keys.ToArray();
        packet.DebitTotals = actor.Debits.Totals.Values.ToArray();
        // this packet is a reconciliation channel, not a heartbeat. Everything it
        // carries is either a scalar the host gates on here or part of this payload, and the two
        // cumulative ledgers (core debit, item debits) are applied as deltas by the receiver, so
        // an unchanged packet would teach a recipient nothing. An idle player — the common case
        // in a session — then costs nothing instead of a full MechaFightData per recipient every
        // six ticks, while a player who is fighting still publishes on every change.
        packet.CombatRevision = ++actor.Revision;
        Multiplayer.Session.PlayerWeapons.Settle(playerId, packet.CombatRevision);
        packet.SettledAttacks = Multiplayer.Session.PlayerWeapons.SettledSequences(playerId, packet.ActorConnection);
        packet.SetHeader(new AuthorityEnvelopeHeader(AuthorityFamily.PlayerCombatState,
            Multiplayer.Session.Authority.Epoch, default, actor.Revision, tick, 0, packet.DeclaredPayloadLength));
        RememberStocks(actor);
        actor.LastPublishedCombat = packet;
        byte[] encoded = null;
        foreach (var recipient in Multiplayer.Session.Server.Players.Connected.Values)
            if (recipient.Id == playerId || recipient.Data.LocalStarId == actor.Data.LocalStarId)
            {
                encoded ??= Multiplayer.Session.Network.WriteOnce(packet);
                recipient.Connection.SendRawPacket(encoded);
            }
        actor.FighterPortsDirty = false;
    }

    /// <summary>
    /// Serializes one actor's fight data for the reconciliation channel, with the host-only counter
    /// normalized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CombatModuleComponent.tickSinceLastEnemy</c> is advanced by the host's own module
    /// <c>GameTick</c> — which <see cref="CompleteFrame"/> calls every tick — and is never read on the
    /// receiving side, because a client suppresses <c>CombatModuleComponent.GameTick</c> entirely.
    /// Shipping the live value would therefore make every packet differ every six ticks, and the
    /// change gate would stop suppressing anything for a player who is merely standing by with combat
    /// modules enabled: a full blob per recipient, forever, describing nothing that moved.
    /// </para>
    /// <para>
    /// The counter is written as a constant and restored immediately afterwards. The restore is not
    /// optional: the record's constructor aliases the live mecha's modules by reference, so writing
    /// the constant straight into it would silently reset the host's own timer and change its fleet
    /// behaviour. It sits in a <c>finally</c> because the size check throws, and an abandoned
    /// normalization must not outlive the call.
    /// </para>
    /// <para>
    /// Checkpoints and saves serialize the same record through their own path and keep the real
    /// counter, so save fidelity is unaffected by what goes on the wire.
    /// </para>
    /// </remarks>
    internal static byte[] SerializeReplicableFightData(IMechaFightData fight, byte[] previous = null)
    {
        var ground = fight.GroundCombatModule;
        var space = fight.SpaceCombatModule;
        var groundCounter = ground.tickSinceLastEnemy;
        var spaceCounter = space.tickSinceLastEnemy;
        ground.tickSinceLastEnemy = 0;
        space.tickSinceLastEnemy = 0;
        try
        {
            var writer = fightWriter ??= new NetDataWriter();
            writer.Reset();
            fight.Serialize(writer);
            if (writer.Length > 65536) throw new InvalidOperationException("Player combat payload exceeds its bound.");
            return previous != null && previous.Length == writer.Length &&
                NebulaeModel.Networking.FieldDeltaCodec.Equal(previous, 0, writer.Data, 0, writer.Length) ? previous : writer.CopyData();
        }
        finally
        {
            ground.tickSinceLastEnemy = groundCounter;
            space.tickSinceLastEnemy = spaceCounter;
        }
    }

    private static bool SameDebits(AuthorityPlayerCombatStatePacket previous, IReadOnlyDictionary<int, int> totals)
    {
        if (previous.DebitItems.Length != totals.Count || previous.DebitTotals.Length != totals.Count) return false;
        var index = 0;
        foreach (var pair in totals)
        {
            if (previous.DebitItems[index] != pair.Key || previous.DebitTotals[index] != pair.Value) return false;
            index++;
        }
        return true;
    }

    /// <summary>
    /// Whether a settings report says anything the host has not already been told.
    /// </summary>
    /// <remarks>
    /// Compared through the tuple rather than by re-encoding, because the report is four values and
    /// there is nothing to encode. The sentinel can never match a real report: the flag word is a
    /// small non-negative bitfield, so it can never equal <see cref="NoSettingsSent"/>'s
    /// <see cref="int.MinValue"/>, whatever the three energy values happen to be. That is what keeps
    /// the first report after a world change from being suppressed, and it is the property the test
    /// locks rather than the tuple comparison itself.
    /// </remarks>
    internal static bool IsUnchangedSettingsReport(
        (int Flags, double Energy, double DebitAck, double ShieldSpeed) previous,
        (int Flags, double Energy, double DebitAck, double ShieldSpeed) next) => previous == next;

    public bool Send(CombatIntent intent, SkillTarget target = default) => Send(intent, target, out _);

    public bool Send(CombatIntent intent, SkillTarget target, out long commandSequence)
    {
        commandSequence = 0;
        var session = Multiplayer.Session;
        if (session.IsServer || !session.Authority.IsActive) return false;
        if (intent.Kind is CombatIntentKind.FleetLaunch or CombatIntentKind.FleetCommand or
            CombatIntentKind.FleetAttackTarget or CombatIntentKind.FleetAttackPosition or CombatIntentKind.FleetOrbit)
            SendSettings(GameMain.gameTick, immediate: true);
        if (!sendingEpoch.Equals(session.Authority.Epoch) || !sendingConnection.Equals(session.Authority.Connection))
        { sendingEpoch = session.Authority.Epoch; sendingConnection = session.Authority.Connection; sequence = 0; }
        var key = default(ObjectKey);
        if (target.id > 0)
        {
            var ground = target.astroId > 0 && target.astroId <= 1000000;
            if (!session.AuthorityRuntime.WorldReplica.TryFindKey(ground ? PoolKind.GroundEnemy : PoolKind.SpaceEnemy,
                    ground ? target.astroId : 0, target.id, out key)) return false;
        }
        return SendCommand(key, CombatIntent.Category, intent.Encode(), out commandSequence);
    }

    public bool SendCommand(ObjectKey key, byte category, byte[] payload, out long commandSequence)
    {
        commandSequence = 0;
        var session = Multiplayer.Session;
        if (session.IsServer || !session.Authority.IsActive) return false;
        if (!sendingEpoch.Equals(session.Authority.Epoch) || !sendingConnection.Equals(session.Authority.Connection))
        { sendingEpoch = session.Authority.Epoch; sendingConnection = session.Authority.Connection; sequence = 0; }
        commandSequence = ++sequence;
        var header = new AuthorityEnvelopeHeader(AuthorityFamily.Command,
            sendingEpoch, sendingConnection, commandSequence, GameMain.gameTick, session.LocalPlayer.Id, payload.Length);
        session.Network.SendPacket(AuthorityCommandPacket.Create(header, key, category, payload));
        return true;
    }

    public bool ApplyState(AuthorityPlayerCombatStatePacket packet)
    {
        if (packet.SettledAttacks == null || packet.SettledAttacks.Length > 256 || packet.SettledAttacks.Any(x => x <= 0)) return false;
        if (packet.DebitItems == null || packet.DebitTotals == null || packet.DebitItems.Length != packet.DebitTotals.Length ||
            packet.DebitItems.Length > 512 || double.IsNaN(packet.CoreEnergyDebitTotal) || double.IsInfinity(packet.CoreEnergyDebitTotal) ||
            packet.CoreEnergyDebitTotal < 0) return false;
        var epoch = new AuthorityEpoch(packet.EpochHigh, packet.EpochLow);
        var session = Multiplayer.Session;
        var ownPacket = packet.PlayerId == session.LocalPlayer.Id;
        if (session.Authority?.IsActive == true && (epoch != session.Authority.Epoch ||
            ownPacket && session.Authority.Connection.IsValid && packet.ActorConnection != session.Authority.Connection.Value)) return true;
        if (!receivingEpoch.Equals(epoch))
        {
            var replacingWorld = receivingEpoch.IsValid;
            receivingEpoch = epoch; received.Clear(); receivedConnections.Clear(); acknowledgedDebits.Clear(); CoreDebitAcknowledged = 0; LastAppliedRevision = LastAppliedCommand = 0;
            if (replacingWorld)
            {
                observedStocks = null; publishedStockHistory.Clear(); publishedPersonalRevision = 0; observedPersonalRevision = 0; personalStocksDirty = false;
            }
            // A new world means a new host that has never been told this player's configuration, so
            // the cached report must not suppress the first one it needs.
            lastSettings = NoSettingsSent;
        }
        if (receivedConnections.TryGetValue(packet.PlayerId, out var connection) && packet.ActorConnection < connection) return true;
        if (connection != packet.ActorConnection)
        {
            receivedConnections[packet.PlayerId] = packet.ActorConnection; received.Remove(packet.PlayerId);
            if (ownPacket)
            {
                acknowledgedDebits.Clear(); CoreDebitAcknowledged = 0; LastAppliedRevision = LastAppliedCommand = 0;
                lastSettings = NoSettingsSent;
            }
        }
        if (received.TryGetValue(packet.PlayerId, out var previous) && packet.CombatRevision <= previous) return true;
        using (Multiplayer.Session.World.GetRemotePlayersModels(out var models))
        {
            var own = packet.PlayerId == Multiplayer.Session.LocalPlayer.Id;
            var player = own ? GameMain.mainPlayer : models.TryGetValue(packet.PlayerId, out var model) ? model.PlayerInstance : null;
            if (player == null) return false;
            if (own)
            {
                if (packet.CoreEnergyDebitTotal < CoreDebitAcknowledged || packet.LastCommandSequence < LastAppliedCommand) return false;
                for (var i = 0; i < packet.DebitItems.Length; i++)
                {
                    acknowledgedDebits.TryGetValue(packet.DebitItems[i], out var acknowledged);
                    if (packet.DebitItems[i] <= 0 || !CombatConsumptionLedger.TryDelta(acknowledged, packet.DebitTotals[i], out _)) return false;
                }
            }
            // A completed local respawn can precede its host checkpoint; remote life messages
            // can also precede queued combat facts from the dead life.
            // Keep its life/energy state, but still acknowledge combat revisions and stocks:
            // the host needs that baseline to accept the respawn-completion checkpoint.
            var staleLife = packet.DeathCount < player.deathCount ||
                player.isAlive && !packet.IsAlive && packet.DeathCount == player.deathCount;
            var fight = new MechaFightData(); fight.Deserialize(new NetDataReader(packet.CombatData));
            var mecha = player.mecha;
            var muzzle = mecha.ammoMuzzleFire; var round = mecha.ammoRoundFire; var muzzleIndex = mecha.ammoMuzzleIndex;
            var laserFire = mecha.laserFire; var bombFire = mecha.bombFire;
            var personalStocks = own ? CopyStocks(mecha) : null;
            var stockBaseline = own && packet.PersonalRevision > observedPersonalRevision && publishedStockHistory.TryGetValue(packet.PersonalRevision, out var confirmedStock)
                ? confirmedStock : observedStocks;
            if (own && stockBaseline == null && personalStocksDirty)
            {
                // Preserve edits until a checkpoint establishes the starting inventory.
                Multiplayer.Session.Life?.Publish();
                return false;
            }
            var serverStocks = own ? new[] { CloneStock(fight.AmmoStorage), CloneStock(fight.BombStorage), CloneStock(fight.FighterStorage) } : null;
            var rebasedStocks = new Dictionary<long, StorageComponent[]>();
            if (staleLife)
            {
                fight.Hp = mecha.hp;
                fight.EnergyShieldEnergy = mecha.energyShieldEnergy;
                fight.LaserEnergy = mecha.laserEnergy;
                fight.LaserRecharging = mecha.laserRecharging;
            }
            if (own)
            {
                fight.AmmoSelectSlot = mecha.ammoSelectSlot; fight.LaserActive = mecha.laserActive;
                fight.AmmoSelectSlotState = mecha.ammoSelectSlotState; fight.LaserActiveState = mecha.laserActiveState; fight.BombActive = mecha.bombActive;
                fight.AutoReplenishFuel = mecha.autoReplenishFuel; fight.AutoReplenishAmmo = mecha.autoReplenishAmmo;
                fight.AutoReplenishHangar = mecha.autoReplenishHangar;
                fight.GroundCombatModule.moduleEnabled = mecha.groundCombatModule.moduleEnabled;
                fight.SpaceCombatModule.moduleEnabled = mecha.spaceCombatModule.moduleEnabled;
                CopyFleetSettings(fight.GroundCombatModule, mecha.groundCombatModule);
                CopyFleetSettings(fight.SpaceCombatModule, mecha.spaceCombatModule);
                if (stockBaseline != null && (personalStocksDirty || packet.PersonalRevision < publishedPersonalRevision))
                {
                    fight.AmmoStorage = MergeStock(serverStocks[0], stockBaseline[0], personalStocks[0]);
                    fight.BombStorage = MergeStock(serverStocks[1], stockBaseline[1], personalStocks[1]);
                    fight.FighterStorage = MergeStock(serverStocks[2], stockBaseline[2], personalStocks[2]);
                }
                // A pending checkpoint can fail to rebase even when the live stock is valid.
                // Prepare all three stocks for every checkpoint before changing the mecha or
                // any baseline, otherwise the next packet would apply its deltas twice.
                if (stockBaseline != null)
                    foreach (var pending in publishedStockHistory.Where(x => x.Key > packet.PersonalRevision))
                        rebasedStocks[pending.Key] = new[]
                        {
                            MergeStock(serverStocks[0], stockBaseline[0], pending.Value[0]),
                            MergeStock(serverStocks[1], stockBaseline[1], pending.Value[1]),
                            MergeStock(serverStocks[2], stockBaseline[2], pending.Value[2])
                        };
            }
            fight.UpdateMech(player);
            if (!staleLife)
            {
                mecha.hpRecoverCD = packet.HpRecoverCD;
                mecha.energyShieldRecoverCD = packet.ShieldRecoverCD;
            }
            if (own)
            {
                observedStocks = serverStocks;
                foreach (var pending in rebasedStocks) publishedStockHistory[pending.Key] = pending.Value;
                foreach (var acknowledged in publishedStockHistory.Keys.Where(x => x <= packet.PersonalRevision).ToArray()) publishedStockHistory.Remove(acknowledged);
                observedPersonalRevision = packet.PersonalRevision;
                mecha.ammoMuzzleFire = muzzle; mecha.ammoRoundFire = round; mecha.ammoMuzzleIndex = muzzleIndex;
                mecha.laserFire = laserFire; mecha.bombFire = bombFire;
                for (var i = 0; i < packet.DebitItems.Length; i++)
                {
                    var item = packet.DebitItems[i]; var total = packet.DebitTotals[i];
                    acknowledgedDebits.TryGetValue(item, out var oldTotal);
                    if (!CombatConsumptionLedger.TryDelta(oldTotal, total, out var delta)) return false;
                    var paid = 0;
                    if (delta > 0) { paid = delta; player.package.TakeTailItems(ref item, ref paid, out _); }
                    // An item moved out of the package is still owed. Retry the unpaid balance
                    // on later facts; acknowledging it would allow a stale checkpoint to refund it.
                    acknowledgedDebits[packet.DebitItems[i]] = oldTotal + paid;
                }
                mecha.coreEnergy = Math.Max(0, mecha.coreEnergy - Math.Max(0, packet.CoreEnergyDebitTotal - CoreDebitAcknowledged));
                CoreDebitAcknowledged = packet.CoreEnergyDebitTotal; LastAppliedRevision = packet.CombatRevision;
                LastAppliedCommand = packet.LastCommandSequence;
                if (Multiplayer.Session.PlayerWeapons != null)
                {
                    foreach (var sequence in packet.SettledAttacks)
                        Multiplayer.Session.PlayerWeapons.Resources.Settle(new PlayerShotId(packet.PlayerId, packet.ActorConnection, sequence), packet.CombatRevision);
                    Multiplayer.Session.PlayerWeapons.Resources.Applied(packet.CombatRevision);
                }
                if (!staleLife && !packet.IsAlive && player.isAlive) player.Kill();
            }
            else if (!staleLife) player.isAlive = packet.IsAlive;
            if (!staleLife)
            {
                player.deathCount = packet.DeathCount;
                player.invincibleTicks = packet.InvincibleTicks;
            }
            Multiplayer.Session.PlayerWeapons?.Resources.Rebase(mecha, packet.HostTick);
        }
        received[packet.PlayerId] = packet.CombatRevision;
        return true;
    }

    public void AcceptCheckpoint(ushort playerId, PlayerData data, PlayerData snapshot, long commandAck, double debitAck,
        int[] itemAcks, int[] debitAcks, bool requestedRespawn, NebulaeModel.DataStructures.IMechaFightData personalFight, long combatRevision = 0)
    {
        if (!actors.TryGetValue(playerId, out var actor)) return;
        if (debitAck > actor.CoreDebit || itemAcks == null || debitAcks == null || itemAcks.Length != debitAcks.Length || itemAcks.Length > 512)
            throw new InvalidOperationException("Invalid combat checkpoint acknowledgement.");
        StorageComponent[] stocks = null;
        if (actor.StockHistory.TryGetValue(combatRevision, out var baseline))
            stocks = new[]
            {
                MergeStock(actor.Player.mecha.ammoStorage, baseline[0], personalFight.AmmoStorage),
                MergeStock(actor.Player.mecha.bombStorage, baseline[1], personalFight.BombStorage),
                MergeStock(actor.Player.mecha.fighterStorage, baseline[2], personalFight.FighterStorage)
            };
        var acknowledgements = new Dictionary<int, int>();
        for (var i = 0; i < itemAcks.Length; i++) acknowledgements[itemAcks[i]] = debitAcks[i];
        foreach (var debit in actor.Debits.Totals)
        {
            acknowledgements.TryGetValue(debit.Key, out var ack);
            var count = actor.Debits.Pending(debit.Key, ack); var item = debit.Key;
            if (count > 0) snapshot.Mecha.Inventory.TakeTailItems(ref item, ref count, out _);
        }
        actor.Player.SetHiddenProperty(nameof(global::Player.package), snapshot.Mecha.Inventory);
        (snapshot.Mecha as MechaData)?.ApplyHand(actor.Player);
        ResetInventoryShadow(actor);
        actor.Player.mecha.coreEnergy = Math.Max(0, snapshot.Mecha.CoreEnergy - Math.Max(0, actor.CoreDebit - debitAck));
        if (snapshot.Mecha is MechaData fuel)
        {
            actor.Player.mecha.reactorItemId = fuel.ReactorItemId;
            actor.Player.mecha.reactorItemInc = fuel.ReactorItemInc;
        }
        data.Mecha.CoreEnergy = actor.Player.mecha.coreEnergy;
        if (stocks != null)
        {
            // Apply only personal transfers since the combat facts the sender observed.
            // Copying an entire old storage would refund ammunition consumed since those facts.
            MechaFightData.UpdateStorage(actor.Player.mecha.ammoStorage, stocks[0]);
            MechaFightData.UpdateStorage(actor.Player.mecha.bombStorage, stocks[1]);
            MechaFightData.UpdateStorage(actor.Player.mecha.fighterStorage, stocks[2]);
        }
        if (commandAck >= actor.LastCommand)
        {
            var source = personalFight;
            actor.Player.mecha.ammoSelectSlot = source.AmmoSelectSlot;
            actor.Player.mecha.laserActive = source.LaserActive;
            actor.Player.mecha.ammoSelectSlotState = source.AmmoSelectSlotState;
            actor.Player.mecha.laserActiveState = source.LaserActiveState;
            actor.Player.mecha.bombActive = source.BombActive;
            actor.Player.mecha.autoReplenishAmmo = source.AutoReplenishAmmo;
            actor.Player.mecha.autoReplenishHangar = source.AutoReplenishHangar;
            CopyFleetSettings(actor.Player.mecha.groundCombatModule, source.GroundCombatModule);
            CopyFleetSettings(actor.Player.mecha.spaceCombatModule, source.SpaceCombatModule);
        }
        if (!actor.Player.isAlive && requestedRespawn &&
            Multiplayer.Session.PropertyTransactions.OwnsCommittedRespawn(snapshot.Life.TransactionId, data.PersistentId, actor.Player.deathCount))
        {
            actor.Player.mecha.Respawn(); actor.Player.isAlive = true; actor.Player.invincibleTicks = 180;
            data.Life.IsAlive = true;
        }
        actor.Data = data;
    }

    public bool ValidatePersonalStocks(ushort playerId, long revision, NebulaeModel.DataStructures.IMechaFightData incoming)
    {
        if (!actors.TryGetValue(playerId, out var actor)) return true;
        if (incoming == null || !actor.StockHistory.TryGetValue(revision, out var baseline) ||
            !actor.StockPersonalRevisions.TryGetValue(revision, out var personalRevision) || personalRevision < actor.Data.Life.Revision) return false;
        try
        {
            MergeStock(actor.Player.mecha.ammoStorage, baseline[0], incoming.AmmoStorage);
            MergeStock(actor.Player.mecha.bombStorage, baseline[1], incoming.BombStorage);
            MergeStock(actor.Player.mecha.fighterStorage, baseline[2], incoming.FighterStorage);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    public Mecha CraftMecha(bool space, int scope, int craftId)
    {
        var pool = space ? GameMain.spaceSector?.craftPool : GameMain.galaxy?.PlanetById(scope)?.factory?.craftPool;
        if (pool == null || craftId <= 0 || craftId >= pool.Length || pool[craftId].id != craftId) return null;
        var craft = pool[craftId];
        if (craft.owner > 0)
        {
            if (craft.owner >= pool.Length || pool[craft.owner].id != craft.owner) return null;
            craft = pool[craft.owner];
        }
        if (craft.owner != -1 || craft.fleetId <= 0) return null;
        foreach (var actor in actors.Values)
        {
            var module = actor.DetachedFight == null
                ? (space ? actor.Mecha.spaceCombatModule : actor.Mecha.groundCombatModule)
                : (space ? actor.DetachedFight.SpaceCombatModule : actor.DetachedFight.GroundCombatModule);
            if (craft.port < 0 || craft.port >= module.fleetCount) continue;
            var fleet = module.moduleFleets[craft.port];
            if (fleet.fleetId == craft.fleetId && (space || fleet.fleetAstroId == scope)) return actor.Mecha;
        }
        return null;
    }

    public static StarData CasterStar() => Multiplayer.IsActive && Multiplayer.Session.IsServer &&
        Multiplayer.Session.CombatAuthority.actors.TryGetValue((ushort)CombatManager.PlayerId, out var actor)
        ? GameMain.galaxy.StarById(actor.Data.LocalStarId) : GameMain.localStar;

    public static PlanetData CasterPlanet() => Multiplayer.IsActive && Multiplayer.Session.IsServer &&
        Multiplayer.Session.CombatAuthority.actors.TryGetValue((ushort)CombatManager.PlayerId, out var actor)
        ? actor.Player.planetData : GameMain.localPlanet;

    private void ReclaimFleets(Actor actor)
    {
        var previous = ReclaimingFleets; ReclaimingFleets = true;
        try
        {
            var fight = actor.DetachedFight ?? new MechaFightData(actor.Player);
            foreach (var module in new[] { fight.GroundCombatModule, fight.SpaceCombatModule })
                for (var i = 0; i < module.fleetCount; i++)
                {
                    ref var fleet = ref module.moduleFleets[i];
                    var factory = module.isSpace ? null : GameMain.galaxy.PlanetById(fleet.fleetAstroId)?.factory;
                    var pool = module.isSpace ? GameMain.spaceSector.craftPool : factory?.craftPool;
                    var fleets = module.isSpace ? GameMain.spaceSector.combatSpaceSystem.fleets : factory?.combatGroundSystem.fleets;
                    if (pool != null && fleet.fleetId > 0 && fleet.fleetId < fleets.cursor && fleets.buffer[fleet.fleetId].id == fleet.fleetId)
                    {
                        var root = fleets.buffer[fleet.fleetId].craftId;
                        if (CraftMecha(module.isSpace, factory?.planetId ?? 0, root) == actor.Mecha)
                        {
                            var owned = pool.Where(x => x.id > 0 && x.owner == root).Select(x => x.id).ToArray();
                            foreach (var craft in owned)
                                if (module.isSpace) GameMain.spaceSector.RemoveCraftWithComponents(craft); else factory.RemoveCraftWithComponents(craft);
                            if (module.isSpace) GameMain.spaceSector.RemoveCraftWithComponents(root); else factory.RemoveCraftWithComponents(root);
                        }
                    }
                    fleet.fleetId = 0; fleet.fleetAstroId = 0; fleet.inCommand = false;
                    for (var port = 0; port < fleet.fighters.Length; port++) fleet.fighters[port].craftId = 0;
                }
            actor.Data.Mecha.FightData = fight;
        }
        finally { ReclaimingFleets = previous; }
    }

    private static void CopyFleetSettings(CombatModuleComponent target, CombatModuleComponent source)
    {
        target.attackRelay = source.attackRelay;
        target.attackBuilding = source.attackBuilding;
        target.autoReplenishFleet = source.autoReplenishFleet;
        for (var i = 0; i < Math.Min(target.fleetCount, source.fleetCount); i++)
            target.moduleFleets[i].fleetEnabled = source.moduleFleets[i].fleetEnabled;
    }

    internal static void ApplyFleetEnabled(CombatModuleComponent module, bool enabled, int fleets)
    {
        module.moduleEnabled = enabled;
        ApplyFleetMask(module, fleets);
    }

    /// <summary>
    /// Applies a ground fleet mask if it only names slots this actor has actually unlocked.
    /// </summary>
    internal static bool TryApplyGroundFleetMask(CombatModuleComponent module, int mask)
    {
        var count = Math.Min(Math.Max(module.fleetCount, 0), 8);
        if (mask < 0 || mask >= 1 << count) return false;
        ApplyFleetMask(module, mask);
        return true;
    }

    /// <summary>Applies one fleet enable mask without touching the module's own enable bit.</summary>
    internal static void ApplyFleetMask(CombatModuleComponent module, int fleets)
    {
        for (var i = 0; i < module.fleetCount; i++)
        {
            module.moduleFleets[i].fleetEnabled = (fleets & (1 << i)) != 0;
            RemoveDisabledFleet(module, i);
        }
    }

    /// <summary>Applies the module enable bit and drops every deployed fleet when it turns off.</summary>
    internal static void ApplyModuleEnabled(CombatModuleComponent module, bool enabled)
    {
        module.moduleEnabled = enabled;
        for (var i = 0; i < module.fleetCount; i++) RemoveDisabledFleet(module, i);
    }

    private static void RemoveDisabledFleet(CombatModuleComponent module, int index)
    {
        // Match the native disable buttons: put deployed fighters back immediately.
        // Running this on the host keeps removals, ports and stock in one transaction.
        var fleet = module.moduleFleets[index];
        if ((!module.moduleEnabled || !fleet.fleetEnabled) && fleet.fleetId > 0)
            module.RemoveFleetDirectly(index);
    }

    private static StorageComponent CloneStock(StorageComponent source)
    {
        var result = new StorageComponent(source.size) { type = source.type, bans = source.bans };
        Array.Copy(source.grids, result.grids, source.grids.Length);
        return result;
    }

    private static void RememberStocks(Actor actor)
    {
        actor.StockHistory[actor.Revision] = CopyStocks(actor.Player.mecha);
        actor.StockPersonalRevisions[actor.Revision] = actor.Data.Life.Revision;
        foreach (var revision in actor.StockHistory.Keys.Where(x => x < actor.Revision - 127).ToArray())
        { actor.StockHistory.Remove(revision); actor.StockPersonalRevisions.Remove(revision); }
    }
    private static StorageComponent[] CopyStocks(Mecha mecha) => new[] { CloneStock(mecha.ammoStorage), CloneStock(mecha.bombStorage), CloneStock(mecha.fighterStorage) };

    internal static void RefreshCombatLimits(Mecha destination, Mecha source)
    {
        // Host research must not reset personal settings, fleets or airborne drones.
        destination.coreEnergyCap = source.coreEnergyCap;
        destination.reactorPowerGen = source.reactorPowerGen;
        destination.hpMaxUpgrade = source.hpMaxUpgrade;
        destination.energyShieldUnlocked = source.energyShieldUnlocked;
        destination.energyShieldRadius = source.energyShieldRadius;
        destination.energyShieldCapacity = source.energyShieldCapacity;
        destination.energyShieldEnergyRate = source.energyShieldEnergyRate;
        destination.energyShieldBurstUnlocked = source.energyShieldBurstUnlocked;
        destination.energyShieldBurstDamageRate = source.energyShieldBurstDamageRate;
        destination.laserEnergyCapacity = source.laserEnergyCapacity;
        destination.laserLocalAttackRange = source.laserLocalAttackRange;
        destination.laserSpaceAttackRange = source.laserSpaceAttackRange;
        destination.laserLocalEnergyCost = source.laserLocalEnergyCost;
        destination.laserSpaceEnergyCost = source.laserSpaceEnergyCost;
        destination.laserLocalDamage = source.laserLocalDamage;
        destination.laserSpaceDamage = source.laserSpaceDamage;
    }

    internal static StorageComponent MergeStock(StorageComponent current, StorageComponent baseline, StorageComponent incoming)
    {
        if (current == null || baseline == null || incoming == null ||
            current.type != baseline.type || current.type != incoming.type ||
            current.size != baseline.size || current.size != incoming.size)
            throw new InvalidOperationException("Incompatible combat stock layout.");
        var result = CloneStock(current);
        var transfers = baseline.grids.Concat(incoming.grids).Where(x => x.itemId > 0).Select(x => x.itemId).Distinct()
            .Select(item => (Item: item,
                Count: incoming.grids.Where(x => x.itemId == item).Sum(x => x.count) - baseline.grids.Where(x => x.itemId == item).Sum(x => x.count),
                Inc: Math.Max(0, incoming.grids.Where(x => x.itemId == item).Sum(x => x.inc) - baseline.grids.Where(x => x.itemId == item).Sum(x => x.inc))))
            .ToArray();
        // Free every outgoing stack before accepting a replacement, regardless of item order.
        foreach (var transfer in transfers.Where(x => x.Count < 0))
        {
            var item = transfer.Item; var count = -transfer.Count;
            result.TakeTailItems(ref item, ref count, out _);
            if (count != -transfer.Count) throw new InvalidOperationException("Combat stock transfer exceeds the available stock.");
        }
        var filters = new int[result.size];
        var filtersChanged = false;
        for (var i = 0; i < filters.Length; i++)
        {
            filters[i] = incoming.grids[i].filter != baseline.grids[i].filter ? incoming.grids[i].filter : current.grids[i].filter;
            filtersChanged |= filters[i] != current.grids[i].filter;
        }
        if (filtersChanged)
        {
            // SetFilter changes an empty slot's itemId and stackSize as well. Rebuild on a
            // clone so filter swaps can move surviving host stock without overwriting it.
            var layout = new StorageComponent(result.size) { type = result.type, bans = result.bans };
            for (var i = 0; i < filters.Length; i++) layout.SetFilter(i, filters[i]);
            foreach (var stock in result.grids.Where(x => x.itemId > 0 && x.count > 0).GroupBy(x => x.itemId))
            {
                var count = stock.Sum(x => x.count);
                if (layout.AddItemStacked(stock.Key, count, stock.Sum(x => x.inc), out _) != count)
                    throw new InvalidOperationException("Combat stock transfer exceeds storage capacity.");
            }
            result = layout;
        }
        foreach (var transfer in transfers.Where(x => x.Count > 0))
            if (result.AddItemStacked(transfer.Item, transfer.Count, transfer.Inc, out _) != transfer.Count)
                throw new InvalidOperationException("Combat stock transfer exceeds storage capacity.");
        return result;
    }

    public void Dispose() { foreach (var actor in actors.Values) actor.Data.CombatAuthoritative = false; actors.Clear(); received.Clear(); }
}
