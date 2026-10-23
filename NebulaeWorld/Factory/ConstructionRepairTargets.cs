using System;

namespace NebulaeWorld.Factory;

/// <summary>Repair queues contain local stat slots, so they must follow the local pool lifetime.</summary>
internal static class ConstructionRepairTargets
{
    internal static bool Valid(PlanetFactory factory, int id)
    {
        var stats = factory?.constructionSystem?.constructStats;
        if (stats?.buffer == null || id <= 0 || id >= stats.cursor || id >= stats.buffer.Length ||
            stats.buffer[id].id != id) return false;
        var entity = stats.buffer[id].entityId;
        if (entity <= 0 || entity >= factory.entityCursor || factory.entityPool[entity].id != entity ||
            factory.entityPool[entity].constructStatId != id) return false;
        var combatId = factory.entityPool[entity].combatStatId;
        var combat = factory.skillSystem?.combatStats;
        return combat?.buffer != null && combatId > 0 && combatId < combat.cursor &&
            combatId < combat.buffer.Length && combat.buffer[combatId].id == combatId &&
            combat.buffer[combatId].objectType == (int)EObjectType.Entity &&
            combat.buffer[combatId].objectId == entity && combat.buffer[combatId].originAstroId == factory.planetId;
    }

    internal static void Prune(ConstructionModuleComponent module, PlanetFactory factory, int removed = 0, bool clear = false)
    {
        if (module?.repairTargets == null) return;
        var targets = module.repairTargets;
        var write = 0;
        var changed = false;
        for (var read = 0; read < targets.Length; read++)
        {
            var id = targets[read].objectId;
            if (id == 0) continue;
            if (clear || id == removed || !Valid(factory, id)) { changed = true; continue; }
            targets[write++] = targets[read];
        }
        Array.Clear(targets, write, targets.Length - write);
        if (changed) module.repairTargetTotalCount = write + 1; // Request a new native search.
    }

    internal static void Invalidate(PlanetFactory factory, int removed = 0, bool clear = false)
    {
        if (factory?.constructionSystem == null) return;
        var modules = factory.constructionSystem.constructionModules;
        if (modules?.buffer != null)
            for (var i = 1; i < modules.cursor; i++)
                if (modules.buffer[i]?.id == i) Prune(modules.buffer[i], factory, removed, clear);
        if (GameMain.mainPlayer?.factory == factory)
            Prune(GameMain.mainPlayer.mecha.constructionModule, factory, removed, clear);
    }

    internal static void ResetImported(PlanetFactory factory)
    {
        if (factory?.constructionSystem?.constructStats == null) return;
        Invalidate(factory, clear: true);
        for (var i = 1; i < factory.entityCursor; i++) factory.entityPool[i].constructStatId = 0;
        factory.constructionSystem.constructStats.Reset();
    }
}
