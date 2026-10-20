#region

using System;

#endregion

namespace NebulaeModel.Authority;

/// <summary>
/// Which seat owns a construction drone task.
/// </summary>
/// <remarks>
/// <para>
/// An owner is represented as <c>OwnerKey = Player(PersistentIdentity, SessionPlayerId) |
/// BattleBase(EntityKey)</c>. The player half carries both the durable identity (ledger balances
/// key by it, so a reconnect keeps its stock) and the reusable session seat (the registry rebinds
/// it on reconnect, preventing task ownership from leaking across players). The base half carries
/// the host's <see cref="ObjectKey"/> for the battle base, so a reloaded world cannot inherit the
/// previous world's base tasks.
/// </para>
/// <para>
/// This is deliberately distinct from <see cref="LedgerOwner"/>: the ledger keys money by
/// persistent owner only, while dispatch keys work by the session seat as well, because "who pays"
/// and "whose drones fly this tick" are different questions. The virtual server seat
/// (<see cref="HostPlayerRole.HeadlessDedicated"/>) never becomes a task owner; callers check
/// <c>HostPlayerState.CanOwnDroneTask</c> before submitting work. This type only checks identity
/// shape, never eligibility.
/// </para>
/// </remarks>
internal enum ConstructionOwnerKind : byte
{
    Unknown = 0,
    Player = 1,
    BattleBase = 2
}

/// <summary>
/// One construction drone-task owner: a durable player seat or a host-identified battle base.
/// </summary>
internal readonly struct ConstructionOwnerKey : IEquatable<ConstructionOwnerKey>, IComparable<ConstructionOwnerKey>
{
    private readonly ConstructionOwnerKind kind;
    private readonly string persistentId;
    private readonly ushort sessionPlayerId;
    private readonly ObjectKey baseKey;

    private ConstructionOwnerKey(ConstructionOwnerKind kind, string persistentId, ushort sessionPlayerId,
        ObjectKey baseKey)
    {
        this.kind = kind;
        this.persistentId = persistentId;
        this.sessionPlayerId = sessionPlayerId;
        this.baseKey = baseKey;
    }

    /// <summary>
    /// A player seat. The persistent id is durable; the session id is the reusable seat.
    /// </summary>
    public static ConstructionOwnerKey ForPlayer(string persistentId, ushort sessionPlayerId)
    {
        if (string.IsNullOrEmpty(persistentId) || sessionPlayerId == 0) return default;
        return new ConstructionOwnerKey(ConstructionOwnerKind.Player, persistentId, sessionPlayerId, default);
    }

    public ConstructionOwnerKind Kind => kind;

    public string PersistentId => persistentId;

    public ushort SessionPlayerId => sessionPlayerId;

    public ObjectKey BaseKey => baseKey;

    public bool IsPlayer => kind == ConstructionOwnerKind.Player;

    public bool IsBase => kind == ConstructionOwnerKind.BattleBase;

    public bool IsValid =>
        kind == ConstructionOwnerKind.Player
            ? !string.IsNullOrEmpty(persistentId) && sessionPlayerId != 0
            : kind == ConstructionOwnerKind.BattleBase && baseKey.IsValid && baseKey.Kind == PoolKind.Base;

    /// <summary>
    /// Deterministic tie-break order after the placer-first rule: players before bases, then persistent id,
    /// session seat and base key.
    /// </summary>
    public static int StableCompare(in ConstructionOwnerKey left, in ConstructionOwnerKey right)
    {
        var byKind = left.kind.CompareTo(right.kind);
        if (byKind != 0) return byKind;
        switch (left.kind)
        {
            case ConstructionOwnerKind.Player:
                var byPersistent = string.Compare(left.persistentId, right.persistentId,
                    StringComparison.Ordinal);
                if (byPersistent != 0) return byPersistent;
                return left.sessionPlayerId.CompareTo(right.sessionPlayerId);
            case ConstructionOwnerKind.BattleBase:
                var byScope = left.baseKey.Scope.CompareTo(right.baseKey.Scope);
                if (byScope != 0) return byScope;
                var byNative = left.baseKey.NativeId.CompareTo(right.baseKey.NativeId);
                if (byNative != 0) return byNative;
                var byGen = left.baseKey.Generation.CompareTo(right.baseKey.Generation);
                if (byGen != 0) return byGen;
                return left.baseKey.Epoch.CompareTo(right.baseKey.Epoch);
            default:
                return 0;
        }
    }

    public int CompareTo(ConstructionOwnerKey other) => StableCompare(in this, in other);

    public bool Equals(ConstructionOwnerKey other) =>
        kind == other.kind &&
        string.Equals(persistentId, other.persistentId, StringComparison.Ordinal) &&
        sessionPlayerId == other.sessionPlayerId &&
        baseKey.Equals(other.baseKey);

    public override bool Equals(object obj) => obj is ConstructionOwnerKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = (int)kind;
            hash = (hash * 397) ^ (persistentId != null ? persistentId.GetHashCode() : 0);
            hash = (hash * 397) ^ sessionPlayerId.GetHashCode();
            hash = (hash * 397) ^ baseKey.GetHashCode();
            return hash;
        }
    }

    public static bool operator ==(ConstructionOwnerKey left, ConstructionOwnerKey right) => left.Equals(right);

    public static bool operator !=(ConstructionOwnerKey left, ConstructionOwnerKey right) => !left.Equals(right);

    public override string ToString() =>
        kind == ConstructionOwnerKind.Player ? "player(" + persistentId + "#" + sessionPlayerId + ")" :
        kind == ConstructionOwnerKind.BattleBase ? "base(" + baseKey + ")" : "invalid-owner";
}
