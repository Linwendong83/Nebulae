#region

using System.Collections.Generic;
using System;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;

#endregion

namespace NebulaeWorld.Authority.Adapters;

/// <summary>
/// The host's canonical view of one planet's building combat state.
/// </summary>
/// <remarks>
/// <para>
/// This <see cref="IHostWorldView"/> covers
/// the <see cref="PoolKind.Entity"/> pool of each loaded factory. It reads exactly two vanilla
/// sources — the entity's combat-stat reference resolved against the global skill pool, and its
/// construct-stat reference resolved against the factory's own construction system — and encodes
/// them as a <see cref="FactoryCombatState"/> per entity.
/// </para>
/// <para>
/// Snapshot reads never mutate world state: they cannot call <c>HandleFullHp</c>, remove stats or invoke other
/// writers.
/// </para>
/// <para>
/// Unreadable is not empty. A planet whose factory is not loaded returns
/// <c>TryReadMembers = false</c>, which the replicator treats as "skip this scope this frame" —
/// never as "publish a wave of deaths". Slot generations are minted per scope by
/// <see cref="SlotGenerationTracker"/>, so a dismantled and rebuilt entity in the same slot is a
/// new <see cref="ObjectKey"/>, never a resurrection of the old one.
/// </para>
/// </remarks>
internal sealed class FactoryCombatSnapshotAdapter : IHostWorldView, IHostIncrementalWorldView
{
    private readonly AuthorityEpoch epoch;
    private readonly Dictionary<int, SlotGenerationTracker> trackers = [];
    private static readonly byte[] undamagedState = EncodeUndamagedState();
    private readonly object gate = new();
    private readonly Dictionary<int, FactoryIndex> indices = [];
    private readonly Func<int, PlanetFactory> resolveFactory;
    private readonly Func<long> clock;

    private sealed class FactoryIndex
    {
        public PlanetFactory Factory;
        public bool Initialized;
        public long AuditTick;
        public readonly Dictionary<int, ObjectKey> Live = [];
        public readonly HashSet<int> Dirty = [];
        public readonly HashSet<ObjectKey> Births = [];
        public readonly HashSet<ObjectKey> Removed = [];
    }

    public long MembershipScans { get; private set; }
    public long StateReads { get; private set; }
    public long IncrementalFrames { get; private set; }

    /// <summary>Scopes skipped because their factory was not readable. Persistent growth means a subscriber is watching a planet the host never loads.</summary>
    public long UnreadableScopes { get; private set; }

    /// <summary>
    /// Entity references that pointed at a stat which is missing or belongs to another object.
    /// A vanilla invariant violation on the host; published as "no stat" and counted, never guessed around.
    /// </summary>
    public long CorruptReferences { get; private set; }

    public FactoryCombatSnapshotAdapter(AuthorityEpoch epoch, Func<int, PlanetFactory> resolveFactory = null, Func<long> clock = null)
    {
        if (!epoch.IsValid) throw new System.ArgumentException("The adapter needs a valid world epoch.", nameof(epoch));
        this.epoch = epoch;
        this.resolveFactory = resolveFactory ?? ResolveFactory;
        this.clock = clock ?? (() => GameMain.gameTick);
    }

    /// <summary>The member keys of one planet's entity pool, each with its slot generation.</summary>
    public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
    {
        if (scope.Kind != PoolKind.Entity)
        {
            // This adapter reads only the building pool.
            return false;
        }
        var factory = resolveFactory(scope.Scope);
        if (factory == null)
        {
            UnreadableScopes++;
            return false;
        }

        lock (gate)
        {
            var index = IndexFor(scope.Scope);
            if (!ReferenceEquals(index.Factory, factory)) { index.Factory = factory; index.Initialized = false; }
            index.Live.Clear();
            var tracker = TrackerFor(scope.Scope);
            var pool = factory.entityPool;
            for (var id = 1; id < factory.entityCursor; id++)
            {
                if (pool[id].id != id) continue;
                var generation = tracker.ObserveOccupied(id);
                var key = ObjectKey.Create(epoch, PoolKind.Entity, scope.Scope, id, generation);
                members.Add(key); index.Live[id] = key;
            }
            tracker.EndScan();
            index.Dirty.IntersectWith(index.Live.Keys);
            // Audits feed every live identity back into the candidate set. Slow sampling may skip
            // an identity on this exact tick; it must remain queued until its own phase is read.
            foreach (var id in index.Live.Keys) index.Dirty.Add(id);
            index.Initialized = true;
            index.AuditTick = clock();
            MembershipScans++;
        }
        return true;
    }

    public bool TryReadChanges(ScopeKey scope, long hostTick, List<ObjectKey> candidates, List<ObjectKey> removed)
    {
        if (scope.Kind != PoolKind.Entity) return false;
        var factory = resolveFactory(scope.Scope);
        if (factory == null) return false;
        lock (gate)
        {
            var index = IndexFor(scope.Scope);
            if (!index.Initialized || !ReferenceEquals(index.Factory, factory)) return false;
            // A periodic native audit catches writes from other mods. Life events are drained first
            // so a birth and retirement in one frame remain visible as two different facts.
            if ((hostTick < index.AuditTick || hostTick - index.AuditTick >= 120) && index.Births.Count == 0 && index.Removed.Count == 0)
                return false;
            var offset = candidates.Count;
            foreach (var birth in index.Births) candidates.Add(birth);
            foreach (var id in index.Dirty)
                if (index.Live.TryGetValue(id, out var key) && !index.Births.Contains(key)) candidates.Add(key);
            foreach (var retirement in index.Removed) removed.Add(retirement);
            candidates.Sort(offset, candidates.Count - offset, KeyComparer.Instance);
            removed.Sort(KeyComparer.Instance);
            index.Births.Clear(); index.Removed.Clear();
            IncrementalFrames++;
            return true;
        }
    }

    private sealed class KeyComparer : IComparer<ObjectKey>
    {
        public static readonly KeyComparer Instance = new();
        public int Compare(ObjectKey left, ObjectKey right)
        {
            var id = left.NativeId.CompareTo(right.NativeId);
            return id != 0 ? id : left.Generation.CompareTo(right.Generation);
        }
    }

    private FactoryIndex IndexFor(int planet)
    {
        if (!indices.TryGetValue(planet, out var index)) indices[planet] = index = new FactoryIndex();
        return index;
    }

    public void MarkState(int planetId, int entityId)
    {
        if (entityId <= 0) return;
        lock (gate) IndexFor(planetId).Dirty.Add(entityId);
    }

    public void StateSampled(in ObjectKey key)
    {
        if (key.Kind != PoolKind.Entity || !key.Epoch.Equals(epoch)) return;
        lock (gate)
        {
            var index = IndexFor(key.Scope);
            if (index.Live.TryGetValue(key.NativeId, out var current) && current.Equals(key)) index.Dirty.Remove(key.NativeId);
        }
    }

    public void Added(PlanetFactory factory, int entityId)
    {
        if (entityId <= 0 || !ReferenceEquals(resolveFactory(factory.planetId), factory)) return;
        lock (gate)
        {
            var index = IndexFor(factory.planetId);
            var key = ObjectKey.Create(epoch, PoolKind.Entity, factory.planetId, entityId, TrackerFor(factory.planetId).ObserveOccupied(entityId));
            index.Live[entityId] = key; index.Dirty.Add(entityId);
            if (index.Initialized) index.Births.Add(key);
        }
    }

    public void Removed(in ObjectKey key)
    {
        if (!key.Epoch.Equals(epoch) || key.Kind != PoolKind.Entity) return;
        lock (gate)
        {
            var index = IndexFor(key.Scope);
            TrackerFor(key.Scope).Retire(key.NativeId);
            index.Live.Remove(key.NativeId); index.Dirty.Remove(key.NativeId);
            if (index.Initialized) index.Removed.Add(key);
        }
    }

    /// <summary>The canonical combat/construct state of one entity, encoded for the wire.</summary>
    public bool TryReadState(ObjectKey key, out byte[] state)
    {
        state = null;
        if (key.Kind != PoolKind.Entity || !key.Epoch.Equals(epoch)) return false;
        var factory = resolveFactory(key.Scope);
        if (factory == null || key.NativeId >= factory.entityCursor ||
            factory.entityPool[key.NativeId].id != key.NativeId)
        {
            return false;
        }
        lock (gate)
        {
            var tracker = TrackerFor(key.Scope);
            if (!tracker.IsAlive(key.NativeId) || tracker.GenerationOf(key.NativeId) != key.Generation) return false;
        }
        StateReads++;
        var entityState = ReadEntityState(factory, key.NativeId);
        if (!entityState.HasCombatStat && !entityState.HasConstructStat)
        {
            // Most buildings have no damage records. Their immutable wire state is identical;
            // reuse it instead of allocating a writer and primitive buffers per building per tick.
            state = undamagedState;
            return true;
        }
        if (!FactoryCombatStateCodec.TryEncode(entityState, out state))
        {
            // The codec's fixed layout cannot exceed its own ceiling; refusal here is a defect.
            throw new System.InvalidOperationException("A factory combat state exceeded its own codec.");
        }
        return true;
    }

    private static byte[] EncodeUndamagedState()
    {
        FactoryCombatStateCodec.TryEncode(default, out var data);
        return data;
    }

    /// <summary>
    /// Reads one entity's combat and construct facts through their vanilla references, validating
    /// each reference before trusting what it points at.
    /// </summary>
    private FactoryCombatState ReadEntityState(PlanetFactory factory, int entityId)
    {
        ref var entity = ref factory.entityPool[entityId];
        var state = new FactoryCombatState();

        var combatStatId = entity.combatStatId;
        if (combatStatId > 0)
        {
            var stats = factory.skillSystem.combatStats;
            if (combatStatId < stats.cursor && stats.buffer[combatStatId].id == combatStatId &&
                stats.buffer[combatStatId].objectType == (int)EObjectType.Entity &&
                stats.buffer[combatStatId].objectId == entityId &&
                stats.buffer[combatStatId].astroId == factory.planet.astroId)
            {
                ref var stat = ref stats.buffer[combatStatId];
                state = new FactoryCombatState(true, stat.hp, stat.hpMax, stat.hpRecover, stat.hpIncoming,
                    state.HasConstructStat, state.DamageRate, state.RepairerCount, state.RepairerModuleId,
                    state.RepairerValue);
            }
            else
            {
                CorruptReferences++;
            }
        }

        var constructStatId = entity.constructStatId;
        if (constructStatId > 0)
        {
            var constructs = factory.constructionSystem.constructStats;
            if (constructStatId < constructs.cursor && constructs.buffer[constructStatId].id == constructStatId &&
                constructs.buffer[constructStatId].entityId == entityId)
            {
                ref var construct = ref constructs.buffer[constructStatId];
                state = new FactoryCombatState(state.HasCombatStat, state.Hp, state.HpMax, state.HpRecover,
                    state.HpIncoming, true, construct.damageRate, construct.repairerCount, construct.repairerModuleId,
                    construct.repairerValue);
            }
            else
            {
                CorruptReferences++;
            }
        }

        return state;
    }

    /// <summary>
    /// The authority identity of a living entity slot, as the replication scan would mint it.
    /// </summary>
    /// <remarks>
    /// The death capture calls this inside the vanilla death commit, where the slot is provably
    /// still occupied. Observing here keeps the tracker's generation in step with the scan: a dying
    /// object takes the identity clients were given, and the slot's next occupant gets the next
    /// generation, never the dead one's.
    /// </remarks>
    public bool TryGetEntityKey(int planetId, int entityId, out ObjectKey key)
    {
        key = default;
        var factory = resolveFactory(planetId);
        if (factory == null || entityId <= 0 || entityId >= factory.entityCursor ||
            factory.entityPool[entityId].id != entityId)
        {
            return false;
        }
        lock (gate)
        {
            var generation = TrackerFor(planetId).ObserveOccupied(entityId);
            key = ObjectKey.Create(epoch, PoolKind.Entity, planetId, entityId, generation);
        }
        return true;
    }

    public bool TryGetEntityKey(PlanetFactory factory, int entityId, out ObjectKey key)
    {
        key = default;
        return ReferenceEquals(resolveFactory(factory.planetId), factory) && TryGetEntityKey(factory.planetId, entityId, out key);
    }

    private static PlanetFactory ResolveFactory(int planetId)
    {
        var planet = GameMain.galaxy?.PlanetById(planetId);
        // A planet whose factory is not realized is unreadable, not empty: treating it as an empty
        // scope would publish every building's death.
        return planet?.factory;
    }

    private SlotGenerationTracker TrackerFor(int planetId)
    {
        if (!trackers.TryGetValue(planetId, out var tracker))
        {
            tracker = new SlotGenerationTracker();
            trackers.Add(planetId, tracker);
        }
        return tracker;
    }
}
