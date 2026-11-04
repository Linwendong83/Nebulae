using HarmonyLib;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class BattleEffectReadingTest
{
    [TestMethod]
    public void TypedReadersPreserveLocalAndSpaceCasterIdentity()
    {
        TestAssert.AreEqual(17, BattleEffectReading<LocalLaserOneShot>.Caster(new LocalLaserOneShot { caster = new SkillTargetLocal { id = 17 } }));
        TestAssert.AreEqual(23, BattleEffectReading<LocalGeneralProjectile>.Caster(new LocalGeneralProjectile { caster = new SkillTargetLocal { id = 23 } }));
        TestAssert.AreEqual(31, BattleEffectReading<SpaceLaserOneShot>.Caster(new SpaceLaserOneShot { caster = new SkillTarget { id = 31 } }));
        TestAssert.AreEqual(43, BattleEffectReading<GeneralProjectile>.Caster(new GeneralProjectile { caster = new SkillTarget { id = 43 } }));
        // Other native kinds have always published a zero caster in the outer effect record.
        TestAssert.IsNull(BattleEffectReading<GeneralMissile>.Caster);
        TestAssert.IsNull(BattleEffectReading<LocalLaserContinuous>.Caster);
        TestAssert.IsNull(BattleEffectReading<SpaceLaserSweep>.Caster);
    }

    [TestMethod]
    public void CannonAndWaveLifetimeReadersMatchMotionPolicy()
    {
        foreach (var progress in new[] { 0f, .4f, .99f, 1f })
        {
            var local = new LocalCannonade { progress = progress, distance = 120, speed = 2 };
            var space = new GeneralCannonade { progress = progress, distance = 200, speed = 4 };
            var wave = new LocalDisturbingWave { life = 40, diffusionSpeed = 60, diffusionMaxRadius = 30, currentDiffuseRadius = progress * 30, thickness = 2 };
            TestAssert.AreEqual(BattleEffectMotion.CannonRemaining(local.progress, local.distance, local.speed), BattleEffectReading<LocalCannonade>.Remaining(local));
            TestAssert.AreEqual(BattleEffectMotion.CannonRemaining(space.progress, space.distance, space.speed), BattleEffectReading<GeneralCannonade>.Remaining(space));
            TestAssert.AreEqual(BattleEffectMotion.WaveRemaining(wave), BattleEffectReading<LocalDisturbingWave>.Remaining(wave));
        }
        TestAssert.IsNull(BattleEffectReading<GeneralProjectile>.Remaining);
    }

    [TestMethod]
    public void CaptureLoopDoesNotBoxNativeProjectiles()
    {
        var collect = AccessTools.Method(typeof(BattleVisualManager), "Collect").MakeGenericMethod(typeof(LocalGeneralProjectile));
        TestAssert.IsFalse(PatchProcessor.GetOriginalInstructions(collect).Any(i => i.opcode == System.Reflection.Emit.OpCodes.Box),
            "One box per captured struct becomes sustained allocation under ground and space combat.");
    }
}
