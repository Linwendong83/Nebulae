using NebulaeWorld.Authority.Adapters;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// The turret aim animation the host derives from the component, mirroring the copy vanilla writes
/// into the enemy animation pool (which the barrel shader reads as an aim rotation).
/// </summary>
[TestClass]
public class EnemyTurretAnimationTest
{
    [TestMethod]
    public void AnAimingTurretPublishesItsLocalDirAndFireCycle()
    {
        // Vanilla: cycle = -fire/interval = 0.5, heat 0 leaves its factor at 2, aiming in range adds the
        // clamped (sensor - reach)/(sensor - attack) = 1, so time = 1.5 + 10.
        var anim = EnemyTurretAnimation.Resolve(new Vector3(0.6f, 0f, 0.8f), EDFTurretState.Aiming,
            fire: -30, heat: 0, sensorRange: 100f, attackRange: 50f, attackInterval: 60f);
        TestAssert.AreEqual(0.6f, anim.prepare_length, 1e-6f);
        TestAssert.AreEqual(0f, anim.working_length, 1e-6f);
        TestAssert.AreEqual(0.8f, anim.power, 1e-6f);
        TestAssert.AreEqual(2u, anim.state);
        TestAssert.AreEqual(11.5f, anim.time, 1e-6f);
    }

    [TestMethod]
    public void ASearchingTurretHasNoFireCycle()
    {
        var anim = EnemyTurretAnimation.Resolve(Vector3.right, EDFTurretState.Searching,
            fire: 0, heat: 0, sensorRange: 100f, attackRange: 50f, attackInterval: 60f);
        // Searching adds no range factor, so only the +10 offset remains.
        TestAssert.AreEqual(10f, anim.time, 1e-6f);
        TestAssert.AreEqual(1f, anim.prepare_length, 1e-6f);
        TestAssert.AreEqual(0f, anim.working_length, 1e-6f);
    }

    [TestMethod]
    public void HeatAndReloadShapeTheCycleLikeVanilla()
    {
        // Overheated (heat < 0) caps the added factor at the heat fraction instead of the range factor.
        var hot = EnemyTurretAnimation.Resolve(new Vector3(0f, 0f, 1f), EDFTurretState.Aiming,
            fire: -60, heat: -1800, sensorRange: 100f, attackRange: 50f, attackInterval: 60f);
        TestAssert.AreEqual(11.5f, hot.time, 1e-6f, "cycle 1 + min(heat 0.5, range 1) = 1.5, plus the offset.");
        // A reloading turret (fire > 0) has a negative cycle that only the -1 floor bounds.
        var reloading = EnemyTurretAnimation.Resolve(Vector3.forward, EDFTurretState.Aiming,
            fire: 600, heat: 0, sensorRange: 100f, attackRange: 50f, attackInterval: 60f);
        TestAssert.AreEqual(9f, reloading.time, 1e-6f);
    }

    [TestMethod]
    public void ADegeneratePrefabCannotPutNanOnTheWire()
    {
        var anim = EnemyTurretAnimation.Resolve(Vector3.zero, EDFTurretState.Aiming,
            fire: 0, heat: 0, sensorRange: 50f, attackRange: 50f, attackInterval: 0f);
        TestAssert.IsFalse(float.IsNaN(anim.time));
        TestAssert.IsFalse(float.IsNaN(anim.prepare_length));
        TestAssert.IsFalse(float.IsNaN(anim.power));
    }
}
