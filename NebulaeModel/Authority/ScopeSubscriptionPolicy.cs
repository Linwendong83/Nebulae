using System.Collections.Generic;

namespace NebulaeModel.Authority;

internal enum ScopeSubscriptionDecision : byte
{
    Allowed = 0,
    Refused = 2
}

/// <summary>Bounds host delivery and builds the client's standing subscription set.</summary>
internal static class ScopeSubscriptionPolicy
{
    /// <summary>The host's enforcement of a client's subscribe request, from host-owned facts.</summary>
    /// <remarks>
    /// Offline players cannot subscribe. Planet pools require the planet accepted by the host for that player.
    /// Sector and hive scopes are allowed for every online player.
    /// </remarks>
    public static ScopeSubscriptionDecision MaySubscribe(in ScopeKey scope, bool subscriberOnline,
        int subscriberPlanetId)
    {
        if (!scope.IsValid || !subscriberOnline) return ScopeSubscriptionDecision.Refused;
        if (!AuthorityScope.TryGetScopeKind(scope.Kind, out var scopeKind))
        {
            return ScopeSubscriptionDecision.Refused;
        }
        if (scopeKind == PoolScopeKind.Planet && scope.Scope != subscriberPlanetId)
        {
            return ScopeSubscriptionDecision.Refused;
        }
        return ScopeSubscriptionDecision.Allowed;
    }

    /// <summary>
    /// Fills <paramref name="desired"/> with the scopes a subscriber should hold at full depth.
    /// </summary>
    /// <remarks>
    /// Digest-only scopes are observation-driven and not part of the standing set: a star map names
    /// them per frame it is open. The standing set includes the current planet's pools and the sector
    /// pools. Hive scopes are joined on demand when the player engages one.
    /// </remarks>
    public static void DesiredScopes(int currentPlanetId, List<ScopeKey> desired)
    {
        if (desired == null) return;
        desired.Clear();

        if (currentPlanetId > 0 && !AuthorityScope.IsSectorAstro(currentPlanetId))
        {
            desired.Add(new ScopeKey(PoolKind.Entity, currentPlanetId));
            desired.Add(new ScopeKey(PoolKind.Turret, currentPlanetId));
            desired.Add(new ScopeKey(PoolKind.Prebuild, currentPlanetId));
            desired.Add(new ScopeKey(PoolKind.GroundEnemy, currentPlanetId));
            desired.Add(new ScopeKey(PoolKind.GroundCraft, currentPlanetId));
            desired.Add(new ScopeKey(PoolKind.Vegetable, currentPlanetId));
            desired.Add(new ScopeKey(PoolKind.Vein, currentPlanetId));
            desired.Add(new ScopeKey(PoolKind.Base, currentPlanetId));
            desired.Add(new ScopeKey(PoolKind.Ruin, currentPlanetId));
            desired.Add(new ScopeKey(PoolKind.DroneTask, currentPlanetId));
        }
        // Orbital attacks and hive status remain observable while the player is on a planet.
        desired.Add(new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector));
        desired.Add(new ScopeKey(PoolKind.SpaceCraft, AuthorityScope.Sector));
        desired.Add(new ScopeKey(PoolKind.HiveSummary, AuthorityScope.Sector));
    }
}
