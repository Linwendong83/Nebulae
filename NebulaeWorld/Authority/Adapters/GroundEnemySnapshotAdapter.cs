#region

using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Combat;

#endregion

namespace NebulaeWorld.Authority.Adapters;

/// <summary>
/// The host's canonical view of one planet's ground enemies.
/// </summary>
/// <remarks>
/// <para>
/// Covers the <see cref="PoolKind.GroundEnemy"/> pool of each loaded factory. It reads the enemy pool, the
/// global skill pool (for HP) and the ground system's component ids (for kind/linkage), and encodes
/// them as a <see cref="GroundEnemyState"/> per enemy. <see cref="GroundEnemyKind"/> identifies its
/// component type in the payload.
/// </para>
/// <para>
/// Reading never mutates: no <c>HandleFullHp/HandleZeroHp</c>, no stat removal, no activation. A
/// planet whose factory is not loaded returns <c>false</c> (skip this scope this frame), never an
/// empty scope — an empty scope would publish every enemy's death. Slot generations are minted per
/// scope by <see cref="SlotGenerationTracker"/>, so a killed and recycled slot is a new
/// <see cref="ObjectKey"/>, never a resurrection.
/// </para>
/// <para>
/// Members are reported base cores first, then dependents, so a subscriber that applies spawns in
/// log order sees the parent identity before the child's state (the inversion rule is still enforced
/// client-side by deferral, but ordering makes the common case converge in one frame).
/// </para>
/// </remarks>
internal sealed class GroundEnemySnapshotAdapter : IHostWorldView, IHostSnapshotWorldView, IHostIncrementalWorldView
{
    private readonly AuthorityEpoch epoch;
    private readonly Dictionary<int, SlotGenerationTracker> trackers = [];
    private readonly object phaseGate = new();
    private readonly Dictionary<(int Planet, int Slot), bool> corePhases = [];
    private readonly Dictionary<ObjectKey, (GroundEnemyState State, byte[] Bytes)> published = new();
    private readonly List<ObjectKey> retired = new();
    private readonly List<int> baseFirst = new(), rest = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, ReplicationCandidateIndex> candidates = new();

    public long UnreadableScopes { get; private set; }
    public long CorruptReferences { get; private set; }
    public long UnknownKinds { get; private set; }

    public GroundEnemySnapshotAdapter(AuthorityEpoch epoch)
    {
        if (!epoch.IsValid) throw new System.ArgumentException("The adapter needs a valid world epoch.", nameof(epoch));
        this.epoch = epoch;
    }

    public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
    {
        if (scope.Kind != PoolKind.GroundEnemy)
        {
            return false;
        }
        var factory = FactoryFor(scope.Scope);
        if (factory == null)
        {
            UnreadableScopes++;
            return false;
        }
        var tracker = TrackerFor(scope.Scope);
        var pool = factory.enemyPool;
        baseFirst.Clear(); rest.Clear();
        for (var id = 1; id < factory.enemyCursor; id++)
        {
            if (id >= pool.Length || pool[id].id != id) continue;
            if (pool[id].dfGBaseId > 0) baseFirst.Add(id);
            else rest.Add(id);
        }
        foreach (var id in baseFirst)
        {
            var generation = Observe(factory, id);
            members.Add(ObjectKey.Create(epoch, PoolKind.GroundEnemy, scope.Scope, id, generation));
        }
        foreach (var id in rest)
        {
            var generation = Observe(factory, id);
            members.Add(ObjectKey.Create(epoch, PoolKind.GroundEnemy, scope.Scope, id, generation));
        }
        tracker.EndScan();
        retired.Clear();
        foreach (var key in published.Keys)
            if (key.Scope == scope.Scope && (!tracker.IsAlive(key.NativeId) || tracker.GenerationOf(key.NativeId) != key.Generation)) retired.Add(key);
        foreach (var key in retired) published.Remove(key);
        Index(scope.Scope).Rebuild(factory.enemyPool, GameMain.gameTick, members);
        foreach (var key in members)
        {
            var enemy = factory.enemyPool[key.NativeId];
            Index(scope.Scope).SetCadence(key, enemy.dynamic && enemy.unitId == 0, enemy.dynamic && enemy.unitId > 0);
        }
        return true;
    }

    private ReplicationCandidateIndex Index(int planet)
        => candidates.GetOrAdd(planet, static _ => new ReplicationCandidateIndex());
    public bool TryReadChanges(ScopeKey scope, long hostTick, List<ObjectKey> changed, List<ObjectKey> removed)
    {
        if (scope.Kind != PoolKind.GroundEnemy) return false;
        var factory = FactoryFor(scope.Scope);
        return factory != null && Index(scope.Scope).Read(factory.enemyPool, factory.enemyCount, hostTick, changed, removed);
    }
    public void StateSampled(in ObjectKey key)
    {
        if (key.Kind == PoolKind.GroundEnemy) Index(key.Scope).Sampled(key);
    }
    public void Added(PlanetFactory factory, int id)
    {
        lock (phaseGate)
        {
            if (TryGetEnemyKey(factory.planetId, id, out var key))
            {
                Index(factory.planetId).Add(key);
                Index(factory.planetId).SetFast(key, factory.enemyPool[id].dynamic && factory.enemyPool[id].unitId == 0);
            }
        }
    }
    public void Removed(ObjectKey key)
    {
        lock (phaseGate)
        {
            TrackerFor(key.Scope).Retire(key.NativeId); Index(key.Scope).Remove(key); published.Remove(key);
        }
    }
    public void MarkState(int planet, int id)
    {
        lock (phaseGate)
        {
            if (!TryGetEnemyKey(planet, id, out var key)) return;
            Index(planet).Add(key);
            var enemy = FactoryFor(planet).enemyPool[id];
            Index(planet).SetFast(key, enemy.dynamic && enemy.unitId == 0);
        }
    }

    public bool TryReadState(ObjectKey key, out byte[] state)
    {
        state = null;
        if (key.Kind != PoolKind.GroundEnemy || !key.Epoch.Equals(epoch) || TrackerFor(key.Scope).GenerationOf(key.NativeId) != key.Generation) return false;
        var factory = FactoryFor(key.Scope);
        if (factory == null || key.NativeId >= factory.enemyCursor ||
            key.NativeId >= factory.enemyPool.Length ||
            factory.enemyPool[key.NativeId].id != key.NativeId)
        {
            return false;
        }
        // Capture all native inputs once at the parked-worker boundary; encode only if publishing.
        if (!TryReadEnemyState(factory, key.NativeId, out var decoded, out var capturedUnit))
        {
            return false;
        }
        var movingUnit = decoded.Kind == GroundEnemyKind.GroundUnit && decoded.IsDynamic;
        // Creation can precede native component binding; refresh the scheduling lane
        // from the decoded identity as soon as that component becomes available.
        Index(key.Scope).SetCadence(key, decoded.IsDynamic && !movingUnit, movingUnit);
        // The gate answers "did a fact move", and only an unchanged reading is answered from the
        // cache: the replicator compares what it gets here against what it already sent, so handing
        // back the previous bytes for a record that moved is indistinguishable from publishing
        // nothing. A building's hitpoints and a base's evolve level are settled facts, not motion,
        // so they travel the SameState branch — see EnemyStatePolicy.HasMoved.
        if (published.TryGetValue(key, out var previous) && !EnemyStatePolicy.HasMoved(previous.State, decoded, movingUnit))
        { state = previous.Bytes; return true; }
        if (!GroundEnemyStateCodec.TryEncodeCaptured(decoded, capturedUnit, out state, out var canonical))
        {
            CorruptReferences++;
            return false;
        }
        published[key] = (canonical, state);
        return true;
    }

    /// <summary>
    /// Captures one enemy and its native unit together, before allocating any wire buffers.
    /// </summary>
    private bool TryReadEnemyState(PlanetFactory factory, int enemyId, out GroundEnemyState state, out EnemyUnitComponent capturedUnit)
    {
        state = default; capturedUnit = default;
        ref var enemy = ref factory.enemyPool[enemyId];
        var kind = GroundEnemyKinds.FromComponentIds(enemy.dfGBaseId, enemy.unitId, enemy.builderId,
            enemy.dfGTurretId, enemy.dfGShieldId, enemy.dfGConnectorId, enemy.dfGReplicatorId);
        if (kind == GroundEnemyKind.Unknown)
        {
            UnknownKinds++;
            return false;
        }
        var hasCombatStat = false;
        int hp = 0, hpMax = 0, hpRecover = 0, hpIncoming = 0;
        var combatStatId = enemy.combatStatId;
        if (combatStatId > 0)
        {
            var stats = GameMain.data.spaceSector.skillSystem.combatStats;
            if (combatStatId < stats.cursor && stats.buffer[combatStatId].id == combatStatId &&
                stats.buffer[combatStatId].objectType == (int)EObjectType.Enemy &&
                stats.buffer[combatStatId].objectId == enemyId &&
                stats.buffer[combatStatId].astroId == factory.planetId)
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
                Log.Warn($"[authority] ground enemy {enemyId} on {factory.planet.name} has a dangling combatStatId {combatStatId}");
            }
        }
        var baseId = ResolveBaseId(factory, in enemy);
        var builderIndex = ResolveBuilderIndex(factory, in enemy);
        var level = ResolveLevel(factory, in enemy, kind);
        var animation = factory.enemyAnimPool[enemyId];
        var presentationBuilder = default(EnemyBuilderComponent);
        // Remote/headless factories do not run local animation updates. Their builder is the fact,
        // including a degenerate spMax of 0: the client panel must see the honest 0/0 (which it
        // renders as 0.0%) instead of falling back to a stale local animation and a zeroed state.
        if (enemy.builderId > 0 && enemy.builderId < factory.enemySystem.builders.cursor)
        {
            ref var builder = ref factory.enemySystem.builders.buffer[enemy.builderId];
            if (builder.id == enemy.builderId && builder.enemyId == enemyId)
            {
                presentationBuilder = builder;
                animation.time = enemy.dfGBaseId > 0 && builder.sp == 0 ? 0 : builder.groundBuildingAnimTime;
                animation.state = (uint)builder.state;
                animation.power = builder.state > 1 ? 1 : 0;
            }
        }
        var productSp = 0;
        var productSpMax = 0;
        if (enemy.dfGReplicatorId > 0 && enemy.dfGReplicatorId < factory.enemySystem.replicators.cursor)
        {
            ref var replicator = ref factory.enemySystem.replicators.buffer[enemy.dfGReplicatorId];
            if (replicator.id == enemy.dfGReplicatorId)
            {
                productSp = replicator.productSp;
                productSpMax = replicator.productSpMax;
            }
        }
        // A turret's aim lives in its own component, which the host's turret AI keeps fresh for every
        // factory. Vanilla only copies it into the animation pool while the planet is locally loaded,
        // so the pool value is stale — or never written at all — for a host that simulates this planet
        // without rendering it, and a client shell (no turret component) would show a frozen barrel.
        // Resolving it from the component after the builder override keeps builders untouched.
        byte[] nativeTurret = null;
        var turretState = 0;
        var turretInRange = false;
        var turretCounterAttack = false;
        if (enemy.dfGTurretId > 0 && enemy.dfGTurretId < factory.enemySystem.turrets.cursor)
        {
            ref var turret = ref factory.enemySystem.turrets.buffer[enemy.dfGTurretId];
            if (turret.id == enemy.dfGTurretId &&
                EnemyTurretAnimation.BuilderIsActive(factory.enemySystem.builders, turret.builderId))
            {
                animation = EnemyTurretAnimation.Resolve(turret.localDir, turret.state, turret.fire, turret.heat,
                    turret.sensorRange, turret.attackRange, EnemyTurretAnimation.AttackInterval(enemy.modelIndex));
                turretState = (int)turret.state;
                nativeTurret = NebulaeWorld.Combat.NativeDarkFogTurrets.Export(turret);
                turretInRange = turret.targetInShootRange;
                var bases = factory.enemySystem.bases;
                var component = turret.baseId > 0 && turret.baseId < bases.cursor ? bases.buffer[turret.baseId] : null;
                turretCounterAttack = turret.state == EDFTurretState.Aiming && NebulaeWorld.Combat.GroundTurretCombat.IsCounterAttack(factory, component, turret.target);
            }
        }
        var unit = enemy.unitId > 0 && enemy.unitId < factory.enemySystem.units.cursor &&
            factory.enemySystem.units.buffer[enemy.unitId].id == enemy.unitId ? factory.enemySystem.units.buffer[enemy.unitId] : default;
        if (kind == GroundEnemyKind.GroundUnit && (unit.id == 0 || unit.enemyId != enemyId))
        { CorruptReferences++; return false; }
        capturedUnit = unit;
        state = new GroundEnemyState(kind, hasCombatStat, enemy.dynamic,
            enemy.protoId, enemy.modelIndex, enemy.owner, enemy.port, enemy.stateFlags,
            enemy.astroId, enemy.originAstroId, baseId, builderIndex, level,
            (float)enemy.pos.x, (float)enemy.pos.y, (float)enemy.pos.z,
            enemy.rot.x, enemy.rot.y, enemy.rot.z, enemy.rot.w,
            enemy.dynamic ? enemy.vel.x : 0f, enemy.dynamic ? enemy.vel.y : 0f, enemy.dynamic ? enemy.vel.z : 0f,
            hp, hpMax, hpRecover, hpIncoming,
            animation.time, animation.prepare_length, animation.working_length, animation.state, animation.power,
            unit.anim, unit.disturbValue, unit.steering, unit.speed,
            presentationBuilder.sp, presentationBuilder.spMax, presentationBuilder.matter, presentationBuilder.maxMatter,
            presentationBuilder.energy, presentationBuilder.maxEnergy,
            presentationBuilder.sp < presentationBuilder.spMax ? -presentationBuilder.spMatter : presentationBuilder.genMatter,
            presentationBuilder.genEnergy - (presentationBuilder.state < 4 ? presentationBuilder.idleEnergy : presentationBuilder.workEnergy),
            productSp, productSpMax, (byte)unit.behavior, (byte)turretState, turretInRange,
            kind == GroundEnemyKind.GroundUnit && enemy.dynamic ? Multiplayer.Session?.AuthorityRuntime?.CompletedSimulationTick ?? GameMain.gameTick : 0,
            unit.stateTick,
            null, builderState: presentationBuilder.state,
            nativeTurret: nativeTurret, turretCounterAttack: turretCounterAttack);
        return true;
    }

    public bool TryReadSnapshotState(ObjectKey key, out byte[] state)
    {
        published.Remove(key);
        return TryReadState(key, out state);
    }

    private static int ResolveBaseId(PlanetFactory factory, in EnemyData enemy)
    {
        if (enemy.dfGBaseId > 0) return enemy.dfGBaseId;
        if (enemy.unitId > 0)
        {
            var units = factory.enemySystem.units;
            if (enemy.unitId < units.cursor &&
                units.buffer[enemy.unitId].id == enemy.unitId)
            {
                return units.buffer[enemy.unitId].baseId;
            }
        }
        return enemy.owner;
    }

    private static int ResolveBuilderIndex(PlanetFactory factory, in EnemyData enemy)
    {
        if (enemy.builderId <= 0) return 0;
        var builders = factory.enemySystem.builders;
        if (enemy.builderId < builders.cursor &&
            builders.buffer[enemy.builderId].id == enemy.builderId)
        {
            return builders.buffer[enemy.builderId].builderIndex;
        }
        return 0;
    }

    private static int ResolveLevel(PlanetFactory factory, in EnemyData enemy, GroundEnemyKind kind)
    {
        try
        {
            if (kind == GroundEnemyKind.GroundUnit && enemy.unitId > 0)
            {
                var units = factory.enemySystem.units;
                if (enemy.unitId < units.cursor &&
                    units.buffer[enemy.unitId].id == enemy.unitId)
                {
                    return units.buffer[enemy.unitId].level;
                }
            }
            if (kind == GroundEnemyKind.GroundBase && enemy.dfGBaseId > 0)
            {
                var bases = factory.enemySystem.bases;
                if (enemy.dfGBaseId < bases.cursor && bases.buffer[enemy.dfGBaseId] != null &&
                    bases.buffer[enemy.dfGBaseId].id == enemy.dfGBaseId)
                {
                    return bases.buffer[enemy.dfGBaseId].evolve.level;
                }
            }
        }
        catch (System.Exception e)
        {
            Log.Warn("[authority] ground enemy level read failed: " + e.Message);
        }
        return 0;
    }

    /// <summary>The authority identity of a living enemy slot, as the replication scan would mint it.</summary>
    /// <seealso cref="TryReadMembers"/>
    public bool TryGetEnemyKey(int planetId, int enemyId, out ObjectKey key)
    {
        key = default;
        var factory = FactoryFor(planetId);
        if (factory == null || enemyId <= 0 || enemyId >= factory.enemyCursor ||
            enemyId >= factory.enemyPool.Length ||
            factory.enemyPool[enemyId].id != enemyId)
        {
            return false;
        }
        var generation = Observe(factory, enemyId);
        key = ObjectKey.Create(epoch, PoolKind.GroundEnemy, planetId, enemyId, generation);
        return true;
    }

    private long Observe(PlanetFactory factory, int id)
    {
        lock (phaseGate)
        {
            var tracker = TrackerFor(factory.planetId);
            ref var enemy = ref factory.enemyPool[id];
            if (enemy.dfGBaseId > 0)
            {
                var slot = (factory.planetId, id);
                if (corePhases.TryGetValue(slot, out var wasDestroyed) && wasDestroyed && !enemy.isInvincible)
                    tracker.Retire(id);
                corePhases[slot] = enemy.isInvincible;
            }
            return tracker.ObserveOccupied(id);
        }
    }

    private static PlanetFactory FactoryFor(int planetId)
    {
        var planet = GameMain.galaxy?.PlanetById(planetId);
        return planet?.factory;
    }

    private SlotGenerationTracker TrackerFor(int planetId)
    {
        lock (phaseGate)
        {
            if (!trackers.TryGetValue(planetId, out var tracker))
            {
                tracker = new SlotGenerationTracker();
                trackers.Add(planetId, tracker);
            }
            return tracker;
        }
    }
}
