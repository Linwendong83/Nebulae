#region

using System;
using System.Collections.Generic;

#endregion

namespace NebulaeModel.Authority;

/// <summary>What a subscriber may hold for one scope.</summary>
internal enum ScopeSubscriptionDecision : byte
{
    /// <summary>Full subscription: baseline, stream, digests.</summary>
    Allowed = 0,

    /// <summary>Observation only: digests as a summary, no baseline, no stream.</summary>
    DigestOnly = 1,

    /// <summary>No subscription. The request is refused with a reason, never silently ignored.</summary>
    Refused = 2
}

/// <summary>What one subscriber is currently looking at, from the client's own local view.</summary>
/// <remarks>
/// This is a declaration of observation, not a claim about the world: the client knows which planet
/// it stands on, whether it is in space, and what its star map or UI is watching. The host never
/// trusts it for rules — it only bounds what the subscriber may be sent.
/// </remarks>
internal sealed class ScopeObservationContext
{
    /// <summary>Planet the subscriber is on, or 0 when it is not on a planet.</summary>
    public int CurrentPlanetId;

    /// <summary>True while the subscriber flies in the sector.</summary>
    public bool IsInSector;

    /// <summary>Astros the subscriber's star map or UI currently observes. May hold planet ids and sector astro slots.</summary>
    public IReadOnlyCollection<int> ObservedAstroIds = Array.Empty<int>();

    /// <summary>True when the subscriber wants the sector summary (star map open, remote UI).</summary>
    public bool WantsSectorSummary;
}

/// <summary>
/// The one place that decides which scopes a subscriber may hold, and in which depth.
/// </summary>
/// <remarks>
/// <para>
/// A planet's pools are a full subscription only while the player is on that planet; a
/// watched planet is a digest-only observation; everything else is refused. The sector's shared
/// pools are full while the player flies in space and digest-only for a star-map observer. The host
/// simulates every scope regardless of any of this — subscription never pauses the simulation, it only bounds
/// delivery.
/// </para>
/// <para>
/// Two entry points exist because the two sides check different things. <see cref="Classify"/> is
/// the client-side planning question ("which scopes do I hold, and how deep?") driven by the
/// subscriber's own observation. <see cref="MaySubscribe"/> is the host-side enforcement question
/// driven by facts the host holds itself (registry presence, the planet the host accepted for that
/// player); a star-map observation is client knowledge, so the host cannot demand it and only
/// enforces the hard planet rule.
/// </para>
/// </remarks>
internal static class ScopeSubscriptionPolicy
{
    /// <summary>
    /// Classifies the subscription depth one scope may have for an observing subscriber.
    /// </summary>
    public static ScopeSubscriptionDecision Classify(in ScopeKey scope, ScopeObservationContext context)
    {
        if (!scope.IsValid || context == null) return ScopeSubscriptionDecision.Refused;

        if (!AuthorityScope.TryGetScopeKind(scope.Kind, out var scopeKind))
        {
            return ScopeSubscriptionDecision.Refused;
        }

        switch (scopeKind)
        {
            case PoolScopeKind.Planet:
                if (scope.Scope == context.CurrentPlanetId) return ScopeSubscriptionDecision.Allowed;
                if (Observes(context, scope.Scope)) return ScopeSubscriptionDecision.DigestOnly;
                return ScopeSubscriptionDecision.Refused;
            case PoolScopeKind.Sector:
                if (context.IsInSector) return ScopeSubscriptionDecision.Allowed;
                if (context.WantsSectorSummary) return ScopeSubscriptionDecision.DigestOnly;
                return ScopeSubscriptionDecision.Refused;
            case PoolScopeKind.AstroSlot:
                // A hive is watched like the space it lives in: full while flying there, digest
                // only from a star map that names its astro slot.
                if (context.IsInSector) return ScopeSubscriptionDecision.Allowed;
                if (Observes(context, scope.Scope)) return ScopeSubscriptionDecision.DigestOnly;
                return ScopeSubscriptionDecision.Refused;
            default:
                return ScopeSubscriptionDecision.Refused;
        }
    }

    private static bool Observes(ScopeObservationContext context, int astroId)
    {
        if (context.ObservedAstroIds == null) return false;
        foreach (var observed in context.ObservedAstroIds)
        {
            if (observed == astroId) return true;
        }
        return false;
    }

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
    /// them per frame it is open. The standing set is where the subscriber lives — its planet's
    /// pools, and the sector pools while it flies in space. Hive scopes are joined on demand when
    /// the player engages one, which is a combat decision, not a location fact.
    /// </remarks>
    public static void DesiredScopes(ScopeObservationContext context, List<ScopeKey> desired)
    {
        if (context == null || desired == null) return;
        desired.Clear();

        if (context.CurrentPlanetId > 0 && !AuthorityScope.IsSectorAstro(context.CurrentPlanetId))
        {
            desired.Add(new ScopeKey(PoolKind.Entity, context.CurrentPlanetId));
            desired.Add(new ScopeKey(PoolKind.Turret, context.CurrentPlanetId));
            desired.Add(new ScopeKey(PoolKind.Prebuild, context.CurrentPlanetId));
            desired.Add(new ScopeKey(PoolKind.GroundEnemy, context.CurrentPlanetId));
            desired.Add(new ScopeKey(PoolKind.GroundCraft, context.CurrentPlanetId));
            desired.Add(new ScopeKey(PoolKind.Vegetable, context.CurrentPlanetId));
            desired.Add(new ScopeKey(PoolKind.Vein, context.CurrentPlanetId));
            desired.Add(new ScopeKey(PoolKind.Base, context.CurrentPlanetId));
            desired.Add(new ScopeKey(PoolKind.DroneTask, context.CurrentPlanetId));
        }
        // Orbital attacks and hive status remain observable while the player is on a planet.
        desired.Add(new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector));
        desired.Add(new ScopeKey(PoolKind.SpaceCraft, AuthorityScope.Sector));
        desired.Add(new ScopeKey(PoolKind.HiveSummary, AuthorityScope.Sector));
    }
}
