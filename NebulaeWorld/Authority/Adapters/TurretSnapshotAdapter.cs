using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;

namespace NebulaeWorld.Authority.Adapters;

internal sealed class TurretSnapshotAdapter : IHostWorldView, IHostIncrementalWorldView, IHostSnapshotWorldView
{
    private const int MembershipAuditTicks = 1200;
    private sealed class ScopeIndex
    {
        public PlanetFactory Factory;
        public long AuditTick;
        public readonly Dictionary<int, ObjectKey> Keys = new();
        public readonly HashSet<ObjectKey> Dirty = new(), Removed = new();
        public readonly List<ObjectKey>[] Checkpoints = new List<ObjectKey>[TurretStatePolicy.CheckpointIntervalTicks];
    }
    private readonly object gate = new();
    private readonly Dictionary<int, ScopeIndex> indexes = new();
    private readonly AuthorityEpoch epoch;
    private readonly Dictionary<int, SlotGenerationTracker> trackers = new();
    private readonly Dictionary<ObjectKey, (TurretState Value, byte[] Bytes)> cached = new();
    private readonly Dictionary<int, PlanetFactory> factories = new();
    private readonly List<ObjectKey> obsolete = new();
    public TurretSnapshotAdapter(AuthorityEpoch epoch) { this.epoch = epoch; }
    public long Generation(int planet, int entity)
    {
        lock (gate) return trackers.TryGetValue(planet, out var tracker) ? tracker.GenerationOf(entity) : 0;
    }
    private SlotGenerationTracker Tracker(int planet)
    {
        if (!trackers.TryGetValue(planet, out var tracker)) trackers[planet] = tracker = new SlotGenerationTracker();
        return tracker;
    }
    public void Retire(int planet, int entity)
    {
        lock (gate)
        {
            var tracker = Tracker(planet);
            var generation = tracker.GenerationOf(entity);
            if (generation > 0)
            {
                var key = ObjectKey.Create(epoch, PoolKind.Turret, planet, entity, generation);
                cached.Remove(key);
                if (indexes.TryGetValue(planet, out var index))
                {
                    index.Keys.Remove(entity);
                    index.Dirty.Remove(key);
                    index.Checkpoints[entity % index.Checkpoints.Length]?.Remove(key);
                    index.Removed.Add(key);
                }
            }
            tracker.Retire(entity);
        }
    }
    public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
    {
        if (scope.Kind != PoolKind.Turret) return false;
        var factory = GameMain.galaxy?.PlanetById(scope.Scope)?.factory;
        if (factory?.defenseSystem?.turrets == null) return false;
        var pool = factory.defenseSystem.turrets;
        var tracker = Tracker(scope.Scope);
        if (factories.TryGetValue(scope.Scope, out var previousFactory) && !ReferenceEquals(previousFactory, factory))
        {
            obsolete.Clear();
            foreach (var key in cached.Keys)
                if (key.Scope == scope.Scope) obsolete.Add(key);
            foreach (var key in obsolete) Retire(key.Scope, key.NativeId);
        }
        factories[scope.Scope] = factory;
        var index = new ScopeIndex { Factory = factory, AuditTick = GameMain.gameTick };
        indexes[scope.Scope] = index;
        for (var id = 1; id < pool.cursor; id++)
        {
            ref var t = ref pool.buffer[id];
            if (t.id != id || !TryTurret(factory, t.entityId, out _)) continue;
            var key = ObjectKey.Create(epoch, PoolKind.Turret, scope.Scope, t.entityId, tracker.ObserveOccupied(t.entityId));
            members.Add(key);
            Index(index, key);
        }
        tracker.EndScan();
        obsolete.Clear();
        foreach (var key in cached.Keys)
            if (key.Scope == scope.Scope && !tracker.IsAlive(key.NativeId)) obsolete.Add(key);
        foreach (var key in obsolete) cached.Remove(key);
        return true;
    }
    public bool IsCurrent(in ObjectKey key) => key.IsValid && key.Epoch.Equals(epoch) && key.Kind == PoolKind.Turret &&
        Tracker(key.Scope).IsAlive(key.NativeId) && Tracker(key.Scope).GenerationOf(key.NativeId) == key.Generation;
    private static void Index(ScopeIndex index, ObjectKey key)
    {
        if (index.Keys.ContainsKey(key.NativeId)) return;
        index.Keys.Add(key.NativeId, key);
        var bucket = key.NativeId % index.Checkpoints.Length;
        (index.Checkpoints[bucket] ??= new List<ObjectKey>()).Add(key);
    }
    public void Added(PlanetFactory factory, int entity)
    {
        lock (gate)
        {
            if (!indexes.TryGetValue(factory.planetId, out var index) || !ReferenceEquals(index.Factory, factory) ||
                !TryTurret(factory, entity, out _)) return;
            var key = ObjectKey.Create(epoch, PoolKind.Turret, factory.planetId, entity, Tracker(factory.planetId).ObserveOccupied(entity));
            Index(index, key);
            index.Dirty.Add(key);
        }
    }
    public void Invalidate(ObjectKey key)
    {
        lock (gate)
        {
            cached.Remove(key);
            if (indexes.TryGetValue(key.Scope, out var index)) index.Dirty.Add(key);
        }
    }
    public void MarkChanged(PlanetFactory factory, int entity)
    {
        lock (gate)
            if (indexes.TryGetValue(factory.planetId, out var index) && index.Keys.TryGetValue(entity, out var key))
                Invalidate(key);
    }
    public bool TryReadChanges(ScopeKey scope, long hostTick, List<ObjectKey> candidates, List<ObjectKey> removed)
    {
        if (scope.Kind != PoolKind.Turret || !indexes.TryGetValue(scope.Scope, out var index)) return false;
        var factory = GameMain.galaxy?.PlanetById(scope.Scope)?.factory;
        if (!ReferenceEquals(index.Factory, factory) || factory?.defenseSystem?.turrets == null ||
            factory.defenseSystem.turrets.count != index.Keys.Count || hostTick < index.AuditTick ||
            hostTick - index.AuditTick >= MembershipAuditTicks) return false;
        candidates.AddRange(index.Dirty);
        var bucket = index.Checkpoints[(int)(hostTick % index.Checkpoints.Length)];
        if (bucket != null)
            foreach (var key in bucket)
                if (!index.Dirty.Contains(key)) candidates.Add(key);
        removed.AddRange(index.Removed);
        index.Removed.Clear();
        return true;
    }
    public void StateSampled(in ObjectKey key)
    {
        if (key.Kind == PoolKind.Turret && indexes.TryGetValue(key.Scope, out var index)) index.Dirty.Remove(key);
    }
    public bool TryReadSnapshotState(ObjectKey key, out byte[] state)
    {
        cached.Remove(key);
        return TryReadState(key, out state);
    }
    public bool TryReadState(ObjectKey key, out byte[] state)
    {
        state = null;
        if (!IsCurrent(key)) return false;
        var factory = GameMain.galaxy?.PlanetById(key.Scope)?.factory;
        if (!TryTurret(factory, key.NativeId, out var id)) return false;
        var current = Capture(factory, id);
        if (cached.TryGetValue(key, out var previous) && !TurretStatePolicy.ShouldPublish(previous.Value, current))
        { state = previous.Bytes; return true; }
        if (!TurretStateCodec.TryEncode(current, out state)) return false;
        cached[key] = (current, state); return true;
    }
    public static bool TryTurret(PlanetFactory factory, int entity, out int id)
    {
        id = 0;
        if (factory?.defenseSystem?.turrets == null || entity <= 0 || entity >= factory.entityCursor || entity >= factory.entityPool.Length || factory.entityPool[entity].id != entity) return false;
        id = factory.entityPool[entity].turretId;
        var pool = factory.defenseSystem.turrets;
        return id > 0 && id < pool.cursor && id < pool.buffer.Length && pool.buffer[id].id == id && pool.buffer[id].entityId == entity;
    }
    public static TurretState Capture(PlanetFactory f, int id)
    {
        ref var t = ref f.defenseSystem.turrets.buffer[id];
        var target = t.target; target.type = ETargetType.Enemy;
        var completedTick = Multiplayer.Session?.AuthorityRuntime?.CompletedSimulationTick ?? 0;
        return new TurretState
        {
            EntityId = t.entityId,
            ModelIndex = f.entityPool[t.entityId].modelIndex,
            Type = (byte)t.type,
            Group = t.group,
            VsSettings = (byte)t.vsSettings,
            Flags = (byte)((t.isAiming ? 1 : 0) | (t.isLockingTarget ? 2 : 0) | (t.activeSearch ? 4 : 0)),
            TargetId = t.target.id,
            TargetAstro = t.target.astroId,
            TargetType = (byte)ETargetType.Enemy,
            TargetGeneration = NebulaeWorld.Combat.EnemyTargetIdentity.TryResolve(target, out var targetKey) ? targetKey.Generation : 0,
            ItemId = t.itemId,
            ItemCount = t.itemCount,
            ItemInc = t.itemInc,
            BulletCount = t.bulletCount,
            ItemBulletCount = t.itemBulletCount,
            BulletDamage = t.bulletDamage,
            BulletInc = t.currentBulletInc,
            SupernovaTick = t.supernovaTick,
            SupernovaStrength = t.supernovaStrength,
            Direction = t.localDir,
            TargetDirection = t.localTargetDir,
            RoundFire = t.roundFire,
            MuzzleFire = t.muzzleFire,
            MuzzleReset = t.muzzleReset,
            MuzzleIndex = t.muzzleIndex,
            PhasePos = t.phasePos,
            AimTime = t.aimt,
            KillCount = t.totalKillCount,
            TotalDamage = t.totalDamage,
            SampleTick = completedTick > 0 ? completedTick : GameMain.gameTick
        };
    }
}
