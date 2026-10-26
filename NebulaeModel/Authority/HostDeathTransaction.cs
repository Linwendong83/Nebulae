using System;
using System.Collections.Generic;

namespace NebulaeModel.Authority;

/// <summary>Deaths observed by the host, keyed by the exact object generation.</summary>
internal interface IHostDeathView
{
    bool IsDead(in ObjectKey key);
}

/// <summary>The result of recording one native death at the frame boundary.</summary>
internal readonly struct HostDeathReceipt
{
    internal HostDeathReceipt(ObjectKey target, long deathTick, long transactionId, bool isFirst, bool saturated)
    {
        Target = target;
        DeathTick = deathTick;
        TransactionId = transactionId;
        IsFirst = isFirst;
        Saturated = saturated;
    }

    public ObjectKey Target { get; }
    public long DeathTick { get; }
    public long TransactionId { get; }
    public bool IsFirst { get; }
    public bool Saturated { get; }
    public bool HasTombstone => !Saturated && Target.IsValid;
}

/// <summary>
/// Records native pool deaths so replication can distinguish a death from a silent despawn.
/// Drops, statistics and player life transitions are committed by their native runtime paths.
/// Tombstones remain for the world lifetime; a recycled slot has a different generation.
/// </summary>
internal sealed class HostDeathLedger : IHostDeathView
{
    public const int DefaultCapacity = 1 << 16;

    private readonly AuthorityEpoch epoch;
    private readonly int capacity;
    private readonly Dictionary<ObjectKey, HostDeathReceipt> poolDeaths = new();
    private long deathSequence;

    public HostDeathLedger(AuthorityEpoch epoch, int capacity = DefaultCapacity)
    {
        if (!epoch.IsValid) throw new ArgumentException("A death ledger needs a valid epoch.", nameof(epoch));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.epoch = epoch;
        this.capacity = capacity;
    }
    public int DeathCount => poolDeaths.Count;
    public bool IsSaturated { get; private set; }

    public HostDeathReceipt OpenDeath(in ObjectKey target, long hostTick)
    {
        if (!target.IsValid || !target.Epoch.Equals(epoch) || !IsDeathTarget(target.Kind)) return default;
        if (poolDeaths.TryGetValue(target, out var existing))
            return new HostDeathReceipt(target, existing.DeathTick, existing.TransactionId, isFirst: false, saturated: false);
        if (poolDeaths.Count >= capacity)
        {
            IsSaturated = true;
            return new HostDeathReceipt(target, hostTick, 0, isFirst: false, saturated: true);
        }

        var receipt = new HostDeathReceipt(target, hostTick, ++deathSequence, isFirst: true, saturated: false);
        poolDeaths.Add(target, receipt);
        return receipt;
    }

    private static bool IsDeathTarget(PoolKind kind) => kind is
        PoolKind.Entity or PoolKind.Vegetable or PoolKind.Vein or PoolKind.GroundEnemy or
        PoolKind.SpaceEnemy or PoolKind.GroundCraft or PoolKind.SpaceCraft or PoolKind.Base;

    public bool IsDead(in ObjectKey target) =>
        target.IsValid && target.Epoch.Equals(epoch) && poolDeaths.ContainsKey(target);

    public void Clear()
    {
        poolDeaths.Clear();
        deathSequence = 0;
        IsSaturated = false;
    }
}
