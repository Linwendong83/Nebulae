using System;
using System.Collections.Generic;
using System.IO;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Combat;
using UnityEngine;

namespace NebulaeWorld.Authority.Adapters;

internal sealed class DarkFogReplicaBinding : IReplicaMirrorObserver, IReplicaBaselineReadiness
{
    private readonly HashSet<int> dirtySearchPlanets = new();
    private readonly Dictionary<(int Planet, int Base), ObjectKey> baseKeys = new();
    private readonly Dictionary<(int Planet, int Base), GroundBaseState> baseSearch = new();
    private readonly Dictionary<int, (EnemyData[] Enemies, DFGBaseComponent[] Bases, object Identity)> poolIdentities = new();

    public object PoolIdentity(int planet, EnemyData[] enemies, DFGBaseComponent[] bases)
    {
        if (enemies == null || bases == null) return null;
        if (poolIdentities.TryGetValue(planet, out var previous) && ReferenceEquals(previous.Enemies, enemies) && ReferenceEquals(previous.Bases, bases))
            return previous.Identity;
        var identity = new object();
        poolIdentities[planet] = (enemies, bases, identity);
        return identity;
    }
    public readonly Dictionary<int, bool> HiveAlive = new();
    public readonly Dictionary<int, int> HiveBuildings = new();
    public readonly Dictionary<int, int> HiveUnits = new();
    public readonly Dictionary<(int Planet, int Base), int> BaseBuildings = new();
    public readonly Dictionary<(int Planet, int Base), int> BaseUnits = new();
    public readonly Dictionary<(int Planet, int Base), bool> BaseAlive = new();
    public readonly Dictionary<(int Planet, int Base), byte> BaseRemovalFlags = new();
    public bool IsReadyForBaseline(ScopeKey scope) => scope.Kind == PoolKind.HiveSummary
        ? GameMain.spaceSector?.dfHivesByAstro != null
        : scope.Kind != PoolKind.Base || GameMain.galaxy?.PlanetById(scope.Scope)?.factory?.planet.factoryLoaded == true;

    public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state)
    {
        if (scope.Kind != PoolKind.HiveSummary && scope.Kind != PoolKind.Base) return;
        if (!Multiplayer.Session.AuthorityRuntime.ApplyContext.IsActiveOnCurrentThread) throw new InvalidOperationException("Dark fog state outside replica apply.");
        if (scope.Kind == PoolKind.Base)
        {
            ApplyBase(scope.Scope, key.NativeId, GroundBaseStateCodec.Decode(state));
            baseKeys[(scope.Scope, key.NativeId)] = key;
            return;
        }
        if (state == null || state.Length > AuthorityLimits.StateRecordMaxBytes) throw new InvalidDataException("Invalid dark fog summary.");
        using var stream = new MemoryStream(state, false);
        using var reader = new BinaryReader(stream);
        if (scope.Kind == PoolKind.HiveSummary)
        {
            var sector = GameMain.spaceSector;
            var starId = reader.ReadInt32(); var orbit = reader.ReadInt32();
            var realized = reader.ReadBoolean(); var empty = reader.ReadBoolean(); var alive = reader.ReadBoolean();
            var root = reader.ReadInt32();
            var evolve = new EvolveData(); evolve.Import(reader);
            var incoming = reader.ReadInt32();
            var position = new VectorLF3(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
            var buildings = reader.ReadInt32(); var unitCount = reader.ReadInt32();
            if (starId <= 0 || GameMain.galaxy.StarById(starId) == null || evolve.level < 0 || evolve.level > 100 || key.NativeId > 65536)
                throw new InvalidDataException("Invalid hive identity or level.");
            var star = GameMain.galaxy.StarById(starId);
            if (orbit < 0 || orbit >= star.hiveAstroOrbits.Length) throw new InvalidDataException("Invalid hive orbit index.");
            if (key.NativeId >= sector.dfHivesByAstro.Length) Array.Resize(ref sector.dfHivesByAstro, key.NativeId + 1);
            var hive = sector.dfHivesByAstro[key.NativeId];
            var needsBinding = hive == null || realized && !hive.realized;
            if (hive == null)
            {
                if (key.NativeId != star.index * 8 + orbit + 1) throw new InvalidDataException("Hive slot does not match orbit.");
                hive = new EnemyDFHiveSystem(); hive.Init(GameMain.data, starId, orbit);
                // Initializes deterministic layout/docks and astro metadata, without spawning enemies.
                hive.SetForNewCreate();
                AppendHive(sector, hive);
            }
            PrepareHivePresentation(hive, realized);
            hive.isEmpty = empty; hive.rootEnemyId = root;
            var hostOrbit = hive.hiveAstroOrbit;
            hostOrbit.orbitRadius = reader.ReadSingle(); hostOrbit.orbitInclination = reader.ReadSingle(); hostOrbit.orbitLongitude = reader.ReadSingle();
            hostOrbit.orbitalPeriod = reader.ReadDouble(); hostOrbit.orbitPhase = reader.ReadSingle();
            hostOrbit.orbitRotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            hostOrbit.orbitNormal = new VectorLF3(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
            hive.orbitRadius = reader.ReadDouble();
            for (var form = 0; form < 3; form++) InstallFormation(hive.forms[form], HiveFormationStateCodec.Read(reader, form));
            if (hostOrbit.orbitalPeriod <= 0 || double.IsNaN(hostOrbit.orbitalPeriod) || double.IsInfinity(hostOrbit.orbitalPeriod))
                throw new InvalidDataException("Invalid hive orbital period.");
            hostOrbit.PredictPose(GameMain.gameTick, hive.starData.uPosition, ref sector.astros[key.NativeId]);
            var patterns = HivePatternStorage.ReadCore(hive);
            if (patterns != null && patterns.Length > 1) patterns[1].instId = root;
            hive.evolve = evolve; hive.currentIncomingAssaultingUnitCount = incoming;
            hive.currentIncomingAssaultingUnitUPos = position;
            HiveAlive[hive.hiveAstroId] = alive;
            HiveBuildings[hive.hiveAstroId] = buildings; HiveUnits[hive.hiveAstroId] = unitCount;
            if (needsBinding) SpaceEnemyPresentation.HiveReady(sector, hive);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing dark fog summary data.");
    }

    internal static void AppendHive(SpaceSector sector, EnemyDFHiveSystem hive)
    {
        var previous = sector.dfHives[hive.starData.index];
        if (previous == null) sector.dfHives[hive.starData.index] = hive;
        else
        {
            while (previous.nextSibling != null) previous = previous.nextSibling;
            previous.nextSibling = hive; hive.prevSibling = previous;
        }
    }

    internal static void PrepareHivePresentation(EnemyDFHiveSystem hive, bool realized)
    {
        hive.realized = realized;
        if (realized && (hive.antSegments == null || hive.antSegments.Length == 0))
        {
            hive.CreateAntSegmentBuffer();
            // Unity cannot allocate an empty ComputeBuffer (an empty layout has no ant segments).
            if (hive.antSegments?.Length == 0) hive.antSegments = new byte[4];
        }
    }

    public static void InstallFormation(EnemyFormation formation, byte[] tags)
    {
        if (formation.units?.Length == tags.Length + 1)
        {
            var equal = true;
            for (var port = 1; port <= tags.Length; port++) if (formation.units[port] != tags[port - 1]) { equal = false; break; }
            if (equal) return;
        }
        // Native vacancies are private. Rebuild through the native API, then install the host tags.
        formation.SetPortCount(tags.Length);
        var occupied = new bool[tags.Length + 1];
        for (var port = 1; port <= tags.Length; port++) occupied[port] = tags[port - 1] != 0;
        for (var port = 1; port <= tags.Length; port++) formation.AddUnit();
        for (var port = 1; port <= tags.Length; port++)
            if (!occupied[port]) formation.RemoveUnit(port);
            else formation.units[port] = tags[port - 1];
    }

    private void ApplyBase(int planet, int id, GroundBaseState state)
    {
        var factory = GameMain.galaxy.PlanetById(planet).factory;
        using var evolutionStream = new MemoryStream(state.Evolution, false);
        using var reader = new BinaryReader(evolutionStream);
        var evolve = new EvolveData(); evolve.Import(reader);
        if (evolutionStream.Position != evolutionStream.Length || evolve.level < 0 || evolve.level > 100 || id > 65536)
            throw new InvalidDataException("Invalid base evolution or identity.");
        var pool = factory.enemySystem.bases;
        if (pool.capacity <= id) pool.SetCapacity(Math.Max(id + 1, pool.capacity * 2));
        var item = pool.buffer[id];
        if (item?.id != id)
        {
            item = new DFGBaseComponent { id = id, groundSystem = factory.enemySystem };
            item.InitFormations(); item.InitIncomingSkills();
            pool.buffer[id] = item;
            pool.cursor = Math.Max(pool.cursor, id + 1);
            for (var recycle = 0; recycle < pool.recycleCursor; recycle++)
                if (pool.recycleIds[recycle] == id)
                { pool.recycleIds[recycle] = pool.recycleIds[--pool.recycleCursor]; break; }
        }
        // Imported native builder ids cannot drive this display-only base.
        item.builderId = 0;
        item.enemyId = state.EnemyId; item.ruinId = 0; item.evolve = evolve;
        // The live core's state area is independent of the pit's ruin area. Removing
        // a presentation shell deliberately skips native base teardown, so own it here.
        factory.platformSystem?.RemoveStateArea((uint)(0x1000000 | id));
        if (state.CoreAlive && state.EnemyId < factory.enemyCursor && factory.enemyPool[state.EnemyId].id == state.EnemyId)
            factory.platformSystem?.AddStateArea((uint)(0x1000000 | id), factory.enemyPool[state.EnemyId].pos, 11.25f, 1);
        item.hiveAstroId = state.HiveAstroId; item.relayEnemyId = state.RelayEnemyId;
        item.relayId = 0; // Resolved from the relay enemy's client-local component id when queried.
        item.currentIncomingAssaultingUnitCount = state.Incoming;
        item.currentIncomingAssaultingUnitPos = new Vector3(state.X, state.Y, state.Z);
        if (!baseKeys.ContainsKey((planet, id))) item.pbuilders = Array.Empty<GrowthPattern_DFGround.Builder>();
        for (var form = 0; form < 3; form++) InstallFormation(item.forms[form], state.Forms[form]);
        BaseBuildings[(planet, id)] = state.Buildings; BaseUnits[(planet, id)] = state.Units;
        BaseAlive[(planet, id)] = state.CoreAlive;
        BaseRemovalFlags[(planet, id)] = state.RemovalFlags;
        baseSearch[(planet, id)] = state;
        if (!baseKeys.ContainsKey((planet, id))) dirtySearchPlanets.Add(planet);
    }

    public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
    {
        if (scope.Kind != PoolKind.HiveSummary && scope.Kind != PoolKind.Base) return;
        var keep = new HashSet<int>();
        foreach (var member in members) keep.Add(member.Key.NativeId);
        if (scope.Kind == PoolKind.Base)
        {
            var pool = GameMain.galaxy.PlanetById(scope.Scope).factory.enemySystem.bases;
            for (var id = 1; id < pool.cursor; id++)
                if (pool.buffer[id]?.id == id && !keep.Contains(id))
                {
                    GameMain.galaxy.PlanetById(scope.Scope).factory.platformSystem?.RemoveStateArea((uint)(0x1000000 | id));
                    pool.Remove(id); dirtySearchPlanets.Add(scope.Scope);
                    BaseBuildings.Remove((scope.Scope, id)); BaseUnits.Remove((scope.Scope, id)); baseKeys.Remove((scope.Scope, id));
                    BaseAlive.Remove((scope.Scope, id));
                    BaseRemovalFlags.Remove((scope.Scope, id));
                    baseSearch.Remove((scope.Scope, id));
                }
        }
        else
            foreach (var id in new List<int>(HiveAlive.Keys))
                if (!keep.Contains(id - 1000000)) HiveAlive[id] = false;
        foreach (var member in members) OnStateApplied(scope, member.Key, member.Revision, member.State);
    }

    public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
    {
        if (scope.Kind == PoolKind.HiveSummary) HiveAlive[1000000 + key.NativeId] = false;
        else if (scope.Kind == PoolKind.Base)
        {
            var identity = (scope.Scope, key.NativeId);
            if (baseKeys.TryGetValue(identity, out var current) && !current.Equals(key)) return;
            baseKeys.Remove(identity); BaseBuildings.Remove(identity); BaseUnits.Remove(identity);
            BaseAlive.Remove(identity);
            BaseRemovalFlags.Remove(identity);
            baseSearch.Remove(identity);
            var pool = GameMain.galaxy?.PlanetById(scope.Scope)?.factory?.enemySystem?.bases;
            if (pool != null && key.NativeId < pool.cursor && pool.buffer[key.NativeId]?.id == key.NativeId)
            {
                GameMain.galaxy.PlanetById(scope.Scope).factory.platformSystem?.RemoveStateArea((uint)(0x1000000 | key.NativeId));
                pool.Remove(key.NativeId); dirtySearchPlanets.Add(scope.Scope);
            }
        }
    }

    public void FlushSearchReferences()
    {
        var replica = Multiplayer.Session.AuthorityRuntime.WorldReplica;
        foreach (var pair in baseSearch)
        {
            var factory = GameMain.galaxy?.PlanetById(pair.Key.Planet)?.factory;
            var item = factory?.enemySystem?.bases[pair.Key.Base];
            if (item?.id != pair.Key.Base || factory.planet.factoryLoaded != true) continue;
            var state = pair.Value;
            // Native geothermal collision checks identify the persistent core through this
            // link. AI remains disabled; shell removal clears the link before native teardown.
            var bindCore = state.EnemyId > 0 && state.EnemyId < factory.enemyCursor && factory.enemyPool[state.EnemyId].id == state.EnemyId &&
                factory.enemyPool[state.EnemyId].owner == item.id && factory.enemyPool[state.EnemyId].dfGBaseId != item.id;
            var ruinId = state.RuinId > 0 && state.RuinId < factory.ruinCursor && factory.ruinPool[state.RuinId].id == state.RuinId &&
                replica.TryFindKey(PoolKind.Ruin, pair.Key.Planet, state.RuinId, out var ruinKey) && ruinKey.Generation == state.RuinGeneration
                ? state.RuinId : 0;
            if (bindCore || item.ruinId != ruinId)
            {
                var targetFactory = factory; var targetBase = item; var targetRuin = ruinId;
                var coreId = bindCore ? state.EnemyId : 0; var alive = state.CoreAlive;
                Multiplayer.Session.AuthorityRuntime.ApplyContext.Run(new ApplyScope(new ScopeKey(PoolKind.Base, pair.Key.Planet)), () =>
                {
                    targetBase.ruinId = targetRuin;
                    if (coreId <= 0) return;
                    targetFactory.enemyPool[coreId].dfGBaseId = (short)targetBase.id;
                    if (alive) targetFactory.platformSystem?.AddStateArea((uint)(0x1000000 | targetBase.id), targetFactory.enemyPool[coreId].pos, 11.25f, 1);
                });
            }
        }
        foreach (var planet in dirtySearchPlanets)
        {
            var defense = GameMain.galaxy?.PlanetById(planet)?.factory?.defenseSystem;
            if (defense == null) continue;
            Multiplayer.Session.AuthorityRuntime.ApplyContext.Run(new ApplyScope(new ScopeKey(PoolKind.Base, planet)), () =>
            {
                RebuildSearchIndex(defense.factory, replica);
                ClearSearchReferences(defense);
                defense.RefreshTurretSearchPair();
            });
        }
        dirtySearchPlanets.Clear();
    }

    public void MarkSearchDirty(int planet) => dirtySearchPlanets.Add(planet);

    internal static void RebuildSearchIndex(PlanetFactory factory, ClientWorldReplica replica)
    {
        // Enemy lifecycle records already carry the authoritative generation. Reuse those
        // installed identities instead of duplicating an unbounded layout in a base record.
        var grouped = new Dictionary<int, List<GrowthPattern_DFGround.Builder>>();
        for (var id = 1; id < factory.enemyCursor; id++)
        {
            ref var enemy = ref factory.enemyPool[id];
            if (enemy.id != id || enemy.dynamic || enemy.owner <= 0 ||
                !replica.TryFindKey(PoolKind.GroundEnemy, factory.planetId, id, out _)) continue;
            if (!grouped.TryGetValue(enemy.owner, out var list)) grouped[enemy.owner] = list = new();
            list.Add(new GrowthPattern_DFGround.Builder { instId = id, connections = Array.Empty<int>() });
        }
        var bases = factory.enemySystem.bases;
        for (var id = 1; id < bases.cursor; id++)
            if (bases.buffer[id]?.id == id)
                bases.buffer[id].pbuilders = grouped.TryGetValue(id, out var list) ? list.ToArray() : Array.Empty<GrowthPattern_DFGround.Builder>();
    }

    public static void ClearSearchReferences(DefenseSystem defense)
    {
        defense.ClearTurretPairs();
        for (var id = 1; id < defense.turrets.cursor; id++)
        {
            ref var turret = ref defense.turrets.buffer[id];
            if (turret.id != id) continue;
            turret.searchPairBeginIndex = turret.searchPairCount = 0;
            turret.hatred0 = default;
        }
    }
}
