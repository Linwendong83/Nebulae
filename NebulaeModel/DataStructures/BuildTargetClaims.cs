using System;
using System.Collections.Generic;

namespace NebulaeModel.DataStructures;

internal enum BuildOwnerKind : byte
{
    None,
    Player,
    Base
}

internal static class BuildCandidateScore
{
    // Matches ConstructionModuleComponent.PreLaunchDrone: bases receive a 1.2 multiplier.
    public static float Calculate(BuildOwnerKind kind, float squaredDistance, float range)
    {
        if (kind == BuildOwnerKind.None || float.IsNaN(squaredDistance) || float.IsInfinity(squaredDistance) ||
            float.IsNaN(range) || float.IsInfinity(range) || range < 0f || squaredDistance < 0f ||
            squaredDistance > range * range)
            return 0f;
        return (kind == BuildOwnerKind.Base ? 1.2f : 1f) / Math.Max(squaredDistance, 1f);
    }
}

internal readonly struct BuildTargetClaim
{
    public BuildTargetClaim(int planetId, int prebuildId, long generation, BuildOwnerKind ownerKind, int ownerId,
        bool launched = false)
    {
        PlanetId = planetId;
        PrebuildId = prebuildId;
        Generation = generation;
        OwnerKind = ownerKind;
        OwnerId = ownerId;
        Launched = launched;
    }

    public int PlanetId { get; }
    public int PrebuildId { get; }
    public long Generation { get; }
    public BuildOwnerKind OwnerKind { get; }
    public int OwnerId { get; }
    public bool Launched { get; }

    public BuildTargetClaim WithLaunched() =>
        new(PlanetId, PrebuildId, Generation, OwnerKind, OwnerId, true);
}

/// <summary>One authoritative owner per prebuild. Generations survive ID reuse within a session.</summary>
internal sealed class BuildTargetClaims
{
    private readonly Dictionary<(int PlanetId, int PrebuildId), BuildTargetClaim> claims = new();
    private readonly Dictionary<(int PlanetId, int PrebuildId), long> generations = new();

    public IEnumerable<BuildTargetClaim> All => claims.Values;

    public bool TryGet(int planetId, int prebuildId, out BuildTargetClaim claim) =>
        claims.TryGetValue((planetId, prebuildId), out claim);

    public bool ValidateLaunch(int planetId, IReadOnlyList<int> objectIds, IReadOnlyList<long> targetGenerations,
        BuildOwnerKind kind, int ownerId, bool requireUnlaunched)
    {
        if (objectIds == null || targetGenerations == null || objectIds.Count != 4 ||
            targetGenerations.Count != 4 || objectIds[0] >= 0) return false;
        var seen = new HashSet<int>();
        for (var i = 0; i < 4; i++)
        {
            var objectId = objectIds[i];
            if (objectId == 0)
            {
                if (targetGenerations[i] != 0) return false;
                continue;
            }
            if (objectId > 0 || !seen.Add(objectId) ||
                !claims.TryGetValue((planetId, -objectId), out var claim) ||
                claim.Generation != targetGenerations[i] || claim.OwnerKind != kind ||
                claim.OwnerId != ownerId || requireUnlaunched && claim.Launched) return false;
        }
        return true;
    }

    public BuildTargetClaim Assign(int planetId, int prebuildId, BuildOwnerKind ownerKind, int ownerId)
    {
        var key = (planetId, prebuildId);
        generations.TryGetValue(key, out var generation);
        var claim = new BuildTargetClaim(planetId, prebuildId, ++generation, ownerKind, ownerId);
        generations[key] = generation;
        claims[key] = claim;
        return claim;
    }

    public bool Apply(BuildTargetClaim claim)
    {
        var key = (claim.PlanetId, claim.PrebuildId);
        generations.TryGetValue(key, out var generation);
        if (claim.Generation < generation) return false;
        if (claim.Generation == generation && !claims.ContainsKey(key)) return false;
        if (claim.Generation == generation && claims.TryGetValue(key, out var previous) &&
            previous.Launched && !claim.Launched) return false;
        generations[key] = claim.Generation;
        claims[key] = claim;
        return true;
    }

    public bool MarkLaunched(int planetId, int prebuildId, long generation, BuildOwnerKind kind, int ownerId)
    {
        var key = (planetId, prebuildId);
        if (!claims.TryGetValue(key, out var claim) || claim.Generation != generation ||
            claim.OwnerKind != kind || claim.OwnerId != ownerId || claim.Launched) return false;
        claims[key] = claim.WithLaunched();
        return true;
    }

    public bool Remove(int planetId, int prebuildId, long generation)
    {
        var key = (planetId, prebuildId);
        generations.TryGetValue(key, out var current);
        if (generation < current) return false;
        generations[key] = generation;
        return claims.Remove(key);
    }

    public void ClearPlanet(int planetId)
    {
        var keys = new List<(int, int)>();
        foreach (var key in claims.Keys)
            if (key.PlanetId == planetId) keys.Add(key);
        foreach (var key in keys) claims.Remove(key);
        keys.Clear();
        foreach (var key in generations.Keys)
            if (key.PlanetId == planetId) keys.Add(key);
        foreach (var key in keys) generations.Remove(key);
    }

    public void Clear()
    {
        claims.Clear();
        generations.Clear();
    }
}
