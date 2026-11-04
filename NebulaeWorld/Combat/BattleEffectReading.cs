using System;

namespace NebulaeWorld.Combat;

/// <summary>Typed pool readers avoid boxing every native projectile during effect capture.</summary>
internal static class BattleEffectReading<T> where T : struct
{
    internal static readonly Func<T, int> Caster = CreateCaster();
    internal static readonly Func<T, int> Remaining = CreateRemaining();

    private static Func<T, int> CreateCaster()
    {
        if (typeof(T) == typeof(LocalLaserOneShot))
            return (Func<T, int>)(object)(Func<LocalLaserOneShot, int>)(p => p.caster.id);
        if (typeof(T) == typeof(LocalGeneralProjectile))
            return (Func<T, int>)(object)(Func<LocalGeneralProjectile, int>)(p => p.caster.id);
        if (typeof(T) == typeof(SpaceLaserOneShot))
            return (Func<T, int>)(object)(Func<SpaceLaserOneShot, int>)(p => p.caster.id);
        if (typeof(T) == typeof(GeneralProjectile))
            return (Func<T, int>)(object)(Func<GeneralProjectile, int>)(p => p.caster.id);
        return null;
    }

    private static Func<T, int> CreateRemaining()
    {
        if (typeof(T) == typeof(LocalCannonade))
            return (Func<T, int>)(object)(Func<LocalCannonade, int>)(p => BattleEffectMotion.CannonRemaining(p.progress, p.distance, p.speed));
        if (typeof(T) == typeof(GeneralCannonade))
            return (Func<T, int>)(object)(Func<GeneralCannonade, int>)(p => BattleEffectMotion.CannonRemaining(p.progress, p.distance, p.speed));
        if (typeof(T) == typeof(LocalDisturbingWave))
            return (Func<T, int>)(object)(Func<LocalDisturbingWave, int>)(p => BattleEffectMotion.WaveRemaining(p));
        return null;
    }
}
