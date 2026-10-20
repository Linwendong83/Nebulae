using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using NebulaeWorld.Authority.Adapters;
using UnityEngine;

namespace NebulaeWorld.Combat;

/// <summary>Installs native inputs and maintains references; the game's own unit loops advance behavior.</summary>
internal static class NativeEnemySimulation
{
    private sealed class Entry
    {
        public long Tick;
        public GroundEnemyState Ground;
        public SpaceEnemyState Space;
        public int UnitId;
        public bool PendingTargets;
        public VectorLF3 Position;
        public Quaternion Rotation;
    }
    private sealed class WorldState
    {
        public EnemyData[] Pool;
        public readonly Dictionary<int, Entry> Units = new();
        public long PreparedTick = -1;
        public object Context;
        public bool Ready;
        public long PreparedFrame;
    }
    private static readonly ConditionalWeakTable<object, WorldState> worlds = new();
    private static long frame;

    public static void BeginFrame() => frame++;

    private static WorldState StateFor(object world, EnemyData[] pool)
    {
        var state = worlds.GetValue(world, _ => new WorldState());
        if (!ReferenceEquals(state.Pool, pool)) { state.Units.Clear(); state.Pool = pool; state.PreparedTick = -1; }
        return state;
    }

    public static void Install(PlanetFactory factory, int id, in GroundEnemyState value)
    {
        var state = StateFor(factory, factory.enemyPool);
        if (value.Kind != GroundEnemyKind.GroundUnit || value.NativeUnit?.Length is not > 0)
        { state.Units.Remove(id); return; }
        var unit = value.DecodedNativeUnit.GetValueOrDefault();
        if (!value.DecodedNativeUnit.HasValue && !EnemyUnitStateCodec.TryDecode(value.NativeUnit, out unit)) return;
        if (!state.Units.TryGetValue(id, out var entry)) state.Units[id] = entry = new Entry();
        entry.Tick = value.SampleTick; entry.Ground = value; entry.UnitId = unit.id;
        entry.PendingTargets = !Bound(factory.enemySystem?.units, factory.enemyPool[id], unit.id) || !CanRun(factory);
        entry.Position = factory.enemyPool[id].pos; entry.Rotation = factory.enemyPool[id].rot;
    }

    public static void Install(SpaceSector sector, int id, in SpaceEnemyState value)
    {
        var state = StateFor(sector, sector.enemyPool);
        if (value.Kind != SpaceEnemyKind.SpaceUnit || value.NativeUnit?.Length is not > 0)
        { state.Units.Remove(id); return; }
        var unit = value.DecodedNativeUnit.GetValueOrDefault();
        if (!value.DecodedNativeUnit.HasValue && !EnemyUnitStateCodec.TryDecode(value.NativeUnit, out unit)) return;
        if (!state.Units.TryGetValue(id, out var entry)) state.Units[id] = entry = new Entry();
        entry.Tick = value.SampleTick; entry.Space = value; entry.UnitId = unit.id;
        entry.PendingTargets = !Bound(sector.GetHiveByAstroId(value.OriginAstroId)?.units, sector.enemyPool[id], unit.id) ||
            !CanRun(sector.GetHiveByAstroId(value.OriginAstroId));
        entry.Position = sector.enemyPool[id].pos; entry.Rotation = sector.enemyPool[id].rot;
    }

    private static bool Bound(DataPool<EnemyUnitComponent> pool, in EnemyData enemy, int id) =>
        pool != null && enemy.unitId == id && id > 0 && id < pool.cursor &&
        pool.buffer[id].id == id && pool.buffer[id].enemyId == enemy.id;

    public static void Remove(object world, int id)
    {
        if (world != null && worlds.TryGetValue(world, out var state)) state.Units.Remove(id);
    }

    public static bool BindUnit(DataPool<EnemyUnitComponent> pool, ref EnemyData enemy, byte[] bytes, int planet)
    {
        if (!EnemyUnitStateCodec.TryDecode(bytes, out var unit)) return false;
        return BindDecodedUnit(pool, ref enemy, unit, planet);
    }

    public static bool BindDecodedUnit(DataPool<EnemyUnitComponent> pool, ref EnemyData enemy, EnemyUnitComponent unit, int planet)
    {
        if (pool == null || unit.id <= 0 || unit.id > EnemyUnitStateCodec.MaxPoolId) return false;
        if (unit.enemyId != enemy.id || unit.protoId != enemy.protoId || unit.planetId != planet)
            throw new InvalidDataException("Native enemy unit does not match its world identity.");
        var id = unit.id;
        if (pool.capacity <= id) pool.SetCapacity(Math.Max(id + 1, pool.capacity * 2));
        if (pool.buffer[id].id != 0 && pool.buffer[id].enemyId != enemy.id) return false;
        var occupied = pool.buffer[id].id == id;
        if (occupied)
        {
            unit.anim = pool.buffer[id].anim;
            unit.steering = pool.buffer[id].steering;
            unit.speed = pool.buffer[id].speed;
        }
        if (enemy.unitId > 0 && enemy.unitId != id && enemy.unitId < pool.cursor && pool.buffer[enemy.unitId].enemyId == enemy.id)
            pool.Remove(enemy.unitId);
        if (id >= pool.cursor)
        {
            for (var hole = pool.cursor; hole < id; hole++) pool.recycleIds[pool.recycleCursor++] = hole;
            pool.cursor = id + 1;
        }
        else if (!occupied)
            for (var slot = 0; slot < pool.recycleCursor; slot++)
                if (pool.recycleIds[slot] == id) { pool.recycleIds[slot] = pool.recycleIds[--pool.recycleCursor]; break; }
        unit.blockSkill = default; // Host skill indexes are private; locally created skills establish their own links.
        unit.hatredLock = default;
        TranslateTarget(ref unit.hatred.max, planet);
        TranslateTarget(ref unit.hatred.h1, planet);
        TranslateTarget(ref unit.hatred.h2, planet);
        TranslateTarget(ref unit.hatred.h3, planet);
        TranslateTarget(ref unit.hatred.h4, planet);
        TranslateTarget(ref unit.hatred.h5, planet);
        TranslateTarget(ref unit.hatred.h6, planet);
        TranslateTarget(ref unit.hatred.min, planet);
        unit.assaults.targetObject = LocalTarget(unit.assaults.targetObject, planet);
        pool.buffer[id] = unit;
        enemy.unitId = id;
        return true;
    }

    private static void TranslateTarget(ref HatredTarget target, int planet)
    {
        var local = LocalTarget(target.target, planet);
        target.target = local;
        if (local == 0) target.value = 0;
    }

    private static int LocalTarget(int target, int planet)
    {
        if ((ETargetType)(target >> 26) != ETargetType.Craft) return target;
        var replica = Multiplayer.Session?.AuthorityRuntime?.WorldReplica;
        var id = target & 33554431;
        var space = planet <= 0 || (target & 33554432) != 0;
        if (replica == null || !replica.TryFindKey(space ? PoolKind.SpaceCraft : PoolKind.GroundCraft,
                space ? AuthorityScope.Sector : planet, id, out var key) ||
            !replica.Mirrors.TryGetValue(key, out var mirror) || mirror.LocalComponentId <= 0) return 0;
        return (target & ~33554431) | mirror.LocalComponentId;
    }

    public static bool IsLive(PoolKind kind, int scope)
    {
        var replica = Multiplayer.Session?.AuthorityRuntime?.WorldReplica;
        return replica != null && replica.Versions.TryGetScope(new ScopeKey(kind, scope), out var version) &&
            version.Phase == SubscriptionPhase.Live;
    }

    private static bool GroundReady(PlanetFactory factory) => factory?.planet.factoryLoaded == true &&
        IsLive(PoolKind.GroundEnemy, factory.planetId) && IsLive(PoolKind.Base, factory.planetId) && IsLive(PoolKind.GroundCraft, factory.planetId);

    private static bool Prepared(object world, out WorldState state) => worlds.TryGetValue(world, out state) &&
        state.PreparedTick == GameMain.gameTick && state.PreparedFrame == frame &&
        ReferenceEquals(state.Context, Multiplayer.Session?.AuthorityRuntime);

    public static bool CanRun(PlanetFactory factory) => factory != null &&
        (Prepared(factory, out var state) ? state.Ready : GroundReady(factory));

    private static bool SpaceReady() => IsLive(PoolKind.SpaceEnemy, AuthorityScope.Sector) &&
        IsLive(PoolKind.HiveSummary, AuthorityScope.Sector) && IsLive(PoolKind.SpaceCraft, AuthorityScope.Sector);

    public static bool CanRun(EnemyDFHiveSystem hive) => hive?.sector?.model?.gpuiManager != null &&
        (hive.starData.id == GameMain.localStar?.id || UIGame.viewMode == EViewMode.Starmap) &&
        (Prepared(hive.sector, out var state) ? state.Ready : SpaceReady());

    public static int SimulationId(ref EnemyUnitComponent unit)
    {
        if (!WorldSimulation.IsClient) return unit.id;
        object world = unit.planetId > 0 ? GameMain.galaxy?.PlanetById(unit.planetId)?.factory : GameMain.spaceSector;
        var pool = world is PlanetFactory factory ? factory.enemyPool : (world as SpaceSector)?.enemyPool;
        if (world == null || pool == null || unit.enemyId <= 0 || unit.enemyId >= pool.Length ||
            !worlds.TryGetValue(world, out var state) || !ReferenceEquals(state.Pool, pool) ||
            !state.Units.TryGetValue(unit.enemyId, out var entry) || GameMain.gameTick <= entry.Tick ||
            entry.PendingTargets || entry.UnitId != unit.id ||
            pool[unit.enemyId].id != unit.enemyId || pool[unit.enemyId].unitId != unit.id) return 0;
        return world is PlanetFactory ground ? CanRun(ground) ? unit.id : 0 :
            CanRun(((SpaceSector)world).GetHiveByAstroId(pool[unit.enemyId].originAstroId)) ? unit.id : 0;
    }

    public static void Prepare(PlanetFactory factory)
    {
        if (factory == null || !worlds.TryGetValue(factory, out var state) || !ReferenceEquals(state.Pool, factory.enemyPool)) return;
        state.Context = Multiplayer.Session?.AuthorityRuntime; state.Ready = GroundReady(factory);
        state.PreparedTick = GameMain.gameTick; state.PreparedFrame = frame;
        foreach (var pair in state.Units)
        {
            var id = pair.Key;
            if (id >= factory.enemyCursor || factory.enemyPool[id].id != id) continue;
            var entry = pair.Value;
            if (factory.enemyPool[id].unitId <= 0)
            {
                GroundEnemyReplicaPools.EnsureUnitComponent(factory, id, entry.Ground);
                entry.PendingTargets = !Bound(factory.enemySystem.units, factory.enemyPool[id], entry.UnitId) || !state.Ready;
            }
            if (entry.PendingTargets)
            {
                if (state.Ready)
                    entry.PendingTargets = !(entry.Ground.DecodedNativeUnit.HasValue
                        ? BindDecodedUnit(factory.enemySystem.units, ref factory.enemyPool[id], entry.Ground.DecodedNativeUnit.Value, factory.planetId)
                        : BindUnit(factory.enemySystem.units, ref factory.enemyPool[id], entry.Ground.NativeUnit, factory.planetId));
            }
            var position = factory.enemyPool[id].pos; var rotation = factory.enemyPool[id].rot;
            GroundEnemyReplicaPools.RefreshPhysics(factory, id, position != entry.Position, rotation != entry.Rotation);
            entry.Position = position; entry.Rotation = rotation;
        }
    }

    public static void Prepare(SpaceSector sector)
    {
        if (sector == null) return;
        var prepared = StateFor(sector, sector.enemyPool);
        prepared.Context = Multiplayer.Session?.AuthorityRuntime; prepared.Ready = SpaceReady();
        prepared.PreparedTick = GameMain.gameTick; prepared.PreparedFrame = frame;
        if (sector.dfHivesByAstro != null && sector.astros != null)
            foreach (var hive in sector.dfHivesByAstro)
                if (hive?.hiveAstroOrbit != null && hive.hiveAstroId > 1000000 &&
                    hive.hiveAstroId - 1000000 < sector.astros.Length && !CanRun(hive))
                    hive.hiveAstroOrbit.PredictPose(GameMain.gameTick, hive.starData.uPosition,
                        ref sector.astros[hive.hiveAstroId - 1000000]);
        if (!worlds.TryGetValue(sector, out var state) || !ReferenceEquals(state.Pool, sector.enemyPool)) return;
        foreach (var pair in state.Units)
        {
            var id = pair.Key;
            if (id >= sector.enemyCursor || sector.enemyPool[id].id != id) continue;
            var entry = pair.Value;
            if (sector.enemyPool[id].unitId <= 0)
            {
                SpaceEnemyReplicaPools.EnsureUnitComponent(sector, id, entry.Space);
                var origin = sector.GetHiveByAstroId(sector.enemyPool[id].originAstroId);
                entry.PendingTargets = !Bound(origin?.units, sector.enemyPool[id], entry.UnitId) || !CanRun(origin);
            }
            if (!entry.PendingTargets) continue;
            var hive = sector.GetHiveByAstroId(sector.enemyPool[id].originAstroId);
            if (CanRun(hive))
                entry.PendingTargets = !(entry.Space.DecodedNativeUnit.HasValue
                    ? BindDecodedUnit(hive.units, ref sector.enemyPool[id], entry.Space.DecodedNativeUnit.Value, 0)
                    : BindUnit(hive.units, ref sector.enemyPool[id], entry.Space.NativeUnit, 0));
        }
    }
}
