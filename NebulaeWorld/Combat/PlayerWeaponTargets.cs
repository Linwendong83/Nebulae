namespace NebulaeWorld.Combat;

internal static class PlayerWeaponTargets
{
    public static bool IsMechaAmmo(EAmmoType ammo) => ammo is EAmmoType.Bullet or EAmmoType.Laser or EAmmoType.Cannon or EAmmoType.Plasma or EAmmoType.Missile;
    public static float Range(float range, bool manual) => range * (manual ? 3f : 1f);

    // Native SearchTargetManually's local branch finds relays through the planet's bases.
    // They remain sector enemies addressed by originAstroId, even while over this planet.
    public static bool IsGroundRelay(PlanetFactory factory, in EnemyData enemy)
    {
        if (factory == null || enemy.id <= 0 || enemy.dfRelayId <= 0 || enemy.astroId != factory.planetId) return false;
        var bases = factory.enemySystem?.bases;
        if (bases == null) return false;
        for (var id = 1; id < bases.cursor; id++)
            if (bases.buffer[id] is { } component && component.id == id && component.relayEnemyId == enemy.id) return true;
        return false;
    }

    public static bool CanAttackGroundRelay(global::Player player, PlanetFactory factory, in EnemyData enemy, bool manual) =>
        manual && !player.sailing && !player.warping && !enemy.isInvincible && IsGroundRelay(factory, enemy) &&
        player.position.magnitude - factory.planet.realRadius < 150f;

    public static bool GroundRelayInRange(PlayerAction_Combat action, Mecha mecha, in EnemyData enemy, EAmmoType ammo, bool manual)
    {
        var range = Range(action.GetAmmoLocalAttackRange(ammo), manual);
        var origin = (mecha.skillCastLeftL + mecha.skillCastRightL) * 0.5f;
        return range > 0 && (enemy.pos - (VectorLF3)origin).sqrMagnitude <= range * range * 1.05;
    }
}
