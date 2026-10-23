using System;

namespace NebulaeModel.Authority;

/// <summary>
/// Which vanilla pool an <see cref="ObjectKey"/> addresses.
/// </summary>
/// <remarks>
/// The pool kind is part of the identity, because the same slot number means
/// different objects in different pools: ground enemy 7 and space enemy 7 must never compare equal.
/// </remarks>
internal enum PoolKind : byte
{
    /// <summary>
    /// Not a pool. A key with this kind is never valid, so a default-constructed
    /// <see cref="ObjectKey"/> cannot be mistaken for a real object.
    /// </summary>
    Unknown = 0,

    /// <summary><c>PlanetFactory.entityPool</c>: placed buildings and other entities.</summary>
    Entity,

    /// <summary><c>PlanetFactory.prebuildPool</c>: construction sites.</summary>
    Prebuild,

    /// <summary><c>PlanetFactory.enemyPool</c>.</summary>
    GroundEnemy,

    /// <summary><c>SpaceSector.enemyPool</c>: one pool shared by every hive.</summary>
    SpaceEnemy,

    /// <summary><c>PlanetFactory.craftPool</c>.</summary>
    GroundCraft,

    /// <summary><c>SpaceSector.craftPool</c>: one pool shared by the whole sector.</summary>
    SpaceCraft,

    /// <summary>One construction drone task, owned by a player or a base.</summary>
    DroneTask,

    /// <summary>A dark fog planetary base, reached through its factory component graph.</summary>
    Base,

    /// <summary>A dark fog hive. Identified by its own sector astro slot.</summary>
    Hive,

    /// <summary><c>PlanetFactory.vegePool</c>: destructible vegetation.</summary>
    Vegetable,

    /// <summary><c>PlanetFactory.veinPool</c>: minable veins.</summary>
    Vein,

    /// <summary>Persistent sector-wide directory and UI facts for dark fog hives.</summary>
    HiveSummary,
    PlayerCombat,
    /// <summary>Player turrets, keyed by their owning entity slot.</summary>
    Turret,
    /// <summary>Persistent factory ruins, including geothermal dark fog pits.</summary>
    Ruin
}

/// <summary>How a pool's ScopeId is derived from the game's astro id.</summary>
internal enum PoolScopeKind : byte
{
    /// <summary>ScopeId is a planet id in 1..<see cref="AuthorityScope.MaxPlanetId"/>.</summary>
    Planet = 0,

    /// <summary>ScopeId is always <see cref="AuthorityScope.Sector"/>: one pool for the whole sector.</summary>
    Sector = 1,

    /// <summary>ScopeId is the object's own sector astro slot, which is greater than <see cref="AuthorityScope.MaxPlanetId"/>.</summary>
    AstroSlot = 2
}

/// <summary>
/// The single place that decides how a game astro id becomes a replication scope.
/// </summary>
internal static class AuthorityScope
{
    /// <summary>ScopeId of every pool shared by the whole sector.</summary>
    public const int Sector = 0;

    /// <summary>
    /// Highest planet astro id. Astro ids above this are sector slots (hives and dynamic astros),
    /// per <c>SpaceSector.IsSectorAstro</c>.
    /// </summary>
    public const int MaxPlanetId = 1000000;

    /// <summary>True when the astro id names a sector slot rather than a planet.</summary>
    public static bool IsSectorAstro(int astroId) => astroId > MaxPlanetId;

    /// <summary>Resolves the scope rule for a pool kind.</summary>
    public static bool TryGetScopeKind(PoolKind kind, out PoolScopeKind scopeKind)
    {
        switch (kind)
        {
            case PoolKind.SpaceEnemy:
            case PoolKind.SpaceCraft:
            case PoolKind.HiveSummary:
            case PoolKind.PlayerCombat:
                scopeKind = PoolScopeKind.Sector;
                return true;
            case PoolKind.Hive:
                scopeKind = PoolScopeKind.AstroSlot;
                return true;
            case PoolKind.Entity:
            case PoolKind.Prebuild:
            case PoolKind.GroundEnemy:
            case PoolKind.GroundCraft:
            case PoolKind.DroneTask:
            case PoolKind.Base:
            case PoolKind.Vegetable:
            case PoolKind.Vein:
            case PoolKind.Turret:
            case PoolKind.Ruin:
                scopeKind = PoolScopeKind.Planet;
                return true;
            default:
                scopeKind = default;
                return false;
        }
    }

    /// <summary>
    /// Turns a game astro id into the scope id for a pool kind.
    /// </summary>
    /// <remarks>
    /// Sector pools ignore the astro id instead of normalizing it, because their hive/star and
    /// current astro are attributes of the object, not part of its identity. A ground
    /// pool given a sector astro id is rejected rather than normalized to 0: silently mapping it to
    /// the sector scope would merge unrelated planets into one scope.
    /// </remarks>
    public static bool TryResolveScope(PoolKind kind, int astroId, out int scope)
    {
        scope = Sector;
        if (!TryGetScopeKind(kind, out var scopeKind)) return false;
        switch (scopeKind)
        {
            case PoolScopeKind.Sector:
                return true;
            case PoolScopeKind.AstroSlot:
                if (!IsSectorAstro(astroId)) return false;
                scope = astroId;
                return true;
            default:
                if (astroId <= 0 || IsSectorAstro(astroId)) return false;
                scope = astroId;
                return true;
        }
    }

    /// <summary>True when <paramref name="scope"/> is a legal scope id for <paramref name="kind"/>.</summary>
    public static bool IsValidScope(PoolKind kind, int scope)
    {
        if (!TryGetScopeKind(kind, out var scopeKind)) return false;
        switch (scopeKind)
        {
            case PoolScopeKind.Sector:
                return scope == Sector;
            case PoolScopeKind.AstroSlot:
                return IsSectorAstro(scope);
            default:
                return scope > 0 && !IsSectorAstro(scope);
        }
    }
}

/// <summary>
/// The identity of one replicated object: <c>AuthorityEpoch + PoolKind + ScopeId + NativeId + Generation</c>.
/// </summary>
/// <remarks>
/// <para>
/// The type is deliberately a pure value: it holds no game or Unity reference, so it can be built,
/// compared and fuzzed in a plain.NET test process. The adapter layer converts between this key and
/// the vanilla pool slots.
/// </para>
/// <para>
/// The public constructor takes an already-resolved scope id and does not validate it, which is what
/// the wire decoding path needs. Call <see cref="IsValid"/> after decoding, or use
/// <see cref="TryCreate"/> / <see cref="Create"/> when starting from a raw game astro id.
/// </para>
/// </remarks>
internal readonly struct ObjectKey : IEquatable<ObjectKey>
{
    private readonly AuthorityEpoch epoch;
    private readonly PoolKind kind;
    private readonly int scope;
    private readonly int nativeId;
    private readonly long generation;

    public ObjectKey(AuthorityEpoch epoch, PoolKind kind, int scope, int nativeId, long generation)
    {
        this.epoch = epoch;
        this.kind = kind;
        this.scope = scope;
        this.nativeId = nativeId;
        this.generation = generation;
    }

    /// <summary>Session identity of the world this object belongs to.</summary>
    public AuthorityEpoch Epoch => epoch;

    /// <summary>Which pool <see cref="NativeId"/> indexes.</summary>
    public PoolKind Kind => kind;

    /// <summary>Planet id, sector 0, or the object's own sector astro slot, depending on <see cref="Kind"/>.</summary>
    public int Scope => scope;

    /// <summary>Host pool slot. The client rebinds its own slots, so this is host-relative.</summary>
    public int NativeId => nativeId;

    /// <summary>Distinguishes successive objects that reuse the same host slot.</summary>
    public long Generation => generation;

    /// <summary>
    /// True only for a fully specified key. A default-constructed key is invalid, and so is one
    /// whose scope does not match its pool kind, which is how a decoded message with a bad scope is
    /// rejected before it can touch the replica.
    /// </summary>
    public bool IsValid =>
        epoch.IsValid && nativeId > 0 && generation > 0 && AuthorityScope.IsValidScope(kind, scope);

    /// <summary>
    /// Builds a key from a raw game astro id, resolving the scope from the pool kind.
    /// </summary>
    public static bool TryCreate(AuthorityEpoch epoch, PoolKind kind, int astroId, int nativeId,
        long generation, out ObjectKey key)
    {
        key = default;
        if (nativeId <= 0 || generation <= 0 || !epoch.IsValid) return false;
        if (!AuthorityScope.TryResolveScope(kind, astroId, out var scope)) return false;
        key = new ObjectKey(epoch, kind, scope, nativeId, generation);
        return true;
    }

    /// <summary>
    /// <see cref="TryCreate"/> for call sites that have already checked their inputs, so an invalid
    /// key is a programming error rather than untrusted input.
    /// </summary>
    public static ObjectKey Create(AuthorityEpoch epoch, PoolKind kind, int astroId, int nativeId,
        long generation)
    {
        if (!TryCreate(epoch, kind, astroId, nativeId, generation, out var key))
        {
            throw new ArgumentException(
                $"Invalid ObjectKey: epoch={epoch}, kind={kind}, astroId={astroId}, nativeId={nativeId}, generation={generation}");
        }
        return key;
    }

    public bool Equals(ObjectKey other) =>
        epoch.Equals(other.epoch) && kind == other.kind && scope == other.scope &&
        nativeId == other.nativeId && generation == other.generation;

    public override bool Equals(object obj) => obj is ObjectKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = epoch.GetHashCode();
            hash = (hash * 397) ^ (int)kind;
            hash = (hash * 397) ^ scope;
            hash = (hash * 397) ^ nativeId;
            hash = (hash * 397) ^ generation.GetHashCode();
            return hash;
        }
    }

    public static bool operator ==(ObjectKey left, ObjectKey right) => left.Equals(right);

    public static bool operator !=(ObjectKey left, ObjectKey right) => !left.Equals(right);

    /// <summary>Formats the object identity for diagnostic logs.</summary>
    public override string ToString() =>
        "epoch=" + epoch + "|kind=" + kind + "|scope=" + scope + "|native=" + nativeId +
        "|gen=" + generation;
}
