namespace NebulaeWorld.Combat;

/// <summary>Native interception links name process-local skill slots, not persistent objects.</summary>
internal static class GroundDefenseReferences
{
    public static void Clear(SkillSystem skills, int id, int astro, ESkillType type)
    {
        if (id <= 0 || astro <= 0 || !SpaceSector.IsPlanetAstro(astro)) return;
        var factories = skills?.astroFactories;
        var system = factories != null && astro < factories.Length ? factories[astro]?.enemySystem : null;
        if (system == null) return; // An unloaded client planet has no native links to retire.
        var bases = system.bases;
        if (bases?.buffer != null)
            for (var i = 1; i < bases.cursor; i++)
            {
                var owner = bases.buffer[i];
                if (owner?.id == i && owner.incomingSkills != null) owner.RemoveIncomingSkill(id, type);
            }
        var units = system.units;
        if (units?.buffer != null)
            for (var i = 1; i < units.cursor; i++)
                if (units.buffer[i].id == i) units.buffer[i].ClearBlockSkill(id, type);
    }

    public static void RetireMissile(DataPool<GeneralMissile> pool, int id)
    {
        var skills = GameMain.spaceSector?.skillSystem;
        if (skills == null || pool == null || id <= 0 || id >= pool.cursor || id >= pool.buffer.Length || pool.buffer[id].id != id) return;
        var type = ReferenceEquals(pool, skills.mechaMissiles) ? ESkillType.MechaMissiles : ESkillType.TurretMissiles;
        if (!ReferenceEquals(pool, skills.mechaMissiles) && !ReferenceEquals(pool, skills.turretMissiles)) return;
        Clear(skills, id, pool.buffer[id].target.astroId, type);
    }

    // Called at each native blockSkill.id check, including after the native base-list selection.
    // The common valid path is O(1); no per-unit scan of the base's incoming list or allocation.
    public static int ValidId(ref SKILLID skill, SkillSystem skills, EnemyData[] enemies,
        DFGBaseComponent[] bases, ref EnemyData enemy)
    {
        if (skill.id <= 0) return 0;
        if (Valid(skill, skills, enemies, enemy.originAstroId)) return skill.id;
        var previous = skill;
        skill = default;
        if (enemy.owner > 0 && bases != null && enemy.owner < bases.Length)
        {
            var owner = bases[enemy.owner];
            if (owner?.id == enemy.owner && owner.incomingSkills != null) owner.RemoveIncomingSkill(previous.id, previous.type);
        }
        return 0;
    }

    internal static bool Valid(SKILLID skill, SkillSystem skills, EnemyData[] enemies, int planet)
    {
        if (skills == null || enemies == null) return false;
        switch (skill.type)
        {
            case ESkillType.FighterPlasmas: return Local(skills.fighterPlasmas, skill.id, enemies, planet);
            case ESkillType.FighterShieldPlasmas: return Local(skills.fighterShieldPlasmas, skill.id, enemies, planet);
            case ESkillType.TurretGaussProjectiles: return Local(skills.turretGaussProjectiles, skill.id, enemies, planet);
            case ESkillType.MechaLocalGaussProjectiles: return Local(skills.mechaLocalGaussProjectiles, skill.id, enemies, planet);
            case ESkillType.MechaPlasmas:
                if (!Exists(skills.mechaPlasmas, skill.id)) return false;
                var plasma = skills.mechaPlasmas.buffer[skill.id];
                return plasma.life > 0 && Target(plasma.target, enemies, planet);
            case ESkillType.TurretMissiles: return Missile(skills.turretMissiles, skill.id, enemies, planet);
            case ESkillType.MechaMissiles: return Missile(skills.mechaMissiles, skill.id, enemies, planet);
            default: return false;
        }
    }

    private static bool Exists<T>(DataPool<T> pool, int id) where T : struct, IPoolElement =>
        pool?.buffer != null && id > 0 && id < pool.cursor && id < pool.buffer.Length && pool.buffer[id].ID == id;
    private static bool Exists<T>(DataPoolRenderer<T> pool, int id) where T : struct, IPoolElement =>
        pool?.buffer != null && id > 0 && id < pool.cursor && id < pool.buffer.Length && pool.buffer[id].ID == id;
    private static bool Target(SkillTarget target, EnemyData[] enemies, int planet) =>
        target.astroId == planet && target.type == ETargetType.Enemy && Target(target.id, target.type, enemies);
    private static bool Target(int id, ETargetType type, EnemyData[] enemies) =>
        type == ETargetType.Enemy && id > 0 && id < enemies.Length && enemies[id].id == id;
    private static bool Local(DataPoolRenderer<LocalGeneralProjectile> pool, int id, EnemyData[] enemies, int planet)
    {
        if (!Exists(pool, id)) return false;
        var shot = pool.buffer[id];
        return shot.life > 0 && shot.astroId == planet && Target(shot.target.id, shot.target.type, enemies);
    }
    private static bool Missile(DataPool<GeneralMissile> pool, int id, EnemyData[] enemies, int planet)
    {
        if (!Exists(pool, id)) return false;
        var shot = pool.buffer[id];
        return shot.life > 0 && Target(shot.target, enemies, planet);
    }
}
