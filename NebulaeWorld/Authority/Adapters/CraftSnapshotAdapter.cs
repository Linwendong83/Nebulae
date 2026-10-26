#region

using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Combat;

#endregion

namespace NebulaeWorld.Authority.Adapters;

/// <summary>
/// The host's canonical view of player and base craft.
/// </summary>
/// <remarks>
/// <para>
/// Covers both <see cref="PoolKind.GroundCraft"/> (one planet scope per factory) and
/// <see cref="PoolKind.SpaceCraft"/> (sector scope 0, one pool shared by the whole sector). It reads
/// the craft pools and the global skill pool (for HP), and encodes each craft as a
/// <see cref="CraftState"/>. Motion inputs allow native client steering, and the shared combat loops
/// run locally for the craft the client owns; fleet orders, unit assembly, craft removal, hitpoint
/// writes, ammunition and destruction remain server-owned.
/// </para>
/// <para>
/// Reading never mutates: no combat-stat removal, no fleet execution, no destruction. A scope whose
/// factory (ground) or sector (space) is not loaded returns <c>false</c> (skip this scope this
/// frame), never an empty scope. Slot generations are minted per scope by
/// <see cref="SlotGenerationTracker"/>, so a destroyed and recycled slot is a new
/// <see cref="ObjectKey"/>, never a resurrection. Two players' craft never share a slot identity;
/// the owner is an attribute, not a second index.
/// </para>
/// </remarks>
internal sealed class CraftSnapshotAdapter : IHostWorldView, IHostIncrementalWorldView, IHostSnapshotWorldView
{
    private readonly AuthorityEpoch epoch;
    private readonly Dictionary<int, SlotGenerationTracker> groundTrackers = [];
    private readonly SlotGenerationTracker spaceTracker = new();
    private readonly Dictionary<ObjectKey, (CraftState State, byte[] Bytes, Control Inputs)> published = new();
    private readonly List<ObjectKey> retired = new();
    private readonly object gate = new();
    private readonly Dictionary<ScopeKey, Membership> membership = [];
    private readonly System.Func<int, PlanetFactory> resolveFactory;
    private readonly System.Func<SpaceSector> resolveSector;
    private readonly System.Func<long> clock;
    private record struct Control(int Component, int Target, int Astro, ETargetType Type,
        VectorLF3 Position, int Hatred, int Flags, int Assembly, int Charging);

    private Control ReadControl(in CraftData craft, bool space)
    {
        var ground = space ? null : resolveFactory(craft.astroId)?.combatGroundSystem;
        var system = space ? resolveSector()?.combatSpaceSystem : null;
        var fleets = space ? system?.fleets : ground?.fleets;
        var units = space ? system?.units : ground?.units;
        if (craft.fleetId > 0 && fleets != null && craft.fleetId < fleets.cursor)
        {
            ref var fleet = ref fleets.buffer[craft.fleetId];
            return new Control(fleet.id, fleet.target.id, fleet.target.astroId, fleet.target.type, fleet.targetPos,
                fleet.hatred.max.target, fleet.dispatch ? 1 : 0, fleet.currentAssembleUnitsCount, fleet.currentChargingUnitsCount);
        }
        if (craft.unitId > 0 && units != null && craft.unitId < units.cursor)
        {
            ref var unit = ref units.buffer[craft.unitId];
            return new Control(-unit.id, 0, 0, 0, default, unit.hatred.max.target,
                (int)unit.behavior | (unit.isRetreating ? 256 : 0) | (unit.isCharging ? 512 : 0), 0, 0);
        }
        return default;
    }

    private static void TrackMoving(Membership index, CraftData[] pool)
    {
        foreach (var key in index.Live.Values)
            index.Candidates.SetFast(key, pool[key.NativeId].fleetId > 0 || pool[key.NativeId].unitId > 0);
    }
    private sealed class Membership
    {
        public object Owner;
        public bool Initialized;
        public readonly Dictionary<int, ObjectKey> Live = [];
        public readonly ReplicationCandidateIndex Candidates = new();
    }

    public long UnreadableScopes { get; private set; }
    public long CorruptReferences { get; private set; }
    public long UnknownKinds { get; private set; }

    public CraftSnapshotAdapter(AuthorityEpoch epoch, System.Func<int, PlanetFactory> resolveFactory = null, System.Func<SpaceSector> resolveSector = null, System.Func<long> clock = null)
    {
        if (!epoch.IsValid) throw new System.ArgumentException("The adapter needs a valid world epoch.", nameof(epoch));
        this.epoch = epoch;
        this.resolveFactory = resolveFactory ?? (planet => GameMain.galaxy?.PlanetById(planet)?.factory);
        this.resolveSector = resolveSector ?? (() => GameMain.spaceSector);
        this.clock = clock ?? (() => GameMain.gameTick);
    }

    public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
    {
        lock (gate)
        {
            if (scope.Kind == PoolKind.GroundCraft) return TryReadGroundMembers(scope.Scope, members);
            if (scope.Kind == PoolKind.SpaceCraft && scope.Scope == AuthorityScope.Sector) return TryReadSpaceMembers(members);
            return false;
        }
    }

    /// <summary>The authority identity of a living planet-craft slot, as the replication scan would mint
    /// it.</summary>
    public bool TryGetGroundCraftKey(int planetId, int craftId, out ObjectKey key)
    {
        key = default;
        var factory = resolveFactory(planetId);
        if (factory == null || craftId <= 0 || craftId >= factory.craftCursor ||
            craftId >= factory.craftPool.Length ||
            factory.craftPool[craftId].id != craftId ||
            factory.craftPool[craftId].prototype == ECraftProto.ConstructionDrone)
        {
            return false;
        }
        var generation = TrackerFor(planetId).ObserveOccupied(craftId);
        key = ObjectKey.Create(epoch, PoolKind.GroundCraft, planetId, craftId, generation);
        return true;
    }

    /// <summary>The authority identity of a living sector-craft slot, as the replication scan would mint
    /// it.</summary>
    public bool TryGetSpaceCraftKey(int craftId, out ObjectKey key)
    {
        key = default;
        var sector = resolveSector();
        if (sector == null || craftId <= 0 || craftId >= sector.craftCursor ||
            craftId >= sector.craftPool.Length ||
            sector.craftPool[craftId].id != craftId)
        {
            return false;
        }
        var generation = spaceTracker.ObserveOccupied(craftId);
        key = ObjectKey.Create(epoch, PoolKind.SpaceCraft, AuthorityScope.Sector, craftId, generation);
        return true;
    }

    public bool TryReadState(ObjectKey key, out byte[] state)
    {
        state = null;
        if (!key.IsValid || key.Epoch != epoch || key.Kind is not (PoolKind.GroundCraft or PoolKind.SpaceCraft)) return false;
        var tracker = key.Kind == PoolKind.SpaceCraft ? spaceTracker : TrackerFor(key.Scope);
        if (tracker.GenerationOf(key.NativeId) != key.Generation) return false;
        if (key.Kind == PoolKind.GroundCraft)
        {
            return TryReadGroundState(key, out state);
        }
        if (key.Kind == PoolKind.SpaceCraft && key.Scope == AuthorityScope.Sector)
        {
            return TryReadSpaceState(key, out state);
        }
        return false;
    }

    private bool TryReadGroundMembers(int planetId, List<ObjectKey> members)
    {
        var factory = resolveFactory(planetId);
        if (factory == null)
        {
            UnreadableScopes++;
            return false;
        }
        var scope = new ScopeKey(PoolKind.GroundCraft, planetId);
        var index = Index(scope, factory);
        index.Live.Clear();
        var tracker = TrackerFor(planetId);
        var pool = factory.craftPool;
        for (var id = 1; id < factory.craftCursor; id++)
        {
            // Native construction owns these crafts, including client-local drone logic ids.
            // Replacing them with display-only authority shells would disconnect active drones.
            if (id >= pool.Length || pool[id].id != id ||
                pool[id].prototype == ECraftProto.ConstructionDrone) continue;
            var generation = tracker.ObserveOccupied(id);
            var key = ObjectKey.Create(epoch, PoolKind.GroundCraft, planetId, id, generation);
            members.Add(key); index.Live[id] = key;
        }
        tracker.EndScan();
        Prune(PoolKind.GroundCraft, planetId, tracker);
        index.Initialized = true; index.Candidates.Rebuild(factory.craftPool, clock(), members);
        TrackMoving(index, factory.craftPool);
        return true;
    }

    private bool TryReadSpaceMembers(List<ObjectKey> members)
    {
        var sector = resolveSector();
        if (sector == null)
        {
            UnreadableScopes++;
            return false;
        }
        var index = Index(new ScopeKey(PoolKind.SpaceCraft, AuthorityScope.Sector), sector);
        index.Live.Clear();
        var pool = sector.craftPool;
        for (var id = 1; id < sector.craftCursor; id++)
        {
            if (id >= pool.Length || pool[id].id != id) continue;
            var generation = spaceTracker.ObserveOccupied(id);
            var key = ObjectKey.Create(epoch, PoolKind.SpaceCraft, AuthorityScope.Sector, id, generation);
            members.Add(key); index.Live[id] = key;
        }
        spaceTracker.EndScan();
        Prune(PoolKind.SpaceCraft, 0, spaceTracker);
        index.Initialized = true; index.Candidates.Rebuild(sector.craftPool, clock(), members);
        TrackMoving(index, sector.craftPool);
        return true;
    }

    private bool TryReadGroundState(ObjectKey key, out byte[] state)
    {
        state = null;
        var factory = resolveFactory(key.Scope);
        if (factory == null || key.NativeId >= factory.craftCursor ||
            key.NativeId >= factory.craftPool.Length ||
            factory.craftPool[key.NativeId].id != key.NativeId)
        {
            return false;
        }
        if (!TryReadCraftState(in factory.craftPool[key.NativeId], false, factory.craftPool, out var decoded, false))
        {
            return false;
        }
        return Publish(key, decoded, out state, factory.craftPool, false);
    }

    private bool TryReadSpaceState(ObjectKey key, out byte[] state)
    {
        state = null;
        var sector = resolveSector();
        if (sector == null || key.NativeId >= sector.craftCursor ||
            key.NativeId >= sector.craftPool.Length ||
            sector.craftPool[key.NativeId].id != key.NativeId)
        {
            return false;
        }
        if (!TryReadCraftState(in sector.craftPool[key.NativeId], true, sector.craftPool, out var decoded, false))
        {
            return false;
        }
        return Publish(key, decoded, out state, sector.craftPool, true);
    }

    private bool Publish(ObjectKey key, in CraftState decoded, out byte[] state, CraftData[] pool, bool wantSpace)
    {
        // AddCraftData runs before native fleet/unit creation. Refresh cadence at the
        // first completed sample, when those component IDs actually exist.
        if (membership.TryGetValue(new ScopeKey(key.Kind, key.Scope), out var index))
            index.Candidates.SetFast(key, pool[key.NativeId].fleetId > 0 || pool[key.NativeId].unitId > 0);
        // A moving craft's pose is recomputed by the receiver from the captured native inputs, so it
        // keeps the checkpoint policy. Everything else is fully authoritative: publish it when, and
        // only when, a fact actually changed. Deciding that from the struct keeps the encoder out of
        // the common case, where the previous bytes are handed straight back and the replicator's
        // comparison short-circuits on reference equality.
        var inputs = ReadControl(pool[key.NativeId], wantSpace);
        var changed = true;
        if (published.TryGetValue(key, out var prior))
        {
            changed = decoded.IsDynamic
                ? inputs != prior.Inputs || CraftStatePolicy.ShouldPublish(prior.State, decoded)
                : !CraftStatePolicy.SameState(prior.State, decoded);
            if (!changed) { state = prior.Bytes; return true; }
        }
        // The native component is an allocation, so the caller read without it. Only a record that is
        // genuinely about to be published pays for the second read that attaches it.
        var record = decoded;
        if (changed)
        {
            if (!TryReadCraftState(in pool[key.NativeId], wantSpace, pool, out record, true))
            {
                state = null;
                return false;
            }
        }
        if (!CraftStateCodec.TryEncode(record, out state))
        {
            CorruptReferences++;
            return false;
        }
        published[key] = (record, state, inputs);
        return true;
    }

    public bool TryReadSnapshotState(ObjectKey key, out byte[] state)
    {
        published.Remove(key);
        return TryReadState(key, out state);
    }

    private void Prune(PoolKind kind, int scope, SlotGenerationTracker tracker)
    {
        retired.Clear();
        foreach (var key in published.Keys)
            if (key.Kind == kind && key.Scope == scope && (!tracker.IsAlive(key.NativeId) || tracker.GenerationOf(key.NativeId) != key.Generation)) retired.Add(key);
        foreach (var key in retired) published.Remove(key);
    }

    /// <summary>
    /// Reads one craft's canonical record. <paramref name="withNative"/> appends the encoded native
    /// fleet/unit component, which is the only allocating part of the reading; callers that are still
    /// deciding whether the record changed pass <c>false</c> and re-read only when publishing.
    /// </summary>
    private bool TryReadCraftState(in CraftData craft, bool wantSpace, CraftData[] pool, out CraftState state, bool withNative)
    {
        state = default;
        if (craft.isSpace != wantSpace)
        {
            UnknownKinds++;
            return false;
        }
        var hasCombatStat = false;
        int hp = 0, hpMax = 0, hpRecover = 0, hpIncoming = 0;
        var combatStatId = craft.combatStatId;
        if (combatStatId > 0)
        {
            var stats = GameMain.data.spaceSector.skillSystem.combatStats;
            if (combatStatId < stats.cursor && stats.buffer[combatStatId].id == combatStatId &&
                stats.buffer[combatStatId].objectType == (int)EObjectType.Craft &&
                stats.buffer[combatStatId].objectId == craft.id)
            {
                ref var stat = ref stats.buffer[combatStatId];
                hasCombatStat = true;
                hp = stat.hp;
                hpMax = stat.hpMax;
                hpRecover = stat.hpRecover;
                hpIncoming = stat.hpIncoming;
            }
            else
            {
                CorruptReferences++;
            }
        }
        var tick = craft.dynamic ? Multiplayer.Session?.AuthorityRuntime?.CompletedSimulationTick ?? GameMain.gameTick : 0;
        var scope = wantSpace ? 0 : craft.astroId;
        var ground = wantSpace ? null : resolveFactory(scope)?.combatGroundSystem;
        var space = wantSpace ? resolveSector()?.combatSpaceSystem : null;
        var fleets = wantSpace ? space?.fleets : ground?.fleets;
        var units = wantSpace ? space?.units : ground?.units;
        byte[] fleet = null, unit = null;
        if (withNative)
        {
            if (craft.fleetId > 0 && fleets != null && craft.fleetId < fleets.cursor && fleets.buffer[craft.fleetId].id == craft.fleetId)
                fleet = CraftNativeStateCodec.Encode(fleets.buffer[craft.fleetId]);
            if (craft.unitId > 0 && units != null && craft.unitId < units.cursor && units.buffer[craft.unitId].id == craft.unitId)
                unit = CraftNativeStateCodec.Encode(units.buffer[craft.unitId]);
        }
        var ownerMecha = Multiplayer.Session?.CombatAuthority?.CraftMecha(wantSpace, scope, craft.id);
        ushort playerOwner = 0;
        if (ownerMecha != null) playerOwner = (ushort)CombatTargetContext.PlayerIdFor(ownerMecha);
        else if (Multiplayer.Session is { IsServer: true, IsDedicated: false } session)
        {
            var root = craft.owner > 0 && craft.owner < pool.Length ? pool[craft.owner] : craft;
            var module = wantSpace ? GameMain.mainPlayer?.mecha?.spaceCombatModule : GameMain.mainPlayer?.mecha?.groundCombatModule;
            if (root.owner == -1 && module != null && root.port >= 0 && root.port < module.fleetCount &&
                module.moduleFleets[root.port].fleetId == root.fleetId) playerOwner = session.LocalPlayer.Id;
        }
        state = new CraftState(hasCombatStat, craft.dynamic, craft.isSpace,
            craft.protoId, craft.modelIndex, craft.port, (byte)craft.prototype, craft.stateFlags,
            craft.astroId, craft.owner,
            craft.pos.x, craft.pos.y, craft.pos.z,
            craft.rot.x, craft.rot.y, craft.rot.z, craft.rot.w,
            craft.dynamic ? craft.vel.x : 0f, craft.dynamic ? craft.vel.y : 0f, craft.dynamic ? craft.vel.z : 0f,
            hp, hpMax, hpRecover, hpIncoming, tick, playerOwner, fleet, unit);
        return true;
    }

    private SlotGenerationTracker TrackerFor(int planetId)
    {
        lock (gate)
        {
            if (!groundTrackers.TryGetValue(planetId, out var tracker))
            {
                tracker = new SlotGenerationTracker();
                groundTrackers.Add(planetId, tracker);
            }
            return tracker;
        }
    }

    private Membership Index(ScopeKey scope, object owner)
    {
        if (!membership.TryGetValue(scope, out var index)) membership[scope] = index = new Membership();
        if (ReferenceEquals(index.Owner, owner)) return index;
        if (index.Owner != null)
        {
            var tracker = scope.Kind == PoolKind.SpaceCraft ? spaceTracker : TrackerFor(scope.Scope);
            foreach (var old in index.Live.Values) { tracker.Retire(old.NativeId); published.Remove(old); }
            index.Live.Clear();
        }
        index.Owner = owner; index.Initialized = false;
        return index;
    }
    public bool TryReadChanges(ScopeKey scope, long hostTick, List<ObjectKey> candidates, List<ObjectKey> removed)
    {
        if (scope.Kind is not (PoolKind.GroundCraft or PoolKind.SpaceCraft) || scope.Kind == PoolKind.SpaceCraft && scope.Scope != AuthorityScope.Sector) return false;
        object owner = scope.Kind == PoolKind.GroundCraft ? resolveFactory(scope.Scope) : resolveSector();
        if (owner == null) return false;
        lock (gate)
        {
            var index = Index(scope, owner);
            var pool = owner is PlanetFactory factory ? factory.craftPool : ((SpaceSector)owner).craftPool;
            if (!index.Initialized || !index.Candidates.Read(pool, index.Live.Count, hostTick, candidates, removed)) return false;
            candidates.Sort((left, right) => left.NativeId.CompareTo(right.NativeId));
            return true;
        }
    }
    public void StateSampled(in ObjectKey key)
    {
        lock (gate)
            if (membership.TryGetValue(new ScopeKey(key.Kind, key.Scope), out var index)) index.Candidates.Sampled(key);
    }
    public void MarkState(bool space, int planet, int id)
    {
        lock (gate)
            if (membership.TryGetValue(new ScopeKey(space ? PoolKind.SpaceCraft : PoolKind.GroundCraft, space ? 0 : planet), out var index))
                index.Candidates.Changed(id);
    }
    public void Added(PlanetFactory factory, int id)
    {
        if (factory == null || !ReferenceEquals(resolveFactory(factory.planetId), factory) || id <= 0 || id >= factory.craftCursor || id >= factory.craftPool.Length ||
            factory.craftPool[id].id != id || factory.craftPool[id].prototype == ECraftProto.ConstructionDrone) return;
        lock (gate)
        {
            var index = Index(new ScopeKey(PoolKind.GroundCraft, factory.planetId), factory);
            index.Live[id] = ObjectKey.Create(epoch, PoolKind.GroundCraft, factory.planetId, id, TrackerFor(factory.planetId).ObserveOccupied(id));
            index.Candidates.Add(index.Live[id]);
            index.Candidates.SetFast(index.Live[id], factory.craftPool[id].fleetId > 0 || factory.craftPool[id].unitId > 0);
        }
    }
    public void Added(SpaceSector sector, int id)
    {
        if (sector == null || !ReferenceEquals(resolveSector(), sector) || id <= 0 || id >= sector.craftCursor || id >= sector.craftPool.Length || sector.craftPool[id].id != id) return;
        lock (gate)
        {
            var index = Index(new ScopeKey(PoolKind.SpaceCraft, AuthorityScope.Sector), sector);
            index.Live[id] = ObjectKey.Create(epoch, PoolKind.SpaceCraft, AuthorityScope.Sector, id, spaceTracker.ObserveOccupied(id));
            index.Candidates.Add(index.Live[id]);
            index.Candidates.SetFast(index.Live[id], sector.craftPool[id].fleetId > 0 || sector.craftPool[id].unitId > 0);
        }
    }
    public void Removed(PlanetFactory factory, int id)
    {
        if (factory == null || !ReferenceEquals(resolveFactory(factory.planetId), factory)) return;
        Remove(new ScopeKey(PoolKind.GroundCraft, factory.planetId), factory, id);
    }
    public void Removed(SpaceSector sector, int id)
    {
        if (sector == null || !ReferenceEquals(resolveSector(), sector)) return;
        Remove(new ScopeKey(PoolKind.SpaceCraft, AuthorityScope.Sector), sector, id);
    }
    private void Remove(ScopeKey scope, object owner, int id)
    {
        if (id <= 0) return;
        lock (gate)
        {
            var index = Index(scope, owner);
            if (index.Live.TryGetValue(id, out var old)) { index.Live.Remove(id); index.Candidates.Remove(old); published.Remove(old); }
            (scope.Kind == PoolKind.SpaceCraft ? spaceTracker : TrackerFor(scope.Scope)).Retire(id);
        }
    }
}
