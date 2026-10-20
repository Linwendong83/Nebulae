using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;

namespace NebulaeWorld.GameStates;

/// <summary>Native sector initialization without enemy instances; authority baselines supply combat facts.</summary>
internal static class SpaceSectorBootstrap
{
    private static readonly AccessTools.FieldRef<SpaceSector, int> CraftRecycleCursor = AccessTools.FieldRefAccess<SpaceSector, int>("craftRecycleCursor");
    private static readonly AccessTools.FieldRef<SpaceSector, int[]> CraftRecycle = AccessTools.FieldRefAccess<SpaceSector, int[]>("craftRecycle");
    private static readonly AccessTools.FieldRef<SpaceSector, int> EnemyRecycleCursor = AccessTools.FieldRefAccess<SpaceSector, int>("enemyRecycleCursor");

    public static bool IsImporting { get; private set; }

    public static void Import(SpaceSector sector, BinaryReader reader)
    {
        var previous = IsImporting;
        try
        {
            IsImporting = true;
            ClearGeneratedEnemies(sector);
            sector.Import(reader);
        }
        finally { IsImporting = previous; }
    }

    public static void Export(SpaceSector sector, BinaryWriter writer)
    {
        writer.Write(1); // SpaceSector's native serialization version.
        writer.Write(sector.astros.Length);
        writer.Write(sector.astroCursor);
        for (var id = 1; id < sector.astroCursor; id++)
        {
            ref var astro = ref sector.astros[id];
            writer.Write(astro.id);
            if (astro.id <= 0) continue;
            writer.Write((int)astro.type); writer.Write(astro.parentId);
            writer.Write(astro.uPos.x); writer.Write(astro.uPos.y); writer.Write(astro.uPos.z);
            writer.Write(astro.uPosNext.x); writer.Write(astro.uPosNext.y); writer.Write(astro.uPosNext.z);
            writer.Write(astro.uRot.x); writer.Write(astro.uRot.y); writer.Write(astro.uRot.z); writer.Write(astro.uRot.w);
            writer.Write(astro.uRotNext.x); writer.Write(astro.uRotNext.y); writer.Write(astro.uRotNext.z); writer.Write(astro.uRotNext.w);
            writer.Write(astro.uRadius);
        }

        writer.Write(32); writer.Write(1); writer.Write(0); // Empty enemy pool, recycle list and animation pool.

        // Compact the retained craft HP records instead of exporting holes for every omitted enemy stat.
        var stats = new DataPool<CombatStat>(); stats.Reset();
        var craftRecycleCount = CraftRecycleCursor(sector);
        writer.Write(sector.craftPool.Length); writer.Write(sector.craftCursor); writer.Write(craftRecycleCount);
        for (var id = 1; id < sector.craftCursor; id++)
        {
            var craft = sector.craftPool[id];
            var statId = craft.combatStatId; craft.combatStatId = 0;
            var source = sector.skillSystem.combatStats;
            if (craft.id == id && statId > 0 && statId < source.cursor &&
                source.buffer[statId].id == statId && source.buffer[statId].objectType == (int)EObjectType.Craft &&
                source.buffer[statId].objectId == id && source.buffer[statId].astroId == craft.astroId)
            {
                ref var copy = ref stats.Add();
                var localId = copy.id;
                copy = source.buffer[statId]; copy.id = localId;
                if (copy.lastCaster.type == ETargetType.Enemy) copy.lastCaster = default;
                craft.combatStatId = localId;
            }
            craft.Export(writer);
        }
        var craftRecycle = CraftRecycle(sector);
        for (var id = 0; id < craftRecycleCount; id++) writer.Write(craftRecycle[id]);
        for (var id = 1; id < sector.craftCursor; id++)
        {
            var anim = sector.craftAnimPool[id];
            writer.Write(anim.time); writer.Write(anim.prepare_length); writer.Write(anim.working_length);
            writer.Write(anim.state); writer.Write(anim.power);
        }
        sector.spaceRuins.Export(writer);

        writer.Write(3); // The join SkillSystem payload consumed by SkillSystem_Patch.Import_Prefix.
        stats.Export(writer);
        var removed = new List<SkillTarget>();
        foreach (var target in sector.skillSystem.removedSkillTargets)
            if (target.type != ETargetType.Enemy) removed.Add(target);
        writer.Write(removed.Count);
        foreach (var target in removed)
        { writer.Write(target.id); writer.Write(target.astroId); writer.Write((int)target.type); }

        writer.Write(sector.galaxy.starCount);
        for (var star = 0; star < sector.galaxy.starCount; star++) writer.Write(0); // No native hive components.
        writer.Write(0); // CombatSpaceSystem native version.
        WritePool(sector.combatSpaceSystem.units, writer, unit => { unit.hatred = default; return unit; });
        WritePool(sector.combatSpaceSystem.fleets, writer, fleet =>
        {
            fleet.hatred = default;
            if (fleet.target.type == ETargetType.Enemy) fleet.target = default;
            return fleet;
        });
    }

    private static void WritePool<T>(DataPool<T> pool, BinaryWriter writer, Func<T, T> project) where T : struct, IPoolElement
    {
        writer.Write(pool.capacity); writer.Write(pool.cursor); writer.Write(pool.recycleCursor);
        for (var id = 1; id < pool.cursor; id++) project(pool.buffer[id]).Export(writer);
        for (var id = 0; id < pool.recycleCursor; id++) writer.Write(pool.recycleIds[id]);
    }

    public static void ClearGeneratedEnemies(SpaceSector sector)
    {
        // Import replaces the native arrays but does not release pre-generated components or clear the hive index.
        for (var id = sector.enemyCursor - 1; id > 0; id--)
            if (sector.enemyPool[id].id == id) sector.RemoveEnemyWithComponents(id);
        if (sector.dfHives != null)
            for (var star = 0; star < sector.dfHives.Length; star++)
            {
                var hive = sector.dfHives[star];
                while (hive != null) { var next = hive.nextSibling; hive.Free(); hive = next; }
                sector.dfHives[star] = null;
            }
        Array.Clear(sector.dfHivesByAstro, 0, sector.dfHivesByAstro.Length);
        Array.Clear(sector.enemyPool, 0, sector.enemyPool.Length);
        Array.Clear(sector.enemyAnimPool, 0, sector.enemyAnimPool.Length);
        sector.enemyCursor = 1; EnemyRecycleCursor(sector) = 0;
        sector.skillSystem.removedSkillTargets.Clear();
    }
}
