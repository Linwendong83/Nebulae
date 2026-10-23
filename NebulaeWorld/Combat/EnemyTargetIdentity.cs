using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NebulaeModel.Authority;

namespace NebulaeWorld.Combat;

internal static class EnemyTargetIdentity
{
    private sealed class Targets
    {
        public readonly ConcurrentDictionary<int, (SkillTarget Target, ObjectKey Key)> Turrets = new();
        public readonly ConcurrentDictionary<int, (SkillTarget Target, ObjectKey Key, int Life)> Missiles = new();
    }
    private static readonly ConditionalWeakTable<PlanetFactory, Targets> turrets = new();
    private static readonly ConditionalWeakTable<DataPool<GeneralMissile>, Targets> missiles = new();

    public static bool TryResolve(SkillTarget target, out ObjectKey key)
    {
        key = default;
        if (target.type != ETargetType.Enemy || target.id <= 0 || target.astroId < 0) return false;
        var space = target.astroId == 0 || AuthorityScope.IsSectorAstro(target.astroId);
        var factory = space ? null : GameMain.galaxy?.PlanetById(target.astroId)?.factory;
        var pool = space ? GameMain.spaceSector?.enemyPool : factory?.enemyPool;
        var cursor = space ? GameMain.spaceSector?.enemyCursor ?? 0 : factory?.enemyCursor ?? 0;
        if (pool == null || target.id >= pool.Length || target.id >= cursor ||
            pool[target.id].id != target.id || pool[target.id].isInvincible) return false;
        var runtime = Multiplayer.Session?.AuthorityRuntime;
        if (runtime == null) return true;
        if (Multiplayer.Session.IsServer)
            return space ? runtime.SpaceEnemyHost?.TryGetEnemyKey(target.id, out key) == true :
                runtime.GroundEnemyHost?.TryGetEnemyKey(target.astroId, target.id, out key) == true;
        return runtime.WorldReplica?.TryFindKey(space ? PoolKind.SpaceEnemy : PoolKind.GroundEnemy,
            space ? 0 : target.astroId, target.id, out key) == true;
    }

    public static bool SameTarget(SkillTarget a, SkillTarget b) => a.id == b.id && a.astroId == b.astroId && a.type == b.type;

    public static void Observe(PlanetFactory factory, in TurretComponent turret, long expectedGeneration = 0)
    {
        var targets = turrets.GetOrCreateValue(factory).Turrets;
        if (turret.target.id == 0) { targets.TryRemove(turret.entityId, out _); return; }
        if (TryResolve(turret.target, out var key) && (expectedGeneration == 0 || key.Generation == expectedGeneration))
            targets[turret.entityId] = (turret.target, key);
    }

    public static bool Validate(PlanetFactory factory, ref TurretComponent turret)
    {
        // Native Search changes only id/astroId. It relies on this persistent kind,
        // even while the target is empty; default(SkillTarget) would mean a building.
        turret.target.type = ETargetType.Enemy;
        if (turret.target.id == 0) return true;
        var targets = turrets.GetOrCreateValue(factory).Turrets;
        var valid = TryResolve(turret.target, out var key);
        if (targets.TryGetValue(turret.entityId, out var previous) && SameTarget(previous.Target, turret.target))
            valid &= previous.Key == key;
        if (valid) { targets[turret.entityId] = (turret.target, key); return true; }
        if (turret.projectileId > 0) turret.StopContinuousSkill(factory.skillSystem, false);
        turret.projectileId = 0; turret.target = new SkillTarget { type = ETargetType.Enemy }; turret.hatred0 = default;
        turret.isAiming = turret.isLockingTarget = false; turret.activeSearch = true;
        targets.TryRemove(turret.entityId, out _);
        return false;
    }

    public static bool ValidateMissile(SkillSystem skills, ref GeneralMissile missile)
    {
        if (missile.caster.type == ETargetType.None) missile.target.type = ETargetType.Enemy;
        if (missile.target.id == 0 || missile.target.type != ETargetType.Enemy) return true;
        var pool = missile.caster.type == ETargetType.Player ? skills.mechaMissiles : skills.turretMissiles;
        if (pool == null) return false;
        var targets = missiles.GetOrCreateValue(pool).Missiles;
        var valid = TryResolve(missile.target, out var key);
        // GeneralMissile.life is an increasing age, unlike local projectile countdowns.
        if (targets.TryGetValue(missile.id, out var previous) && missile.life >= previous.Life && SameTarget(previous.Target, missile.target))
            valid &= previous.Key == key;
        if (valid) { targets[missile.id] = (missile.target, key, missile.life); return true; }
        // Keep native flight/expiry, but never home onto an unrelated recycled slot.
        missile.target.id = 0; missile.targetCombatStatId = 0;
        targets.TryRemove(missile.id, out _); return false;
    }

    public static void ForgetMissile(DataPool<GeneralMissile> pool, int id)
    {
        if (missiles.TryGetValue(pool, out var targets)) targets.Missiles.TryRemove(id, out _);
    }

    public static void RemoveGroundReferences(PlanetFactory factory, int enemyId)
    {
        var defense = factory.defenseSystem;
        if (defense?.turrets == null) return;
        defense.RemoveGlobalTargets(ETargetType.Enemy, enemyId);
        for (var id = 1; id < defense.turrets.cursor; id++)
        {
            ref var turret = ref defense.turrets.buffer[id];
            if (turret.id != id) continue;
            if (!turret.hatred0.isSpace && turret.hatred0.objectId == enemyId) turret.hatred0 = default;
            if (turret.target.type != ETargetType.Enemy || turret.target.astroId != factory.planetId || turret.target.id != enemyId) continue;
            if (turret.projectileId > 0) turret.StopContinuousSkill(factory.skillSystem, false);
            turret.target = new SkillTarget { type = ETargetType.Enemy }; turret.projectileId = 0;
            turret.isAiming = turret.isLockingTarget = false; turret.activeSearch = true;
            turrets.GetOrCreateValue(factory).Turrets.TryRemove(turret.entityId, out _);
        }
    }
}
