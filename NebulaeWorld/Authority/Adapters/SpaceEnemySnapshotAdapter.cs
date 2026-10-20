#region

using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Combat;

#endregion

namespace NebulaeWorld.Authority.Adapters;

/// <summary>
/// The host's canonical view of the sector's space enemies.
/// </summary>
/// <remarks>
/// <para>
/// Covers the <see cref="PoolKind.SpaceEnemy"/> pool of the sector (scope 0, one pool shared by
/// every hive). It reads the sector enemy pool, the global skill pool (for HP) and the hive
/// system's component ids (for kind/linkage), and encodes them as a <see cref="SpaceEnemyState"/>
/// per enemy. <see cref="SpaceEnemyKind"/> identifies its component type in the payload.
/// </para>
/// <para>
/// Reading never mutates: no <c>HandleFullHp/HandleZeroHp</c>, no stat removal, no activation. A
/// missing sector returns <c>false</c> (skip this scope this frame), never an empty scope — an
/// empty scope would publish every space enemy's death. Slot generations are minted by
/// <see cref="SlotGenerationTracker"/>, so a killed and recycled slot is a new
/// <see cref="ObjectKey"/>, never a resurrection. Cross-astro migration keeps the same key: the
/// sector scope is 0 and the astro ids are attributes.
/// </para>
/// <para>
/// Members are reported cores first, then the rest, so a subscriber that applies spawns in log
/// order sees hive structure before its units. A relay that lands and becomes a ground base is a
/// different pool (GroundEnemy) under a new key — the space key despawns and the ground key spawns
/// — so this adapter never reinterprets one pool's slot as another's.
/// </para>
/// </remarks>
internal sealed class SpaceEnemySnapshotAdapter : IHostWorldView, IHostSnapshotWorldView, IHostIncrementalWorldView
{
    private readonly AuthorityEpoch epoch;
    private readonly SlotGenerationTracker tracker = new();
    private readonly Dictionary<ObjectKey, (SpaceEnemyState State, byte[] Bytes)> published = new();
    private readonly List<ObjectKey> retired = new();
    private readonly List<int> coresFirst = new(), rest = new();
    private readonly ReplicationCandidateIndex candidates = new();
    private readonly object gate = new();

    public long UnreadableScopes { get; private set; }
    public long CorruptReferences { get; private set; }
    public long UnknownKinds { get; private set; }

    public SpaceEnemySnapshotAdapter(AuthorityEpoch epoch)
    {
        if (!epoch.IsValid) throw new System.ArgumentException("The adapter needs a valid world epoch.", nameof(epoch));
        this.epoch = epoch;
    }

    public ReplicationInterest InterestFor(ObjectKey key)
    {
        if (key.Kind != PoolKind.SpaceEnemy) return default;
        var sector = GameMain.spaceSector;
        if (sector == null || key.NativeId <= 0 || key.NativeId >= sector.enemyCursor ||
            sector.enemyPool[key.NativeId].id != key.NativeId || tracker.GenerationOf(key.NativeId) != key.Generation)
            return ReplicationInterest.SpaceEnemy(SpaceEnemyKind.Unknown, 0);
        ref var enemy = ref sector.enemyPool[key.NativeId];
        var kind = SpaceEnemyKinds.FromComponentIds(enemy.dfSCoreId, enemy.dfSNodeId,
            enemy.dfSConnectorId, enemy.dfSReplicatorId, enemy.dfSGammaId, enemy.dfSTurretId,
            enemy.dfTinderId, enemy.dfRelayId, enemy.unitId, enemy.builderId);
        return ReplicationInterest.SpaceEnemy(kind, sector.GetHiveByAstroId(enemy.originAstroId)?.starData.id ?? 0);
    }

    public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
    {
        if (scope.Kind != PoolKind.SpaceEnemy || scope.Scope != AuthorityScope.Sector)
        {
            return false;
        }
        var sector = GameMain.spaceSector;
        if (sector == null)
        {
            UnreadableScopes++;
            return false;
        }
        var pool = sector.enemyPool;
        coresFirst.Clear(); rest.Clear();
        for (var id = 1; id < sector.enemyCursor; id++)
        {
            if (id >= pool.Length || pool[id].id != id) continue;
            if (pool[id].dfSCoreId > 0) coresFirst.Add(id);
            else rest.Add(id);
        }
        foreach (var id in coresFirst)
        {
            var generation = tracker.ObserveOccupied(id);
            members.Add(ObjectKey.Create(epoch, PoolKind.SpaceEnemy, AuthorityScope.Sector, id, generation));
        }
        foreach (var id in rest)
        {
            var generation = tracker.ObserveOccupied(id);
            members.Add(ObjectKey.Create(epoch, PoolKind.SpaceEnemy, AuthorityScope.Sector, id, generation));
        }
        tracker.EndScan();
        retired.Clear();
        foreach (var key in published.Keys)
            if (!tracker.IsAlive(key.NativeId) || tracker.GenerationOf(key.NativeId) != key.Generation) retired.Add(key);
        foreach (var key in retired) published.Remove(key);
        candidates.Rebuild(sector.enemyPool, GameMain.gameTick, members);
        foreach (var key in members)
        {
            var enemy = sector.enemyPool[key.NativeId];
            candidates.SetCadence(key, enemy.dynamic && enemy.unitId == 0, enemy.dynamic && enemy.unitId > 0);
        }
        return true;
    }

    public bool TryReadChanges(ScopeKey scope, long hostTick, List<ObjectKey> changed, List<ObjectKey> removed) =>
        scope.Kind == PoolKind.SpaceEnemy && GameMain.spaceSector is { } sector &&
        candidates.Read(sector.enemyPool, sector.enemyCount, hostTick, changed, removed);
    public void StateSampled(in ObjectKey key)
    {
        if (key.Kind == PoolKind.SpaceEnemy) candidates.Sampled(key);
    }
    public void Added(int id)
    {
        lock (gate)
        {
            if (!TryGetEnemyKey(id, out var key)) return;
            candidates.Add(key);
            var enemy = GameMain.spaceSector.enemyPool[id];
            candidates.SetFast(key, enemy.dynamic && enemy.unitId == 0);
        }
    }
    public void Removed(ObjectKey key)
    {
        lock (gate)
        {
            tracker.Retire(key.NativeId); candidates.Remove(key); published.Remove(key);
        }
    }
    public void MarkState(int id) => candidates.Changed(id);

    /// <summary>The authority identity of a living sector-enemy slot, as the replication scan would mint
    /// it.</summary>
    public bool TryGetEnemyKey(int enemyId, out ObjectKey key)
    {
        key = default;
        var sector = GameMain.spaceSector;
        if (sector == null || enemyId <= 0 || enemyId >= sector.enemyCursor ||
            enemyId >= sector.enemyPool.Length ||
            sector.enemyPool[enemyId].id != enemyId)
        {
            return false;
        }
        var generation = tracker.ObserveOccupied(enemyId);
        key = ObjectKey.Create(epoch, PoolKind.SpaceEnemy, AuthorityScope.Sector, enemyId, generation);
        return true;
    }

    public bool TryReadState(ObjectKey key, out byte[] state)
    {
        state = null;
        if (!key.IsValid || key.Epoch != epoch || key.Kind != PoolKind.SpaceEnemy || key.Scope != AuthorityScope.Sector ||
            tracker.GenerationOf(key.NativeId) != key.Generation) return false;
        var sector = GameMain.spaceSector;
        if (sector == null || key.NativeId >= sector.enemyCursor ||
            key.NativeId >= sector.enemyPool.Length ||
            sector.enemyPool[key.NativeId].id != key.NativeId)
        {
            return false;
        }
        // Capture all native inputs once at the parked-worker boundary; encode only if publishing.
        if (!TryReadEnemyState(sector, key.NativeId, out var decoded, out var capturedUnit))
        {
            return false;
        }
        var movingUnit = decoded.Kind == SpaceEnemyKind.SpaceUnit && decoded.IsDynamic;
        candidates.SetCadence(key, decoded.IsDynamic && !movingUnit, movingUnit);
        // Same gate as the ground adapter: only an unchanged reading is answered from the cache. A
        // hive core's hitpoints, a node's manufacture progress and a turret's state are settled
        // facts — see EnemyStatePolicy.HasMoved.
        if (published.TryGetValue(key, out var previous) && !EnemyStatePolicy.HasMoved(previous.State, decoded, movingUnit))
        { state = previous.Bytes; return true; }
        if (!SpaceEnemyStateCodec.TryEncodeCaptured(decoded, capturedUnit, out state, out var canonical))
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
    private bool TryReadEnemyState(SpaceSector sector, int enemyId, out SpaceEnemyState state, out EnemyUnitComponent capturedUnit)
    {
        state = default; capturedUnit = default;
        ref var enemy = ref sector.enemyPool[enemyId];
        var kind = SpaceEnemyKinds.FromComponentIds(enemy.dfSCoreId, enemy.dfSNodeId,
            enemy.dfSConnectorId, enemy.dfSReplicatorId, enemy.dfSGammaId, enemy.dfSTurretId,
            enemy.dfTinderId, enemy.dfRelayId, enemy.unitId, enemy.builderId);
        if (kind == SpaceEnemyKind.Unknown)
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
                stats.buffer[combatStatId].objectId == enemyId)
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
                Log.Warn($"[authority] space enemy {enemyId} has a dangling combatStatId {combatStatId}");
            }
        }
        var dockIndex = ResolveDockIndex(sector, in enemy);
        var builderIndex = ResolveBuilderIndex(sector, in enemy);
        var unit = ReadUnit(sector, in enemy);
        if (kind == SpaceEnemyKind.SpaceUnit && unit.id == 0) { CorruptReferences++; return false; }
        var level = kind == SpaceEnemyKind.SpaceUnit ? unit.level : ResolveLevel(sector, in enemy, kind);
        var animation = sector.enemyAnimPool[enemyId];
        // A hive turret's aim is only copied into the sector animation pool while the host is inside
        // the star (isLocal). Resolving it from the component keeps the barrel turning wherever the
        // host happens to be, and feeds the brief panel its engaged/alert state.
        byte[] nativeTurret = null;
        var turretState = 0;
        var turretInRange = false;
        if (enemy.dfSTurretId > 0)
        {
            var hive = ResolveHive(sector, in enemy);
            if (hive != null && enemy.dfSTurretId < hive.turrets.cursor)
            {
                ref var turret = ref hive.turrets.buffer[enemy.dfSTurretId];
                if (turret.id == enemy.dfSTurretId &&
                    EnemyTurretAnimation.BuilderIsActive(hive.builders, turret.builderId))
                {
                    animation = EnemyTurretAnimation.Resolve(turret.localDir, turret.state, turret.fire, turret.heat,
                        turret.sensorRange, turret.attackRange, EnemyTurretAnimation.AttackInterval(enemy.modelIndex));
                    turretState = (int)turret.state;
                nativeTurret = NebulaeWorld.Combat.NativeDarkFogTurrets.Export(turret);
                    turretInRange = turret.targetInShootRange;
                }
            }
        }
        var buildingFacts = ReadBuildingFacts(sector, in enemy);
        capturedUnit = unit;
        state = new SpaceEnemyState(kind, hasCombatStat, enemy.dynamic,
            enemy.protoId, enemy.modelIndex, enemy.owner, enemy.port, enemy.stateFlags,
            enemy.astroId, enemy.originAstroId, dockIndex, builderIndex, level,
            enemy.pos.x, enemy.pos.y, enemy.pos.z,
            enemy.rot.x, enemy.rot.y, enemy.rot.z, enemy.rot.w,
            enemy.dynamic ? enemy.vel.x : 0f, enemy.dynamic ? enemy.vel.y : 0f, enemy.dynamic ? enemy.vel.z : 0f,
            hp, hpMax, hpRecover, hpIncoming,
            animation.time, animation.prepare_length, animation.working_length, animation.state, animation.power,
            buildingFacts.Sp, buildingFacts.SpMax, buildingFacts.ProductSp, buildingFacts.ProductSpMax,
            buildingFacts.Matter, buildingFacts.MatterMax, buildingFacts.MatterChange,
            buildingFacts.Energy, buildingFacts.EnergyMax, buildingFacts.EnergyChange, (byte)unit.behavior,
            (byte)turretState, turretInRange,
            kind == SpaceEnemyKind.SpaceUnit && enemy.dynamic ? Multiplayer.Session?.AuthorityRuntime?.CompletedSimulationTick ?? GameMain.gameTick : 0,
            unit.stateTick, unit.disturbValue,
            null,
            unit.anim, unit.steering, unit.speed, buildingFacts.State, nativeTurret, ReadActivity(sector, in enemy));
        return true;
    }

    internal static SpaceEnemyActivity ReadActivity(SpaceSector sector, in EnemyData enemy)
    {
        if (enemy.dfRelayId == 0 && enemy.dfTinderId == 0 && enemy.dfSCoreId == 0) return default;
        var hive = sector.GetHiveByAstroId(enemy.originAstroId);
        if (hive == null) return default;
        if (enemy.dfRelayId > 0 && enemy.dfRelayId < hive.relays.cursor && hive.relays.buffer[enemy.dfRelayId] is { } relay && relay.id == enemy.dfRelayId)
            return new SpaceEnemyActivity(stage: relay.stage, direction: relay.direction, targetAstroId: relay.targetAstroId,
                baseId: relay.baseId, baseState: relay.baseState);
        if (enemy.dfTinderId > 0 && enemy.dfTinderId < hive.tinders.cursor)
        {
            ref var tinder = ref hive.tinders.buffer[enemy.dfTinderId];
            if (tinder.id == enemy.dfTinderId) return new SpaceEnemyActivity(stage: tinder.stage, direction: tinder.direction, targetAstroId: tinder.targetHiveAstroId);
        }
        if (enemy.dfSCoreId > 0 && enemy.dfSCoreId < hive.cores.cursor)
        {
            ref var core = ref hive.cores.buffer[enemy.dfSCoreId];
            if (core.id == enemy.dfSCoreId) return new SpaceEnemyActivity(hive.relayCount, hive.tinderCount,
                core.buildRelaySp, core.buildRelaySpMax, core.buildTinderSp, core.buildTinderSpMax);
        }
        return default;
    }

    public bool TryReadSnapshotState(ObjectKey key, out byte[] state)
    {
        published.Remove(key);
        return TryReadState(key, out state);
    }

    /// <summary>
    /// The unit's <c>EnemyUnitComponent.behavior</c>, which the client's brief panel prints as the
    /// state line ("攻击中"/"巡逻中"/…). Zero for every non-unit enemy and while the hive or the
    /// component cannot be resolved.
    /// </summary>
    private static EnemyUnitComponent ReadUnit(SpaceSector sector, in EnemyData enemy)
    {
        if (enemy.unitId <= 0) return default;
        var hive = sector.GetHiveByAstroId(enemy.originAstroId);
        if (hive?.units == null || enemy.unitId >= hive.units.cursor) return default;
        ref var unit = ref hive.units.buffer[enemy.unitId];
        return unit.id == enemy.unitId && unit.enemyId == enemy.id ? unit : default;
    }

    /// <summary>The hive an enemy's building components belong to: where it is now, else where it came from.</summary>
    private static EnemyDFHiveSystem ResolveHive(SpaceSector sector, in EnemyData enemy)
    {
        // GetHiveByAstroId is null-safe for every astro id: a planet astro indexes out of range and yields null.
        return sector.GetHiveByAstroId(enemy.astroId) ?? sector.GetHiveByAstroId(enemy.originAstroId);
    }

    /// <summary>
    /// The builder/replicator presentation the vanilla brief panel needs. Without it a client shell
    /// (whose builderId is zero) leaves the panel dividing 0/0 and printing "建造中 NaN" for every
    /// hive building; the matter/energy income mirrors the ground adapter's formulas so both panels
    /// read the same way.
    /// </summary>
    private static (int Sp, int SpMax, int ProductSp, int ProductSpMax, int Matter, int MatterMax,
        int MatterChange, int Energy, int EnergyMax, int EnergyChange, int State) ReadBuildingFacts(SpaceSector sector, in EnemyData enemy)
    {
        var facts = (Sp: 0, SpMax: 0, ProductSp: 0, ProductSpMax: 0, Matter: 0, MatterMax: 0,
            MatterChange: 0, Energy: 0, EnergyMax: 0, EnergyChange: 0, State: 0);
        var hive = ResolveHive(sector, in enemy);
        if (hive == null) return facts;
        try
        {
            if (enemy.builderId > 0 && enemy.builderId < hive.builders.cursor)
            {
                ref var builder = ref hive.builders.buffer[enemy.builderId];
                if (builder.id == enemy.builderId && builder.enemyId == enemy.id)
                {
                    facts.State = builder.state;
                    facts.Sp = builder.sp;
                    facts.SpMax = builder.spMax;
                    facts.Matter = builder.matter;
                    facts.MatterMax = builder.maxMatter;
                    facts.MatterChange = builder.sp < builder.spMax ? -builder.spMatter : builder.genMatter;
                    facts.Energy = builder.energy;
                    facts.EnergyMax = builder.maxEnergy;
                    facts.EnergyChange = builder.genEnergy - (builder.state < 4 ? builder.idleEnergy : builder.workEnergy);
                }
            }
            if (enemy.dfSReplicatorId > 0 && enemy.dfSReplicatorId < hive.replicators.cursor)
            {
                ref var replicator = ref hive.replicators.buffer[enemy.dfSReplicatorId];
                if (replicator.id == enemy.dfSReplicatorId)
                {
                    facts.ProductSp = replicator.productSp;
                    facts.ProductSpMax = replicator.productSpMax;
                }
            }
        }
        catch (System.Exception e)
        {
            Log.Warn("[authority] space enemy building read failed: " + e.Message);
        }
        return facts;
    }

    private static int ResolveDockIndex(SpaceSector sector, in EnemyData enemy)
    {
        try
        {
            if (enemy.dfRelayId <= 0 && enemy.dfTinderId <= 0) return 0;
            var hive = sector.GetHiveByAstroId(enemy.originAstroId);
            if (hive == null) return 0;
            if (enemy.dfRelayId > 0 && enemy.dfRelayId < hive.relays.cursor &&
                hive.relays.buffer[enemy.dfRelayId] != null)
            {
                return hive.relays.buffer[enemy.dfRelayId].dockIndex;
            }
            if (enemy.dfTinderId > 0 && enemy.dfTinderId < hive.tinders.cursor &&
                hive.tinders.buffer[enemy.dfTinderId].id == enemy.dfTinderId)
            {
                return hive.tinders.buffer[enemy.dfTinderId].dockIndex;
            }
        }
        catch (System.Exception e)
        {
            Log.Warn("[authority] space enemy dock read failed: " + e.Message);
        }
        return 0;
    }

    private static int ResolveBuilderIndex(SpaceSector sector, in EnemyData enemy)
    {
        if (enemy.builderId <= 0) return 0;
        try
        {
            var hive = sector.GetHiveByAstroId(enemy.originAstroId);
            if (hive == null) return 0;
            if (enemy.builderId < hive.builders.cursor &&
                hive.builders.buffer[enemy.builderId].id == enemy.builderId)
            {
                return hive.builders.buffer[enemy.builderId].builderIndex;
            }
        }
        catch (System.Exception e)
        {
            Log.Warn("[authority] space enemy builder read failed: " + e.Message);
        }
        return 0;
    }

    private static int ResolveLevel(SpaceSector sector, in EnemyData enemy, SpaceEnemyKind kind)
    {
        try
        {
            var hive = sector.GetHiveByAstroId(enemy.originAstroId);
            if (hive == null) return 0;
            if (kind == SpaceEnemyKind.SpaceCore)
            {
                return hive.evolve.level;
            }
        }
        catch (System.Exception e)
        {
            Log.Warn("[authority] space enemy level read failed: " + e.Message);
        }
        return 0;
    }
}
