using global::NebulaeModel.DataStructures;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class BattleAudioTest
{
    [TestMethod]
    public void RepeatedFullFramesAndLateOldGenerationsCannotReplayAnAttack()
    {
        var gate = new BattleAudioGate();
        TestAssert.IsTrue(gate.TryPlay(BattleEffectKind.RaiderLaser, 4, 100));
        TestAssert.IsFalse(gate.TryPlay(BattleEffectKind.RaiderLaser, 4, 100));
        TestAssert.IsTrue(gate.TryPlay(BattleEffectKind.RaiderLaser, 4, 101));
        TestAssert.IsFalse(gate.TryPlay(BattleEffectKind.RaiderLaser, 4, 100));
        TestAssert.IsTrue(gate.TryPlay(BattleEffectKind.MechaGroundLaser, 4, 100));
    }

    [TestMethod]
    public void IndependentImpactPoolsAndSourcesCanShareNativeIds()
    {
        var source = new BattleAudioGate();
        TestAssert.IsTrue(source.TryPlay(BattleEffectKind.Impact, 1, 500, 20));
        TestAssert.IsTrue(source.TryPlay(BattleEffectKind.Impact, 1, 400, 19));
        TestAssert.IsFalse(source.TryPlay(BattleEffectKind.Impact, 1, 500, 20));
        TestAssert.IsTrue(new BattleAudioGate().TryPlay(BattleEffectKind.Impact, 1, 500, 20));
    }

    [TestMethod]
    public void NearbyCoordinatesOnDifferentPlanetsAreNotAudible()
    {
        TestAssert.IsTrue(BattleAudioPlayback.Audible(101, 101, 10));
        TestAssert.IsFalse(BattleAudioPlayback.Audible(102, 101, 10));
        TestAssert.IsFalse(BattleAudioPlayback.Audible(101, 0, 10));
        TestAssert.IsFalse(BattleAudioPlayback.Audible(101, 101, 120));
        TestAssert.IsFalse(BattleAudioPlayback.Audible(0, 101, double.NaN));
        TestAssert.IsTrue(BattleAudioPlayback.Audible(0, 0, 10));
    }

    [TestMethod]
    public void GroundAndSpaceVariantsUseTheSamePlayerWeaponSound()
    {
        TestAssert.AreEqual(312, BattleAudioPlayback.PlayerProto(BattleEffectKind.MechaGroundLaser));
        TestAssert.AreEqual(BattleAudioPlayback.PlayerProto(BattleEffectKind.MechaGroundLaser), BattleAudioPlayback.PlayerProto(BattleEffectKind.MechaSpaceLaser));
        TestAssert.AreEqual(313, BattleAudioPlayback.PlayerProto(BattleEffectKind.MechaGroundGauss));
        TestAssert.AreEqual(BattleAudioPlayback.PlayerProto(BattleEffectKind.MechaGroundGauss), BattleAudioPlayback.PlayerProto(BattleEffectKind.MechaSpaceGauss));
        TestAssert.AreEqual(0, BattleAudioPlayback.PlayerProto(BattleEffectKind.RaiderLaser));
    }
}
