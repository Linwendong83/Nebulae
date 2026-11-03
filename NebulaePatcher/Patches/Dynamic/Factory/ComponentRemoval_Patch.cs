#region

using System;
using HarmonyLib;
using NebulaeModel.Logger;

#endregion

namespace NebulaePatcher.Patches.Dynamic.Factory;

// A building whose death was interrupted (crash save, or a host that lost the planet mid-commit)
// keeps its component ids while the component slots are already freed. Vanilla removes components
// with an unconditional "buffer[id].Free()", which then throws every frame the death is retried.
// Removal is idempotent: a slot that is already empty needs no work, so skip it instead of
// crashing and re-running the whole teardown.
[HarmonyPatch(typeof(ConstructionSystem), nameof(ConstructionSystem.RemoveConstructionModuleComponent))]
internal static class ConstructionModuleRemoval_Patch
{
    [HarmonyPrefix]
    private static bool Prefix(ConstructionSystem __instance, int id)
    {
        if (id <= 0 || __instance.constructionModules == null) return true;
        var modules = __instance.constructionModules;
        if (id >= modules.cursor || id >= modules.buffer.Length) return true;
        var module = modules.buffer[id];
        if (module != null && module.id == id) return true;
        Log.Warn($"[factory] construction module id={id} was already removed; skipping the second removal.");
        return false;
    }
}

[HarmonyPatch(typeof(CombatGroundSystem), nameof(CombatGroundSystem.RemoveCombatModuleComponent))]
internal static class CombatModuleRemoval_Patch
{
    [HarmonyPrefix]
    private static bool Prefix(CombatGroundSystem __instance, int id)
    {
        if (id <= 0 || __instance.combatModules == null) return true;
        var modules = __instance.combatModules;
        if (id >= modules.cursor || id >= modules.buffer.Length) return true;
        var module = modules.buffer[id];
        if (module != null && module.id == id) return true;
        Log.Warn($"[factory] combat module id={id} was already removed; skipping the second removal.");
        return false;
    }
}

// The teardown of a combat module kills every fighter it ever launched, by walking its fleet table
// and dereferencing craft, stat and module slots directly. A client whose combat replica is behind
// can still list a fighter whose craft was already released, and vanilla then throws in the middle
// of a building death - which is what left the factory half-mutated and diverged for good
// (2026-11-03: planet 6301, KillCraftFinally NullReferenceException under KillAllCraftsDirectly).
// The guard only takes over when it finds such a dead reference; an intact module still runs the
// untouched vanilla path.
[HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.KillAllCraftsDirectly))]
internal static class CombatModuleKillAll_Patch
{
    [HarmonyPrefix]
    private static bool Prefix(CombatModuleComponent __instance) =>
        !CraftKillGuard.TryReplaceKillAll(__instance);
}

// A second death of an already released craft has nothing to tear down, and re-running the vanilla
// body would index a freed slot.
[HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.KillCraftFinally))]
internal static class PlanetCraftKill_Patch
{
    [HarmonyPrefix]
    private static bool Prefix(PlanetFactory __instance, int craftId)
    {
        if (craftId <= 0) return true;
        if (CraftKillGuard.CraftAlive(__instance?.craftPool, craftId)) return true;
        CraftKillGuard.WarnStaleCraft("planet", __instance?.planetId ?? 0, craftId);
        return false;
    }
}

[HarmonyPatch(typeof(SpaceSector), nameof(SpaceSector.KillCraftFinal))]
internal static class SpaceCraftKill_Patch
{
    [HarmonyPrefix]
    private static bool Prefix(SpaceSector __instance, int craftId)
    {
        if (craftId <= 0) return true;
        if (CraftKillGuard.CraftAlive(__instance?.craftPool, craftId)) return true;
        CraftKillGuard.WarnStaleCraft("space", 0, craftId);
        return false;
    }
}

internal static class CraftKillGuard
{
    internal static bool CraftAlive(CraftData[] pool, int craftId) =>
        pool != null && craftId > 0 && craftId < pool.Length && pool[craftId].id == craftId;

    // Slot 0 of a stat pool is the unused placeholder, and vanilla passes it as a throwaway ref.
    private static bool StatAlive(DataPool<CombatStat> stats, int statId) =>
        stats?.buffer != null && statId >= 0 && statId < stats.buffer.Length &&
        (statId == 0 || stats.buffer[statId].ID == statId);

    internal static void WarnStaleCraft(string kind, int planetId, int craftId) =>
        Diagnostics.Warn("factory:craft-kill:" + kind + ":" + planetId,
            () => $"[factory] {kind} craft {craftId} on planet {planetId} was already released; skipping the kill.");

    private static PlanetFactory AstroFactory(PlanetFactory[] astros, int astroId) =>
        astros != null && astroId >= 0 && astroId < astros.Length ? astros[astroId] : null;

    private static bool FleetAlive(PlanetFactory factory, int fleetId)
    {
        var fleets = factory?.combatGroundSystem?.fleets;
        return fleets?.buffer != null && fleetId > 0 && fleetId < fleets.buffer.Length &&
               fleets.buffer[fleetId].id == fleetId;
    }

    private static int FleetCraftId(PlanetFactory factory, int fleetId) =>
        FleetAlive(factory, fleetId) ? factory.combatGroundSystem.fleets.buffer[fleetId].craftId : 0;

    /// <summary>
    ///     Runs the guarded teardown when the vanilla loop would follow a dead reference.
    /// </summary>
    /// <returns><c>true</c> when the module was torn down here; <c>false</c> to let vanilla run.</returns>
    internal static bool TryReplaceKillAll(CombatModuleComponent module)
    {
        if (module == null || module.isSpace) return false;
        var fleets = module.moduleFleets;
        if (fleets == null) return true; // Vanilla reads fleet.fighters.Length and would throw.
        var astros = module.sector?.galaxy?.astrosFactory;
        if (astros == null) return true;
        var count = Math.Min(module.fleetCount, fleets.Length);
        if (!AnyStaleReference(fleets, count, astros)) return false;
        KillAll(module, fleets, count, astros);
        return true;
    }

    private static bool AnyStaleReference(ModuleFleet[] fleets, int count, PlanetFactory[] astros)
    {
        for (var i = 0; i < count; i++)
        {
            ref var fleet = ref fleets[i];
            if (fleet.fleetId == 0) break;
            var factory = AstroFactory(astros, fleet.fleetAstroId);
            if (factory == null) break;
            var crafts = factory.craftPool;
            var stats = factory.skillSystem?.combatStats;
            if (fleet.fighters == null) return true;
            for (var j = 0; j < fleet.fighters.Length; j++)
            {
                var craftId = fleet.fighters[j].craftId;
                if (craftId <= 0) continue;
                if (!CraftAlive(crafts, craftId)) return true;
                if (!StatAlive(stats, crafts[craftId].combatStatId)) return true;
            }
            if (!FleetAlive(factory, fleet.fleetId)) return true;
            var leaderCraft = FleetCraftId(factory, fleet.fleetId);
            if (leaderCraft > 0 && !CraftAlive(crafts, leaderCraft)) return true;
            if (leaderCraft > 0 && !StatAlive(stats, crafts[leaderCraft].combatStatId)) return true;
        }
        return false;
    }

    private static void KillAll(CombatModuleComponent module, ModuleFleet[] fleets, int count, PlanetFactory[] astros)
    {
        for (var i = 0; i < count; i++)
        {
            ref var fleet = ref fleets[i];
            if (fleet.fleetId == 0) break;
            var factory = AstroFactory(astros, fleet.fleetAstroId);
            if (factory == null) break;
            var crafts = factory.craftPool;
            var stats = factory.skillSystem?.combatStats;
            if (fleet.fighters != null)
            {
                for (var j = 0; j < fleet.fighters.Length; j++)
                {
                    var craftId = fleet.fighters[j].craftId;
                    if (craftId <= 0) continue;
                    if (!CraftAlive(crafts, craftId))
                    {
                        // The fighter is already gone; keeping its id would make the next teardown
                        // follow it again.
                        Diagnostics.Warn("factory:module-fighter:" + module.id,
                            () => $"[factory] combat module {module.id} lists a fighter whose craft {craftId} is gone; dropping the reference.");
                        fleet.fighters[j].ClearFighterForeignKey();
                        continue;
                    }
                    KillCraft(factory, crafts, stats, craftId);
                }
            }
            var leaderCraft = FleetCraftId(factory, fleet.fleetId);
            if (leaderCraft > 0) KillCraft(factory, crafts, stats, leaderCraft);
            fleet.OnFleetComponentRemoved();
        }
    }

    private static void KillCraft(PlanetFactory factory, CraftData[] crafts, DataPool<CombatStat> stats, int craftId)
    {
        if (!CraftAlive(crafts, craftId)) return;
        if (stats?.buffer == null || stats.buffer.Length == 0)
        {
            // No skill pool to hand over: remove the craft without the death side effects.
            factory.RemoveCraftWithComponents(craftId);
            return;
        }
        var statId = crafts[craftId].combatStatId;
        if (!StatAlive(stats, statId))
        {
            // Vanilla would index this id in the caller, so point the craft at the throwaway slot.
            Diagnostics.Warn("factory:craft-stat:" + factory.planetId,
                () => $"[factory] craft {craftId} on planet {factory.planetId} references a released combat stat {statId}; the death uses the throwaway slot.");
            crafts[craftId].combatStatId = 0;
            statId = 0;
        }
        factory.KillCraftFinally(craftId, ref stats.buffer[statId]);
    }
}
