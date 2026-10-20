using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NebulaeWorld.Authority;
using UnityEngine;

namespace NebulaeWorld.Combat;

/// <summary>Short-lived model offsets; checkpoints and native combat pools remain authoritative.</summary>
internal sealed class EnemyMotionPresentation : IDisposable
{
    internal const double CorrectionTicks = 6;
    internal const double GroundSnapDistance = 8, SpaceSnapDistance = 200;

    internal readonly struct Checkpoint
    {
        internal readonly EnemyData Enemy;
        internal readonly VectorLF3 Position;
        internal readonly Quaternion Rotation;
        internal readonly double Tick;
        internal readonly bool Valid;

        internal Checkpoint(in EnemyData enemy, VectorLF3 position, Quaternion rotation, double tick)
        {
            Enemy = enemy;
            Position = position;
            Rotation = rotation;
            Tick = tick;
            Valid = true;
        }
    }

    private sealed class Entry
    {
        internal EnemyData Identity;
        internal VectorLF3 PositionOffset;
        internal Quaternion RotationOffset;
        internal double Tick;
        internal int ActiveIndex = -1;
    }

    private sealed class WorldState
    {
        internal EnemyData[] Pool;
        internal readonly Dictionary<int, Entry> Entries = new();
        internal readonly List<Entry> Active = new();
        internal readonly DrawState Drawing = new();
    }

    /// <summary>Reused drawing scratch, restored by a Harmony finalizer even if drawing throws.</summary>
    internal sealed class DrawState
    {
        private readonly List<(GPUOBJECT[] Pool, int Id, GPUOBJECT Value)> ground = new();
        private readonly List<(SPACEOBJECT[] Pool, int Id, SPACEOBJECT Value)> space = new();
        private FactoryModel factoryModel;
        private SectorModel sectorModel;
        internal bool InUse { get; private set; }

        internal void Begin(FactoryModel factory, SectorModel sector)
        {
            factoryModel = factory;
            sectorModel = sector;
            InUse = true;
        }

        internal void Write(GPUOBJECT[] pool, int id, VectorLF3 position, Quaternion rotation)
        {
            ground.Add((pool, id, pool[id]));
            ref var value = ref pool[id];
            value.posx = (float)position.x; value.posy = (float)position.y; value.posz = (float)position.z;
            value.rotx = rotation.x; value.roty = rotation.y; value.rotz = rotation.z; value.rotw = rotation.w;
        }

        internal void Write(SPACEOBJECT[] pool, int id, VectorLF3 position, Quaternion rotation)
        {
            space.Add((pool, id, pool[id]));
            ref var value = ref pool[id];
            value.posx = (float)position.x; value.posy = (float)position.y; value.posz = (float)position.z;
            value.rotx = rotation.x; value.roty = rotation.y; value.rotz = rotation.z; value.rotw = rotation.w;
        }

        internal void Restore()
        {
            foreach (var item in ground) item.Pool[item.Id] = item.Value;
            foreach (var item in space) item.Pool[item.Id] = item.Value;
            // The GPU still contains the smoothed pose. Arrange one final raw upload even if
            // correction expires before another simulation tick or before a second camera draws.
            if (ground.Count > 0 && factoryModel is not null) factoryModel.enemyUnitsDirty = true;
            if (space.Count > 0 && sectorModel is not null) sectorModel.enemyUnitsDirty = true;
            ground.Clear(); space.Clear();
            factoryModel = null; sectorModel = null;
            InUse = false;
        }
    }

    private ConditionalWeakTable<object, WorldState> worlds = new();
    private readonly BattleVisualClock clock = new();
    private readonly Func<double> sampleClock;

    internal EnemyMotionPresentation(Func<double> sampleClock = null) => this.sampleClock = sampleClock;

    internal double Now => sampleClock != null ? sampleClock() : clock.Sample(GameMain.gameTick,
        Time.realtimeSinceStartupAsDouble, FPSController.currentUPS,
        GameMain.isPaused || Multiplayer.Session?.WorldPaused == true || Multiplayer.Session?.IsGameLoaded != true);

    internal Checkpoint Capture(PlanetFactory factory, int id, bool moving) => Capture(factory, factory.enemyPool,
        id, moving && WorldSimulation.IsClient && NativeEnemySimulation.CanRun(factory), NowIfClient());

    internal Checkpoint Capture(SpaceSector sector, int id, bool moving) => Capture(sector, sector.enemyPool,
        id, moving && WorldSimulation.IsClient && NativeEnemySimulation.CanRun(sector.GetHiveByAstroId(sector.enemyPool[id].originAstroId)), NowIfClient());

    private double NowIfClient() => WorldSimulation.IsClient ? Now : 0;

    // The explicit clock/readiness inputs also let regression tests exercise the production state
    // machine without a Unity scene. No renderer or native simulation is called by these methods.
    internal Checkpoint Capture(object world, EnemyData[] pool, int id, bool ready, double now)
    {
        if (!ready || id <= 0 || id >= pool.Length || pool[id].id != id || pool[id].unitId <= 0 || !pool[id].dynamic)
        {
            Remove(world, id);
            return default;
        }
        if (!worlds.TryGetValue(world, out var state) || !ReferenceEquals(state.Pool, pool))
        {
            StateFor(world, pool);
            return default;
        }
        ref var enemy = ref pool[id];
        Pose(state, in enemy, now, out var position, out var rotation);
        return new Checkpoint(enemy, position, rotation, now);
    }

    internal void Rebase(object world, EnemyData[] pool, int id, in Checkpoint before, bool space)
    {
        if (!before.Valid || id <= 0 || id >= pool.Length || !SameIdentity(before.Enemy, pool[id]) ||
            !worlds.TryGetValue(world, out var state) || !ReferenceEquals(state.Pool, pool))
        {
            Remove(world, id);
            return;
        }
        ref var enemy = ref pool[id];
        var limit = space ? SpaceSnapDistance : GroundSnapDistance;
        var difference = before.Position - enemy.pos;
        if (!Finite(difference) || !Finite(enemy.pos) || !TryNormalize(before.Rotation, out var shownRotation) ||
            !TryNormalize(enemy.rot, out var rotation) ||
            (before.Enemy.pos - enemy.pos).sqrMagnitude > limit * limit || difference.sqrMagnitude > limit * limit ||
            Math.Abs(Dot(shownRotation, rotation)) < 0.5 - 1e-6) // cos(120 degrees / 2)
        {
            Remove(world, id);
            return;
        }
        if (!state.Entries.TryGetValue(id, out var entry)) state.Entries.Add(id, entry = new Entry());
        entry.Identity = enemy;
        entry.PositionOffset = difference;
        entry.RotationOffset = shownRotation * new Quaternion(-rotation.x, -rotation.y, -rotation.z, rotation.w);
        entry.Tick = before.Tick;
        if (difference.sqrMagnitude < 1e-18 && Math.Abs(entry.RotationOffset.w) >= 1 - 1e-12)
        {
            Deactivate(state, entry);
            return;
        }
        if (entry.ActiveIndex < 0)
        {
            entry.ActiveIndex = state.Active.Count;
            state.Active.Add(entry);
        }
    }

    private WorldState StateFor(object world, EnemyData[] pool)
    {
        var state = worlds.GetValue(world, _ => new WorldState());
        if (!ReferenceEquals(state.Pool, pool))
        {
            state.Drawing.Restore();
            state.Entries.Clear(); state.Active.Clear(); state.Pool = pool;
        }
        return state;
    }

    internal void Sample(object world, EnemyData[] pool, int id, double now, out VectorLF3 position, out Quaternion rotation)
    {
        ref var enemy = ref pool[id];
        if (worlds.TryGetValue(world, out var state) && ReferenceEquals(state.Pool, pool))
            Pose(state, in enemy, now, out position, out rotation);
        else { position = enemy.pos; rotation = enemy.rot; }
    }

    private static void Pose(WorldState state, in EnemyData enemy, double now, out VectorLF3 position, out Quaternion rotation)
    {
        position = enemy.pos; rotation = enemy.rot;
        if (!state.Entries.TryGetValue(enemy.id, out var entry) || entry.ActiveIndex < 0 || !SameIdentity(entry.Identity, enemy)) return;
        var weight = Math.Max(0, Math.Min(1, 1 - (now - entry.Tick) / CorrectionTicks));
        position += entry.PositionOffset * weight;
        rotation = FadeRotation(entry.RotationOffset, weight) * rotation;
    }

    private static bool SameIdentity(in EnemyData a, in EnemyData b) =>
        a.id == b.id && a.unitId == b.unitId && a.protoId == b.protoId && a.modelIndex == b.modelIndex &&
        a.astroId == b.astroId && a.originAstroId == b.originAstroId && a.owner == b.owner && a.port == b.port;

    // Managed quaternion math keeps this small interpolation independent of Unity internal calls.
    private static double Dot(Quaternion a, Quaternion b) => (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w;
    private static bool Finite(VectorLF3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    private static bool TryNormalize(Quaternion value, out Quaternion result)
    {
        var length = Math.Sqrt(Dot(value, value));
        result = Quaternion.identity;
        if (!Finite(length) || length < 1e-9) return false;
        result = new Quaternion((float)(value.x / length), (float)(value.y / length), (float)(value.z / length), (float)(value.w / length));
        return true;
    }

    private static Quaternion FadeRotation(Quaternion value, double weight)
    {
        if (value.w < 0) value = new Quaternion(-value.x, -value.y, -value.z, -value.w);
        var angle = Math.Acos(Math.Max(-1, Math.Min(1, value.w)));
        var sine = Math.Sin(angle);
        if (sine < 1e-6) return Quaternion.identity;
        var scale = Math.Sin(angle * weight) / sine;
        return new Quaternion((float)(value.x * scale), (float)(value.y * scale), (float)(value.z * scale), (float)Math.Cos(angle * weight));
    }

    private static void Deactivate(WorldState state, Entry entry)
    {
        if (entry.ActiveIndex < 0) return;
        var last = state.Active[state.Active.Count - 1];
        state.Active[entry.ActiveIndex] = last;
        last.ActiveIndex = entry.ActiveIndex;
        state.Active.RemoveAt(state.Active.Count - 1);
        entry.ActiveIndex = -1;
    }

    internal void Remove(object world, int id)
    {
        if (world == null || !worlds.TryGetValue(world, out var state) || !state.Entries.TryGetValue(id, out var entry)) return;
        Deactivate(state, entry);
        state.Entries.Remove(id);
    }

    internal void Forget(object world)
    {
        if (world == null || !worlds.TryGetValue(world, out var state)) return;
        state.Drawing.Restore();
        worlds.Remove(world);
    }

    internal DrawState BeginDraw(FactoryModel model)
    {
        var factory = model.planet?.factory;
        if (factory == null) return null;
        if (!NativeEnemySimulation.CanRun(factory)) { Forget(factory); return null; }
        return BeginDraw(factory, factory.enemyPool, model, null, Now);
    }

    internal DrawState BeginDraw(SectorModel model)
    {
        var sector = GameMain.spaceSector;
        if (sector == null || !ReferenceEquals(sector.model, model)) return null;
        return BeginDraw(sector, sector.enemyPool, null, model, Now);
    }

    internal DrawState BeginDraw(object world, EnemyData[] pool, FactoryModel ground, SectorModel space, double now)
    {
        if (!worlds.TryGetValue(world, out var state)) return null;
        if (!ReferenceEquals(state.Pool, pool)) { Forget(world); return null; }
        var draw = state.Drawing;
        if (draw.InUse || state.Active.Count == 0) return null;
        draw.Begin(ground, space);
        try
        {
            for (var i = state.Active.Count - 1; i >= 0; i--)
            {
                var entry = state.Active[i];
                var id = entry.Identity.id;
                if (id >= pool.Length || !SameIdentity(entry.Identity, pool[id]) || now - entry.Tick >= CorrectionTicks ||
                    world is SpaceSector sector && !NativeEnemySimulation.CanRun(sector.GetHiveByAstroId(pool[id].originAstroId)))
                { Deactivate(state, entry); continue; }
                ref var enemy = ref pool[id];
                if (enemy.modelId <= 0) continue;
                Pose(state, in enemy, now, out var position, out var rotation);
                if (ground is not null)
                {
                    var renderers = ground.gpuiManager?.objectRenderers;
                    if (renderers == null || enemy.modelIndex >= renderers.Length ||
                        renderers[enemy.modelIndex] is not DynamicRenderer renderer || enemy.modelId >= renderer.instPool.Length ||
                        renderer.instPool[enemy.modelId].objId != id) continue;
                    draw.Write(renderer.instPool, enemy.modelId, position, rotation);
                    ground.enemyUnitsDirty = true;
                }
                else if (space is not null)
                {
                    var renderers = space.gpuiManager?.objectRenderers;
                    if (renderers == null || enemy.modelIndex >= renderers.Length ||
                        renderers[enemy.modelIndex] is not { } renderer || enemy.modelId >= renderer.instPool.Length ||
                        renderer.instPool[enemy.modelId].objId != id) continue;
                    if (enemy.astroId == 0)
                    {
                        position = Maths.QInvRotateLF(GameMain.data.relativeRot, position - GameMain.data.relativePos);
                        rotation = Maths.QInvMultiply(GameMain.data.relativeRot, rotation);
                    }
                    draw.Write(renderer.instPool, enemy.modelId, position, rotation);
                    space.enemyUnitsDirty = true;
                }
            }
            return draw;
        }
        catch { draw.Restore(); throw; }
    }

    public void Dispose() => worlds = new ConditionalWeakTable<object, WorldState>();
}
