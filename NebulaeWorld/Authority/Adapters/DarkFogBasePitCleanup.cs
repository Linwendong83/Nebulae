#region

using System;

#endregion

namespace NebulaeWorld.Authority.Adapters;

/// <summary>Releases formation ports that a destroyed base can never deploy or re-absorb.</summary>
/// <remarks>
/// <para>
/// A base pit keeps the formation bookkeeping of the base it used to be. The engine clears tags it
/// considers stale (unit values above 1) in <c>EnemyDFGroundSystem.PostKeyTick</c>, but that routine
/// starts with <c>if (!isLocalLoaded) return;</c> — and <c>isLocalLoaded</c> means "this is the
/// planet the local player stands on", which a dedicated server never has. A client is told not to
/// run it either, because its unit changes wait for the server. The tags therefore survive on both
/// sides forever.
/// </para>
/// <para>
/// <c>CheckBaseCanRemoved</c> counts every occupied formation port as a remaining unit, so such a
/// pit reports "units remain" while holding nothing: its removal button stays disabled and a
/// geothermal station aimed at the pit is refused with the generic collision message, for every
/// player. Pruning is only ever applied to a pit whose own troops are gone, so a live garrison (or
/// a base that is still fighting) keeps its verdict.
/// </para>
/// </remarks>
internal static class DarkFogBasePitCleanup
{
    [ThreadStatic] private static byte[] liveOwners;

    /// <summary>Clears the formation tags of pits that own no living unit. True when anything changed.</summary>
    public static bool Prune(EnemyDFGroundSystem system)
    {
        var bases = system?.bases;
        var builders = system?.builders.buffer;
        if (bases?.buffer == null || builders == null) return false;
        var pruned = false;
        byte[] owners = null;
        for (var id = 1; id < bases.cursor; id++)
        {
            var item = bases.buffer[id];
            if (item?.id != id || item.forms == null) continue;
            // An intact base may hold a garrison it is about to deploy: never touch it. The pit link is
            // the second half of the same statement — a base whose core is gone but that has no pit
            // ruin yet is mid-teardown, not a pit whose leftovers may be released.
            if (item.builderId > 0 && item.builderId < builders.Length && builders[item.builderId].sp != 0) continue;
            if (item.ruinId <= 0) continue;
            if (!HasTags(item)) continue;
            owners ??= LiveOwners(system.factory, bases.capacity);
            // A unit of this base is still on the field: the pit is not empty, whatever its tags say.
            if (id < owners.Length && owners[id] != 0) continue;
            for (var form = 0; form < item.forms.Length; form++) item.forms[form]?.Clear();
            pruned = true;
        }
        return pruned;
    }

    private static bool HasTags(DFGBaseComponent item)
    {
        for (var form = 0; form < item.forms.Length; form++)
        {
            var units = item.forms[form]?.units;
            if (units == null) continue;
            for (var port = 1; port < units.Length; port++) if (units[port] != 0) return true;
        }
        return false;
    }

    /// <summary>One pool pass over the planet's enemies, indexed by owning base.</summary>
    private static byte[] LiveOwners(PlanetFactory factory, int capacity)
    {
        var owners = liveOwners;
        if (owners == null || owners.Length < Math.Max(capacity, factory.enemyPool.Length))
            liveOwners = owners = new byte[Math.Max(capacity, factory.enemyPool.Length)];
        Array.Clear(owners, 0, owners.Length);
        var pool = factory.enemyPool;
        for (var id = 1; id < factory.enemyCursor && id < pool.Length; id++)
        {
            ref var enemy = ref pool[id];
            // Core death retains an invincible shell owned by this base until the pit is filled.
            // Buildings have their own removal bit; only mobile troops can keep formations alive.
            if (enemy.id != id || (enemy.unitId <= 0 && !enemy.dynamic) || enemy.owner <= 0 || enemy.owner >= owners.Length) continue;
            owners[enemy.owner] = 1;
        }
        return owners;
    }
}
