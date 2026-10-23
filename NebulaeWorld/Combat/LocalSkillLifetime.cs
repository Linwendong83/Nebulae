
namespace NebulaeWorld.Combat;

/// <summary>Validate local skill references before native code dereferences a pool or released player.</summary>
internal static class LocalSkillLifetime
{
    public static bool IsValid(SkillSystem skills, int astro, SkillTargetLocal caster, SkillTargetLocal target)
    {
        if (skills?.astroFactories == null || astro <= 0 || astro >= skills.astroFactories.Length) return false;
        var factory = skills.astroFactories[astro];
        return factory != null && ValidTarget(factory, caster, false) && ValidTarget(factory, target, true);
    }

    private static bool ValidTarget(PlanetFactory factory, SkillTargetLocal target, bool mayMiss)
    {
        if (target.id == 0) return mayMiss;
        if (target.id < 0) return false;
        switch (target.type)
        {
            case ETargetType.Player:
                return CombatTargetContext.IsPlayerTarget(target.id) &&
                (!mayMiss || GroundTurretCombat.TryPlayer(factory, target.id, out _));
            case ETargetType.Enemy: return target.id < factory.enemyCursor && factory.enemyPool[target.id].id == target.id;
            case ETargetType.Craft: return target.id < factory.craftCursor && factory.craftPool[target.id].id == target.id;
            case ETargetType.None: return target.id < factory.entityCursor && factory.entityPool[target.id].id == target.id;
            case ETargetType.Vegetable: return target.id < factory.vegeCursor && factory.vegePool[target.id].id == target.id;
            case ETargetType.Vein: return target.id < factory.veinCursor && factory.veinPool[target.id].id == target.id;
            default: return false;
        }
    }

    public static void RemovePlayerReferences(SkillSystem skills, int playerId)
    {
        if (skills == null) return;
        for (var id = 1; id < skills.localLaserOneShots.cursor; id++)
            if (skills.localLaserOneShots.buffer[id].id == id && References(skills.localLaserOneShots.buffer[id].caster, skills.localLaserOneShots.buffer[id].target, playerId)) skills.localLaserOneShots.Remove(id);
        for (var id = 1; id < skills.raiderLasers.cursor; id++)
            if (skills.raiderLasers.buffer[id].id == id && References(skills.raiderLasers.buffer[id].caster, skills.raiderLasers.buffer[id].target, playerId)) skills.raiderLasers.Remove(id);
        for (var id = 1; id < skills.dfgTowerLasers.cursor; id++)
            if (skills.dfgTowerLasers.buffer[id].id == id && References(skills.dfgTowerLasers.buffer[id].caster, skills.dfgTowerLasers.buffer[id].target, playerId)) skills.dfgTowerLasers.Remove(id);
        for (var id = 1; id < skills.mechaLocalLaserOneShots.cursor; id++)
            if (skills.mechaLocalLaserOneShots.buffer[id].id == id && References(skills.mechaLocalLaserOneShots.buffer[id].caster, skills.mechaLocalLaserOneShots.buffer[id].target, playerId)) skills.mechaLocalLaserOneShots.Remove(id);
        // Other local projectile kinds are guarded before their next native tick.
    }

    private static bool References(SkillTargetLocal caster, SkillTargetLocal target, int playerId) =>
        caster.type == ETargetType.Player && caster.id == playerId || target.type == ETargetType.Player && target.id == playerId;
}
