namespace NebulaeWorld.Combat;

internal static class PlayerWeaponTargets
{
    public static bool IsMechaAmmo(EAmmoType ammo) => ammo is EAmmoType.Bullet or EAmmoType.Laser or EAmmoType.Cannon or EAmmoType.Plasma or EAmmoType.Missile;
    public static float Range(float range, bool manual) => range * (manual ? 3f : 1f);
}
