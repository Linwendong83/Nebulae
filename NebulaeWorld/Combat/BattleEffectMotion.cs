using System;
using UnityEngine;

namespace NebulaeWorld.Combat;

internal static class BattleEffectMotion
{
    public static Vector3 LocalPosition(LocalGeneralProjectile sample, double ageTicks) => sample.pos + sample.dir * (sample.speed * (float)ageTicks);
    public static VectorLF3 UniversalPosition(VectorLF3 position, Vector3 velocity, double ageTicks) => position + (VectorLF3)velocity * (ageTicks / 60);
    public static float CannonProgress(float progress, float distance, float speed, double ageTicks) => Math.Min(distance, progress + speed * (float)ageTicks);
    public static int CannonRemaining(float progress, float distance, float speed) => speed > 0
        ? (int)Math.Min(360000, Math.Max(0, Math.Ceiling((distance - progress) / speed))) : 0;
    public static int WaveRemaining(LocalDisturbingWave wave) => wave.diffusionSpeed > 0
        ? (int)Math.Min(360000, Math.Max(0, Math.Ceiling((wave.diffusionMaxRadius + wave.thickness * 0.5f - wave.currentDiffuseRadius) * 60 / wave.diffusionSpeed))) : 0;
}
