#region

using NebulaeModel.Authority;

#endregion

namespace NebulaeWorld.Authority.Adapters;

/// <summary>
/// Resolves a dying vanilla object's authority identity from the domain adapters.
/// </summary>
/// <remarks>
/// <para>
/// Vanilla dispatches every death by <c>originAstroId</c> + <c>objectType</c>
/// (<c>CombatStat.HandleZeroHp</c>). This resolver applies the same dispatch to the identity
/// question: which pool, which scope, and which generation — always the generation the owning
/// adapter's replication scan mints, so the tombstone lands on the key clients were given.
/// </para>
/// <para>
/// A planet astro resolves through its realized factory; anything else is the sector
/// (<c>AuthorityScope.Sector</c>). Vegetable and vein deaths have no key source yet — their pools
/// have no replica adapter, so the capture refuses and counts them rather than minting a key no
/// replication state will ever match. When their adapter lands it must own the same tracker this
/// decision is waiting for.
/// </para>
/// <para>
/// Called from the vanilla rule's thread, including parallel combat workers: every adapter lookup
/// it reaches validates its pool slot before touching the tracker, and the trackers are locked.
/// </para>
/// </remarks>
internal sealed class VanillaDeathKeyResolver
{
    private readonly FactoryCombatSnapshotAdapter entities;
    private readonly GroundEnemySnapshotAdapter groundEnemies;
    private readonly SpaceEnemySnapshotAdapter spaceEnemies;
    private readonly CraftSnapshotAdapter crafts;

    public VanillaDeathKeyResolver(FactoryCombatSnapshotAdapter entities,
        GroundEnemySnapshotAdapter groundEnemies, SpaceEnemySnapshotAdapter spaceEnemies,
        CraftSnapshotAdapter crafts)
    {
        this.entities = entities ?? throw new System.ArgumentNullException(nameof(entities));
        this.groundEnemies = groundEnemies ?? throw new System.ArgumentNullException(nameof(groundEnemies));
        this.spaceEnemies = spaceEnemies ?? throw new System.ArgumentNullException(nameof(spaceEnemies));
        this.crafts = crafts ?? throw new System.ArgumentNullException(nameof(crafts));
    }

    /// <summary>Resolves the key, or false when no adapter can vouch for the object's identity.</summary>
    public bool TryResolveKey(int originAstroId, int objectType, int objectId, out ObjectKey key)
    {
        key = default;
        if (objectId <= 0) return false;
        var factory = GameMain.galaxy?.PlanetById(originAstroId)?.factory;

        switch (objectType)
        {
            case 0: // EObjectType.Entity
                return factory != null && entities.TryGetEntityKey(factory.planetId, objectId, out key);
            case 4: // EObjectType.Enemy — planet pool or sector pool by astro
                return factory != null
                    ? groundEnemies.TryGetEnemyKey(factory.planetId, objectId, out key)
                    : spaceEnemies.TryGetEnemyKey(objectId, out key);
            case 6: // EObjectType.Craft — planet pool or sector pool by astro
                return factory != null
                    ? crafts.TryGetGroundCraftKey(factory.planetId, objectId, out key)
                    : crafts.TryGetSpaceCraftKey(objectId, out key);
            default:
                // Vegetable (1) and Vein (2) have no key source yet; Prebuild/Ruin/None are not
                // damage targets. Counted by the capture as unresolved, never guessed.
                return false;
        }
    }
}
