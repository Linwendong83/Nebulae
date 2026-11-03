#region

using System;
using NebulaeModel.Logger;

#endregion

namespace NebulaeWorld.Factory;

/// <summary>Finishes building deaths whose component pair was already freed.</summary>
/// <remarks>
/// <para>
/// A battle base can outlive its storage or construction module when a native death is interrupted
/// half-way (our death replication, an interrupted commit, or a save taken between the two steps).
/// The entity then keeps its negative-HP combat stat, so the combat system retries the death every
/// tick and throws inside the native teardown each time: the planet never settles, the ground enemy
/// task reports a NullReferenceException, and every joining peer keeps resyncing the same scope.
/// </para>
/// <para>
/// A zero-HP building was already meant to be removed, so removal is the honest repair. A living
/// battle base only loses its stored items instead: its storage is recreated empty and relinked.
/// Nothing here pauses the world or changes another entity's state.
/// </para>
/// </remarks>
internal static class FactoryRepair
{
    /// <summary>Repairs one factory; returns how many entities were removed or rebuilt.</summary>
    public static int RepairBattleBaseEntities(PlanetFactory factory, string source)
    {
        if (factory?.entityPool == null || factory.planet == null) return 0;
        var repaired = 0;
        var cursor = Math.Min(factory.entityCursor, factory.entityPool.Length);
        for (var id = 1; id < cursor; id++)
        {
            ref var entity = ref factory.entityPool[id];
            if (entity.id != id || entity.battleBaseId <= 0) continue;
            var reason = InvalidComponent(factory, ref entity);
            if (reason == null) continue;
            if (IsDead(factory, entity.combatStatId))
            {
                if (Remove(factory, id, reason, source)) repaired++;
                continue;
            }
            if (reason == "storage" && RestoreStorage(factory, id, source)) repaired++;
        }
        return repaired;
    }

    /// <summary>Removes one entity whose death can no longer run natively.</summary>
    public static bool RemoveCorruptEntity(PlanetFactory factory, int id, string source)
    {
        if (factory?.entityPool == null || id <= 0 || id >= factory.entityPool.Length) return false;
        if (factory.entityPool[id].id != id) return true;
        return Remove(factory, id, "death", source);
    }

    /// <summary>True when the entity carries a battle base whose components cannot tick.</summary>
    public static bool IsCorruptBattleBase(PlanetFactory factory, int id)
    {
        if (factory?.entityPool == null || id <= 0 || id >= factory.entityPool.Length) return false;
        ref var entity = ref factory.entityPool[id];
        if (entity.id != id || entity.battleBaseId <= 0) return false;
        return InvalidComponent(factory, ref entity) != null;
    }

    /// <summary>False when the module's battle base or its storage is gone; the tick must not run.</summary>
    public static bool CanTick(CombatModuleComponent module, PlanetFactory factory)
    {
        if (module == null || module.entityId <= 0) return true;
        var bases = factory?.defenseSystem?.battleBases;
        if (bases?.buffer == null || module.battleBaseId <= 0 || module.battleBaseId >= bases.buffer.Length) return false;
        var battleBase = bases.buffer[module.battleBaseId];
        return battleBase != null && battleBase.id == module.battleBaseId && battleBase.storage != null;
    }

    private static string InvalidComponent(PlanetFactory factory, ref EntityData entity)
    {
        var bases = factory.defenseSystem?.battleBases;
        if (bases?.buffer == null || entity.battleBaseId <= 0 || entity.battleBaseId >= bases.buffer.Length) return "battle-base";
        var battleBase = bases.buffer[entity.battleBaseId];
        if (battleBase == null || battleBase.id != entity.battleBaseId) return "battle-base-slot";
        var storagePool = factory.factoryStorage?.storagePool;
        if (entity.storageId <= 0 || storagePool == null || entity.storageId >= storagePool.Length ||
            storagePool[entity.storageId] == null) return "storage";
        if (battleBase.storage == null) return "battle-base-storage";
        var combatModules = factory.combatGroundSystem?.combatModules;
        if (entity.combatModuleId <= 0 || combatModules?.buffer == null ||
            entity.combatModuleId >= combatModules.buffer.Length || combatModules.buffer[entity.combatModuleId] == null) return "combat-module";
        var constructionModules = factory.constructionSystem?.constructionModules;
        if (entity.constructionModuleId <= 0 || constructionModules?.buffer == null ||
            entity.constructionModuleId >= constructionModules.buffer.Length ||
            constructionModules.buffer[entity.constructionModuleId] == null) return "construction-module";
        return null;
    }

    private static bool IsDead(PlanetFactory factory, int statId)
    {
        var stats = factory.skillSystem?.combatStats;
        if (stats?.buffer == null || statId <= 0 || statId >= stats.buffer.Length) return true;
        var stat = stats.buffer[statId];
        return stat.id != statId || stat.hp <= 0;
    }

    private static bool Remove(PlanetFactory factory, int id, string reason, string source)
    {
        try
        {
            factory.BeforeKillObject(id);
            factory.RemoveEntityWithComponents(id, false);
            Log.Warn($"[factory] removed a corrupt battle base id={id} planet={factory.planetId} reason={reason} source={source}");
            return true;
        }
        catch (Exception error)
        {
            Diagnostics.Warn($"factory:repair:{factory.planetId}:{id}", () => $"[factory] could not remove corrupt battle base id={id} planet={factory.planetId} reason={reason} source={source}", error);
            return false;
        }
    }

    private static bool RestoreStorage(PlanetFactory factory, int id, string source)
    {
        try
        {
            var modelIndex = factory.entityPool[id].modelIndex;
            var desc = modelIndex > 0 && modelIndex < LDB.models.modelArray.Length ? LDB.models.modelArray[modelIndex]?.prefabDesc : null;
            if (desc == null) return false;
            var size = Math.Max(1, desc.storageCol * desc.storageRow);
            var storage = factory.factoryStorage.NewStorageComponent(id, size);
            storage.InitConn();
            var bases = factory.defenseSystem.battleBases;
            var battleBase = bases.buffer[factory.entityPool[id].battleBaseId];
            battleBase.storageId = storage.id;
            battleBase.storage = storage;
            battleBase.storageStartTakeFrom = null;
            Log.Warn($"[factory] recreated the storage component of battle base id={id} planet={factory.planetId} source={source}");
            return true;
        }
        catch (Exception error)
        {
            Diagnostics.Warn($"factory:repair-storage:{factory.planetId}:{id}", () => $"[factory] could not recreate the storage of battle base id={id} planet={factory.planetId} source={source}", error);
            return false;
        }
    }
}
