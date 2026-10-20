using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority.Adapters;
using NebulaeWorld.Combat;

namespace NebulaeWorld.Authority;

/// <summary>Reads real object pools. Locally advancing pose, targeting and cooldowns are excluded.</summary>
internal sealed class RuntimeObjectVerifier : IReplicaRuntimeVerifier
{
    public RuntimeObjectIntegrity VerifyObject(ObjectKey key, byte[] bytes)
    {
        switch (key.Kind)
        {
            case PoolKind.GroundEnemy:
                var ground = Factory(key.Scope);
                if (ground?.planet.factoryLoaded != true || !NativeEnemySimulation.IsLive(PoolKind.Base, key.Scope)) return RuntimeObjectIntegrity.Unavailable;
                if (!AuthorityStateFields.TryReadGround(bytes, out var g) ||
                    !EnemyExists(ground.enemyPool, key.NativeId)) return RuntimeObjectIntegrity.Drift;
                ref var ge = ref ground.enemyPool[key.NativeId];
                return Result(ge.protoId == g.ProtoId && ge.modelIndex == g.ModelIndex &&
                    UnitMatches(ground.enemySystem.units, ge, g.NativeUnit, g.DecodedNativeUnit) &&
                    CombatMatches(ground.skillSystem, ge.combatStatId, EObjectType.Enemy, key.NativeId, key.Scope,
                        g.HasCombatStat, g.Hp, g.HpMax, g.HpRecover, g.HpIncoming));
            case PoolKind.SpaceEnemy:
                var sector = GameMain.spaceSector;
                if (sector?.skillSystem == null || !NativeEnemySimulation.IsLive(PoolKind.HiveSummary, AuthorityScope.Sector)) return RuntimeObjectIntegrity.Unavailable;
                if (!AuthorityStateFields.TryReadSpace(bytes, out var s) ||
                    !EnemyExists(sector.enemyPool, key.NativeId)) return RuntimeObjectIntegrity.Drift;
                ref var se = ref sector.enemyPool[key.NativeId];
                var hive = sector.GetHiveByAstroId(se.originAstroId);
                return Result(se.protoId == s.ProtoId && se.modelIndex == s.ModelIndex &&
                    UnitMatches(hive?.units, se, s.NativeUnit, s.DecodedNativeUnit) &&
                    CombatMatches(sector.skillSystem, se.combatStatId, EObjectType.Enemy, key.NativeId, s.AstroId,
                        s.HasCombatStat, s.Hp, s.HpMax, s.HpRecover, s.HpIncoming));
            case PoolKind.Entity:
                var factory = Factory(key.Scope);
                if (factory?.planet.factoryLoaded != true) return RuntimeObjectIntegrity.Unavailable;
                if (!FactoryCombatStateCodec.TryDecode(bytes, 0, bytes.Length, out var state, out _) ||
                    key.NativeId <= 0 || key.NativeId >= factory.entityCursor || factory.entityPool[key.NativeId].id != key.NativeId)
                    return RuntimeObjectIntegrity.Drift;
                ref var entity = ref factory.entityPool[key.NativeId];
                var construct = entity.constructStatId;
                var constructMatches = !state.HasConstructStat ? construct == 0 : construct > 0 &&
                    construct < factory.constructionSystem.constructStats.cursor &&
                    factory.constructionSystem.constructStats.buffer[construct].id == construct &&
                    factory.constructionSystem.constructStats.buffer[construct].entityId == key.NativeId;
                return Result(constructMatches && CombatMatches(factory.skillSystem, entity.combatStatId,
                    EObjectType.Entity, key.NativeId, key.Scope, state.HasCombatStat, state.Hp, state.HpMax, state.HpRecover, state.HpIncoming));
            case PoolKind.Turret:
                var turretFactory = Factory(key.Scope);
                if (turretFactory?.planet.factoryLoaded != true) return RuntimeObjectIntegrity.Unavailable;
                return Result(TurretStateCodec.TryDecode(bytes, out var turret) &&
                    TurretSnapshotAdapter.TryTurret(turretFactory, key.NativeId, out var id) &&
                    turretFactory.entityPool[key.NativeId].modelIndex == turret.ModelIndex &&
                    (byte)turretFactory.defenseSystem.turrets.buffer[id].type == turret.Type);
            case PoolKind.GroundCraft:
            case PoolKind.SpaceCraft:
                return VerifyCraft(key, bytes);
            default: return RuntimeObjectIntegrity.Unavailable;
        }
    }

    private static RuntimeObjectIntegrity VerifyCraft(ObjectKey key, byte[] bytes)
    {
        var runtime = Multiplayer.Session?.AuthorityRuntime?.WorldReplica;
        var factory = key.Kind == PoolKind.GroundCraft ? Factory(key.Scope) : null;
        var sector = GameMain.spaceSector;
        if ((key.Kind == PoolKind.GroundCraft && factory?.planet.factoryLoaded != true) || sector?.skillSystem == null)
            return RuntimeObjectIntegrity.Unavailable;
        if (runtime == null || !runtime.Mirrors.TryGetValue(key, out var mirror) || mirror.LocalComponentId <= 0)
            return RuntimeObjectIntegrity.Unavailable;
        var pool = factory != null ? factory.craftPool : sector.craftPool;
        var id = mirror.LocalComponentId;
        if (!CraftStateCodec.TryDecode(bytes, 0, bytes.Length, out var state, out _) || id >= pool.Length || pool[id].id != id)
            return RuntimeObjectIntegrity.Drift;
        ref var craft = ref pool[id];
        var fleets = factory != null ? factory.combatGroundSystem?.fleets : sector.combatSpaceSystem?.fleets;
        var units = factory != null ? factory.combatGroundSystem?.units : sector.combatSpaceSystem?.units;
        if (state.NativeFleet?.Length > 0 && (!CraftNativeStateCodec.TryFleet(state.NativeFleet, out var fleet) ||
            fleets == null || craft.fleetId != fleet.id || fleet.id >= fleets.cursor ||
            fleets.buffer[fleet.id].id != fleet.id || fleets.buffer[fleet.id].craftId != id)) return RuntimeObjectIntegrity.Drift;
        if (state.NativeUnit?.Length > 0 && (!CraftNativeStateCodec.TryUnit(state.NativeUnit, out var unit) ||
            units == null || craft.unitId != unit.id || unit.id >= units.cursor ||
            units.buffer[unit.id].id != unit.id || units.buffer[unit.id].craftId != id)) return RuntimeObjectIntegrity.Drift;
        return Result(craft.protoId == state.ProtoId && craft.modelIndex == state.ModelIndex &&
            CombatMatches(sector.skillSystem, craft.combatStatId, EObjectType.Craft, id,
                factory?.planetId ?? state.AstroId, state.HasCombatStat, state.Hp, state.HpMax, state.HpRecover, state.HpIncoming));
    }

    private static bool UnitMatches(DataPool<EnemyUnitComponent> units, in EnemyData enemy, byte[] body,
        EnemyUnitComponent? decoded = null)
    {
        if (body?.Length is not > 0) return true;
        var expected = decoded.GetValueOrDefault();
        if (!decoded.HasValue && !EnemyUnitStateCodec.TryDecode(body, out expected)) return false;
        if (units == null ||
            enemy.unitId != expected.id || enemy.unitId >= units.cursor) return false;
        ref var actual = ref units.buffer[enemy.unitId];
        return actual.id == expected.id && actual.enemyId == enemy.id && actual.protoId == expected.protoId;
    }

    private static bool CombatMatches(SkillSystem skills, int id, EObjectType type, int owner, int astro,
        bool exists, int hp, int max, int recover, int incoming)
    {
        if (!exists) return id == 0;
        if (skills?.combatStats == null || id <= 0 || id >= skills.combatStats.cursor) return false;
        ref var actual = ref skills.combatStats.buffer[id];
        return actual.id == id && actual.objectType == (int)type && actual.objectId == owner && actual.astroId == astro &&
            actual.hp == hp && actual.hpMax == max && actual.hpRecover == recover && actual.hpIncoming == incoming;
    }

    private static bool EnemyExists(EnemyData[] pool, int id) => pool != null && id > 0 && id < pool.Length && pool[id].id == id;
    private static PlanetFactory Factory(int planet) => GameMain.galaxy?.PlanetById(planet)?.factory;
    private static RuntimeObjectIntegrity Result(bool matches) => matches ? RuntimeObjectIntegrity.Matches : RuntimeObjectIntegrity.Drift;
}
