#region

using System;
using System.Collections.Generic;

#endregion

namespace NebulaeModel.Authority;

/// <summary>
/// The read side of the host's death ledger: which exact keys died.
/// </summary>
/// <remarks>
/// <para>
/// The frame-end replicator publishes a membership removal for every object that left a pool, and
/// vanilla has two very different removal paths: the Kill path (<c>KillEnemyFinally</c> /
/// <c>KillEnemyFinal</c>), which plays the wreckage and the death audio, and <c>RemoveEnemyFinal</c>,
/// which removes silently — a unit returning to its formation, a relay that landed, a builder the
/// hive reclaimed. Only the first is a death, and the ledger is the one place that knows which:
/// <c>CombatStat.HandleZeroHp</c> is the sole capture point, and its transactions are committed at
/// the frame boundary before the replicator's scan publishes the frame's despawns.
/// </para>
/// <para>
/// The query is by exact <see cref="ObjectKey"/>, generation included, so a recycled slot is a new
/// object rather than a resurrected one. A session without a registered view publishes every removal
/// as a plain despawn: a missed presentation is recoverable, a false one is not.
/// </para>
/// </remarks>
internal interface IHostDeathView
{
    /// <summary>True while a tombstone exists for this exact key (same epoch and generation).</summary>
    bool IsDead(in ObjectKey key);
}

/// <summary>
/// What kind of object died, expressed in authority identity terms.
/// </summary>
/// <remarks>
/// <para>
/// Vanilla names damage targets with <c>EObjectType</c> (Entity/Vegetable/Vein/Prebuild/Enemy/Ruin/
/// Craft) plus the enemy's pool split (planet factory vs space sector). That enum is not the wire
/// identity: the same number means different pools, and players have no EObjectType at
/// all. This enum is the death-ledger's own classification, mapped once from the vanilla values so
/// the aggregation cannot silently merge a ground enemy with a space enemy or lose a vegetable.
/// </para>
/// </remarks>
internal enum HostDeathObjectKind : byte
{
    Unknown = 0,

    /// <summary>A placed building (<c>PlanetFactory.entityPool</c>).</summary>
    Entity = 1,

    /// <summary>Destructible vegetation (<c>PlanetFactory.vegePool</c>).</summary>
    Vegetable = 2,

    /// <summary>A minable vein (<c>PlanetFactory.veinPool</c>).</summary>
    Vein = 3,

    /// <summary>A dark fog unit on a planet (<c>factory.enemyPool</c>).</summary>
    GroundEnemy = 4,

    /// <summary>A dark fog unit in the sector (<c>spaceSector.enemyPool</c>).</summary>
    SpaceEnemy = 5,

    /// <summary>A player fleet fighter, ground or space (<c>craftPool</c>).</summary>
    Craft = 6,

    /// <summary>A dark fog planetary base.</summary>
    Base = 7,

    /// <summary>A player mecha. No vanilla pool: identified by its persistent owner.</summary>
    Player = 8
}

/// <summary>The per-kind policy table for death side effects.</summary>
/// <remarks>
/// <para>Each damage target kind has an explicit policy for the side effects executed once when its death
/// transaction opens, including targets whose effects are display-only.</para>
/// <para>Shields absorb damage for the object behind them and are not independent death targets. The host
/// computes absorption; client shield animations cannot write shield capacity or energy.</para>
/// </remarks>
internal static class HostDeathKinds
{
    /// <summary>Maps a vanilla <c>EObjectType</c> value plus the enemy pool split to a death kind.</summary>
    /// <remarks>
    /// Prebuild (3) and Ruin (5) are build-domain records, not damage targets with deaths, and an
    /// unknown value maps to <see cref="HostDeathObjectKind.Unknown"/> so a game update cannot
    /// invent a silent classification.
    /// </remarks>
    public static HostDeathObjectKind FromVanillaObjectType(int objectType, bool spaceEnemy)
    {
        switch (objectType)
        {
            case 0: return HostDeathObjectKind.Entity;      // EObjectType.Entity
            case 1: return HostDeathObjectKind.Vegetable;   // EObjectType.Vegetable
            case 2: return HostDeathObjectKind.Vein;        // EObjectType.Vein
            case 4:
                return spaceEnemy
                ? HostDeathObjectKind.SpaceEnemy
                : HostDeathObjectKind.GroundEnemy;          // EObjectType.Enemy
            case 6: return HostDeathObjectKind.Craft;       // EObjectType.Craft
            default: return HostDeathObjectKind.Unknown;    // Prebuild/Ruin/None
        }
    }

    /// <summary>The death-kind a pool object carries. False for pools that cannot die.</summary>
    public static bool TryFromPoolKind(PoolKind kind, out HostDeathObjectKind deathKind)
    {
        switch (kind)
        {
            case PoolKind.Entity: deathKind = HostDeathObjectKind.Entity; return true;
            case PoolKind.Vegetable: deathKind = HostDeathObjectKind.Vegetable; return true;
            case PoolKind.Vein: deathKind = HostDeathObjectKind.Vein; return true;
            case PoolKind.GroundEnemy: deathKind = HostDeathObjectKind.GroundEnemy; return true;
            case PoolKind.SpaceEnemy: deathKind = HostDeathObjectKind.SpaceEnemy; return true;
            case PoolKind.GroundCraft:
            case PoolKind.SpaceCraft: deathKind = HostDeathObjectKind.Craft; return true;
            case PoolKind.Base: deathKind = HostDeathObjectKind.Base; return true;
            default: deathKind = HostDeathObjectKind.Unknown; return false;
        }
    }

    /// <summary>True when the object's destruction is one loot/drop event (e.g. enemy drops, player inventory).</summary>
    public static bool ProducesLoot(HostDeathObjectKind kind)
    {
        switch (kind)
        {
            case HostDeathObjectKind.GroundEnemy:
            case HostDeathObjectKind.SpaceEnemy:
            case HostDeathObjectKind.Vegetable:
            case HostDeathObjectKind.Player:
                return true;
            default:
                return false;
        }
    }

    /// <summary>True when the death is one <c>KillStatisticsManager</c> entry.</summary>
    public static bool CountsKillStatistics(HostDeathObjectKind kind) =>
        kind == HostDeathObjectKind.GroundEnemy || kind == HostDeathObjectKind.SpaceEnemy;

    /// <summary>True when dependent construction tasks must be released once (repair target dies, owner dies).</summary>
    public static bool ReleasesTasks(HostDeathObjectKind kind) =>
        kind == HostDeathObjectKind.Entity || kind == HostDeathObjectKind.Player ||
        kind == HostDeathObjectKind.Base;
}

/// <summary>
/// The immutable record of one death transaction, shared by the opener, the binding and the guards.
/// </summary>
internal readonly struct HostDeathReceipt : IEquatable<HostDeathReceipt>
{
    internal HostDeathReceipt(ObjectKey target, string persistentId, HostDeathObjectKind kind,
        long deathTick, long transactionId, long finalRevision, ConstructionOwnerKey owner,
        bool isFirst, bool saturated, int tasksReleased)
    {
        Target = target;
        PersistentId = persistentId;
        Kind = kind;
        DeathTick = deathTick;
        TransactionId = transactionId;
        FinalRevision = finalRevision;
        Owner = owner;
        IsFirst = isFirst;
        Saturated = saturated;
        TasksReleased = tasksReleased;
    }

    /// <summary>The dead pool object. Invalid for a player death.</summary>
    public ObjectKey Target { get; }

    /// <summary>The dead player's persistent identity, or null for a pool object.</summary>
    public string PersistentId { get; }

    public HostDeathObjectKind Kind { get; }

    public long DeathTick { get; }

    /// <summary>Ledger-assigned identity of this death transaction.</summary>
    public long TransactionId { get; }

    /// <summary>The object revision the death published, when the caller had one.</summary>
    public long FinalRevision { get; }

    /// <summary>For Player/Base deaths, the owner whose tasks are released. Default otherwise.</summary>
    public ConstructionOwnerKey Owner { get; }

    /// <summary>True only for the call that opened the death and ran the side effects.</summary>
    public bool IsFirst { get; }

    /// <summary>True when the ledger was full and refused to track this death (fail-closed).</summary>
    public bool Saturated { get; }

    /// <summary>Construction tasks the binding released, or -1 when the ledger refused the death.</summary>
    public int TasksReleased { get; }

    public bool HasTombstone => !Saturated && (Target.IsValid || !string.IsNullOrEmpty(PersistentId));

    public bool Equals(HostDeathReceipt other) =>
        Target.Equals(other.Target) && PersistentId == other.PersistentId && Kind == other.Kind &&
        DeathTick == other.DeathTick && TransactionId == other.TransactionId &&
        FinalRevision == other.FinalRevision && Owner.Equals(other.Owner) &&
        IsFirst == other.IsFirst && Saturated == other.Saturated && TasksReleased == other.TasksReleased;

    public override bool Equals(object obj) => obj is HostDeathReceipt other && Equals(other);

    public override int GetHashCode() => TransactionId.GetHashCode();

    public override string ToString() =>
        "kind=" + Kind + "|target=" + (Target.IsValid ? Target.ToString() : (PersistentId ?? "-")) +
        "|tick=" + DeathTick + "|tx=" + TransactionId + "|first=" + IsFirst +
        (Saturated ? "|saturated" : string.Empty) + "|tasks=" + TasksReleased;
}

/// <summary>
/// The side effects of one death, each executed exactly once by the ledger.
/// </summary>
/// <remarks>
/// The ledger owns the once-only guarantee; implementations own the actual work. A game adapter
///  binds the vanilla statistics/drop calls; <see cref="ConstructionTaskDeathBinding"/> is the
/// model-side implementation that releases construction tasks. An implementation must be
/// idempotent-unreachable rather than idempotent: the ledger never calls it twice for one death.
/// </remarks>
internal interface IHostDeathBinding
{
    /// <summary>Records the single kill-statistics entry for this death.</summary>
    void RecordStatistics(in HostDeathReceipt receipt);

    /// <summary>Grants the single loot/drop for this death.</summary>
    void GrantLoot(in HostDeathReceipt receipt);

    /// <summary>Releases dependent construction tasks. Returns how many were released.</summary>
    int ReleaseTasks(in HostDeathReceipt receipt);
}

/// <summary>
/// Aggregates several bindings so a session can attach statistics, loot and task release
/// independently without the ledger serialising them.
/// </summary>
internal sealed class CompositeDeathBinding : IHostDeathBinding
{
    private readonly IHostDeathBinding[] bindings;

    public CompositeDeathBinding(params IHostDeathBinding[] bindings)
    {
        this.bindings = bindings ?? Array.Empty<IHostDeathBinding>();
    }

    public void RecordStatistics(in HostDeathReceipt receipt)
    {
        foreach (var binding in bindings) binding.RecordStatistics(in receipt);
    }

    public void GrantLoot(in HostDeathReceipt receipt)
    {
        foreach (var binding in bindings) binding.GrantLoot(in receipt);
    }

    public int ReleaseTasks(in HostDeathReceipt receipt)
    {
        var total = 0;
        foreach (var binding in bindings) total += binding.ReleaseTasks(in receipt);
        return total;
    }
}

/// <summary>
/// Releases construction tasks that reference a dead object or a dead player.
/// </summary>
/// <remarks>
/// The death order is "HP终结 → 释放依赖/维修任务 → 原版结构移除 → 掉落/统计". This
/// binding implements the second step over <see cref="ConstructionTaskLedger"/>: every live task
/// whose target names the dead key is cancelled once with <c>TargetDestroyed</c>, and a dead
/// player's own tasks are cancelled once with <c>OwnerDead</c>. Repeated <c>OpenDeath</c> calls for
/// the same key never reach this binding again, so <c>repairerCount</c> and the drone budget move
/// exactly once.
/// </remarks>


/// <summary>
/// One tracked death: the tombstone record the guards read and the counts the binding produced.
/// </summary>
internal sealed class HostDeathRecord
{
    internal HostDeathReceipt Receipt { get; set; }

    internal HostDeathReceipt ReceiptWith(bool isFirst, int tasksReleased) =>
        new(Receipt.Target, Receipt.PersistentId, Receipt.Kind, Receipt.DeathTick, Receipt.TransactionId,
            Receipt.FinalRevision, Receipt.Owner, isFirst, Receipt.Saturated, tasksReleased);

    internal HostDeathReceipt PublicReceipt => ReceiptWith(isFirst: false, tasksReleased: Receipt.TasksReleased);
}

/// <summary>
/// The host's once-per-generation death ledger.
/// </summary>
/// <remarks>
/// <para>
/// Every source that sees a target reach zero HP funnels into
/// <see cref="OpenDeath"/>, and only the first call for a key opens the transaction, runs the
/// binding and publishes the tombstone. Every later call for the same key — another weapon's
/// killing blow arriving the same frame, a replayed report — gets the cached receipt with
/// <c>IsFirst=false</c> and moves nothing. That is the structural answer to "一目标被多种武器同时
/// 击杀，只生成一死亡事务、一份掉落、一笔统计".
/// </para>
/// <para>
/// Tombstones are per <see cref="ObjectKey"/> including generation, so a recycled slot is a new
/// key with a clean state, and no same-generation Alive/HP
/// message can revive the dead: <see cref="CanAcceptAliveState"/> and <see cref="CanAcceptHealing"/>
/// answer false for a tombstoned key. Recovery happens only through
/// <see cref="TryRecoverForNewBaseline"/> with a strictly increasing baseline id — a TTL or a
/// capacity eviction would be a resurrection bug, so an exhausted ledger refuses new deaths
/// (<c>Saturated</c>) instead of forgetting old ones.
/// </para>
/// <para>
/// Player deaths live in their own namespace keyed by persistent identity. A player legitimately
/// returns from the dead through the host's respawn path, which is the only thing that clears a
/// player tombstone (<see cref="ApplyHostRespawn"/>); a stale client life packet cannot.
/// </para>
/// </remarks>
internal sealed class HostDeathLedger : IHostDeathView
{
    /// <summary>Default capacity that bounds tombstone allocation.</summary>
    public const int DefaultCapacity = 1 << 16;

    private readonly AuthorityEpoch epoch;
    private readonly IHostDeathBinding binding;
    private readonly int capacity;
    private readonly Dictionary<ObjectKey, HostDeathRecord> poolDeaths = new();
    private readonly Dictionary<string, HostDeathRecord> playerDeaths = new(StringComparer.Ordinal);
    private long deathSequence;
    private long recoveryBaseline;

    public HostDeathLedger(AuthorityEpoch epoch, IHostDeathBinding binding = null, int capacity = DefaultCapacity)
    {
        if (!epoch.IsValid) throw new ArgumentException("A death ledger needs a valid epoch.", nameof(epoch));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.epoch = epoch;
        this.binding = binding;
        this.capacity = capacity;
    }

    public AuthorityEpoch Epoch => epoch;

    /// <summary>Deaths opened since creation, including recovered ones after a baseline swap.</summary>
    public int DeathCount { get; private set; }

    /// <summary>Live tombstones. Never trimmed except by a confirmed new baseline or a reset.</summary>
    public int TombstoneCount => poolDeaths.Count + playerDeaths.Count;

    /// <summary>True once the ledger refused a death for capacity. Fail-closed, never self-healing.</summary>
    public bool IsSaturated { get; private set; }

    /// <summary>
    /// Opens the death transaction for a pool object. First call wins; later calls are cached no-ops.
    /// </summary>
    /// <remarks>
    /// The kind must match the key's pool. A mismatched kind is a caller bug, not a hostile packet:
    /// the receipt records it via <c>Kind=Unknown</c> semantics by refusing (default receipt), and
    /// the caller surfaces the refusal rather than the ledger guessing what died.
    /// </remarks>
    public HostDeathReceipt OpenDeath(in ObjectKey target, long hostTick, long finalRevision,
        ConstructionOwnerKey owner = default)
    {
        if (!target.IsValid || !target.Epoch.Equals(epoch)) return default;
        if (!HostDeathKinds.TryFromPoolKind(target.Kind, out var kind)) return default;
        if (poolDeaths.TryGetValue(target, out var existing))
        {
            return existing.PublicReceipt;
        }
        if (TombstoneCount >= capacity)
        {
            IsSaturated = true;
            return new HostDeathReceipt(target, null, kind, hostTick, 0, finalRevision, owner,
                isFirst: false, saturated: true, tasksReleased: -1);
        }

        // The record is stored before the binding runs: if the binding throws, a retry observes the
        // tombstone and returns IsFirst=false instead of executing the side effects twice. A death
        // whose binding failed is visible (DeathCount vs. completed bindings) rather than silent.
        DeathCount++;
        var record = new HostDeathRecord
        {
            Receipt = new HostDeathReceipt(target, null, kind, hostTick, ++deathSequence,
                finalRevision, owner, isFirst: true, saturated: false, tasksReleased: 0)
        };
        poolDeaths[target] = record;

        var tasksReleased = 0;
        if (binding != null)
        {
            // The classification table gates each side effect, so a binding is only ever asked for
            // what its kind owes (an enemy owes statistics and a drop, a building owes task
            // release, a player owes the inventory drop — and nothing else).
            if (HostDeathKinds.CountsKillStatistics(kind)) binding.RecordStatistics(record.Receipt);
            if (HostDeathKinds.ProducesLoot(kind)) binding.GrantLoot(record.Receipt);
            if (HostDeathKinds.ReleasesTasks(kind)) tasksReleased = binding.ReleaseTasks(record.Receipt);
        }
        record.Receipt = record.ReceiptWith(isFirst: true, tasksReleased);
        return record.Receipt;
    }

    /// <summary>Opens the death transaction for a player, keyed by persistent identity.</summary>
    public HostDeathReceipt OpenPlayerDeath(string persistentId, ConstructionOwnerKey owner,
        long hostTick, long finalRevision = 0)
    {
        if (string.IsNullOrEmpty(persistentId)) return default;
        if (playerDeaths.TryGetValue(persistentId, out var existing))
        {
            return existing.PublicReceipt;
        }
        if (TombstoneCount >= capacity)
        {
            IsSaturated = true;
            return new HostDeathReceipt(default, persistentId, HostDeathObjectKind.Player, hostTick, 0,
                finalRevision, owner, isFirst: false, saturated: true, tasksReleased: -1);
        }

        DeathCount++;
        var record = new HostDeathRecord
        {
            Receipt = new HostDeathReceipt(default, persistentId, HostDeathObjectKind.Player, hostTick,
                ++deathSequence, finalRevision, owner, isFirst: true, saturated: false, tasksReleased: 0)
        };
        playerDeaths[persistentId] = record;

        var tasksReleased = 0;
        if (binding != null)
        {
            // Same gating as the pool path: a player owes one inventory drop and one task release,
            // and is never a kill-statistics entry.
            if (HostDeathKinds.ProducesLoot(HostDeathObjectKind.Player)) binding.GrantLoot(record.Receipt);
            if (HostDeathKinds.ReleasesTasks(HostDeathObjectKind.Player))
                tasksReleased = binding.ReleaseTasks(record.Receipt);
        }
        record.Receipt = record.ReceiptWith(isFirst: true, tasksReleased);
        return record.Receipt;
    }

    /// <summary>True while a tombstone exists for this exact key (same generation).</summary>
    public bool IsDead(in ObjectKey target) =>
        target.IsValid && target.Epoch.Equals(epoch) && poolDeaths.ContainsKey(target);

    /// <summary>True while the player's death tombstone is open.</summary>
    public bool IsPlayerDead(string persistentId) =>
        !string.IsNullOrEmpty(persistentId) && playerDeaths.ContainsKey(persistentId);

    /// <summary>
    /// Whether a same-generation Alive/HP/Pose message may be accepted for this key.
    /// </summary>
    /// <remarks>
    /// Unconditionally false for a tombstoned key: death is final for that generation, and a "newer revision"
    /// Alive would be exactly the resurrection the tombstone exists to
    /// prevent. A newer generation is a different key and never consults this tombstone.
    /// </remarks>
    public bool CanAcceptAliveState(in ObjectKey target) => !IsDead(target);

    /// <summary>Whether a FullHp/heal may touch this key. Dead objects cannot be healed back.</summary>
    public bool CanAcceptHealing(in ObjectKey target) => !IsDead(target);

    /// <summary>
    /// Clears a player tombstone through the host's own respawn path. Returns true when one existed.
    /// </summary>
    /// <remarks>
    /// This is deliberately the only revival entry: the host simulation's Respawn action calls
    /// it, so "who may un-die a player" has one answer. Pool objects never revive — a recycled slot
    /// is a new generation, which is a different key, not a cleared tombstone.
    /// </remarks>
    public bool ApplyHostRespawn(string persistentId) => playerDeaths.Remove(persistentId);

    /// <summary>
    /// Releases every tombstone once a strictly newer snapshot baseline is confirmed.
    /// </summary>
    /// <remarks>
    /// Recovery is refused unless the baseline id is strictly greater than the last recovered one,
    /// so replaying an old confirmation cannot silently drop tombstones while the stream still
    /// delivers pre-baseline messages. Callers must have completed the new baseline install before
    /// asking; the ledger cannot verify that, which is why the id is monotonic rather than trusted.
    /// </remarks>
    public bool TryRecoverForNewBaseline(long confirmedBaselineId)
    {
        if (confirmedBaselineId <= recoveryBaseline) return false;
        recoveryBaseline = confirmedBaselineId;
        poolDeaths.Clear();
        playerDeaths.Clear();
        return true;
    }

    /// <summary>Last baseline id accepted by <see cref="TryRecoverForNewBaseline"/>, or 0.</summary>
    public long RecoveryBaseline => recoveryBaseline;

    /// <summary>Clears every tombstone. Used when the world ends; never as a repair.</summary>
    public void Clear()
    {
        poolDeaths.Clear();
        playerDeaths.Clear();
        DeathCount = 0;
        deathSequence = 0;
        IsSaturated = false;
    }
}
