using global::NebulaeModel.DataStructures;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class BattleVisualFrameTest
{
    [TestMethod]
    public void BatchedEventsPreserveIndividualAnchorsAndExplicitEnds()
    {
        var frame = new BattleVisualFrame();
        frame.Effects.Add(new BattleEffectData
        { Kind = BattleEffectKind.TurretMissile, Id = 9, Generation = 20, Tick = 100, Life = 60, SourceAstroId = 101, Payload = [1, 2, 3] });
        frame.Effects.Add(new BattleEffectData
        { Kind = BattleEffectKind.TurretMissile, Id = 9, Generation = 20, Tick = 120, End = true, Payload = [] });
        var restored = BattleVisualFrame.Import(frame.Export());
        TestAssert.AreEqual(100L, restored.Effects[0].Tick);
        TestAssert.AreEqual(101, restored.Effects[0].SourceAstroId);
        TestAssert.AreEqual(120L, restored.Effects[1].Tick);
        TestAssert.IsTrue(restored.Effects[1].End);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, restored.Effects[0].Payload);
        TestAssert.IsEmpty(restored.Effects[1].Payload);
        TestAssert.IsEmpty(BattleVisualFrame.Import(new BattleVisualFrame().Export()).Effects);
    }

    [TestMethod]
    public void OnlyReadyLocalTurretsReplaceTheirNetworkEffects()
    {
        foreach (var kind in Enum.GetValues(typeof(BattleEffectKind)).Cast<BattleEffectKind>().Where(BattleEffectData.IsTurret))
        {
            var effect = new BattleEffectData { Kind = kind, SourceAstroId = 101 };
            TestAssert.IsFalse(effect.NeedsReplication(101, true));
            TestAssert.IsTrue(effect.NeedsReplication(102, true));
            TestAssert.IsTrue(effect.NeedsReplication(101, false));
        }
        TestAssert.IsTrue(new BattleEffectData { Kind = BattleEffectKind.Impact, SourceAstroId = 101 }.NeedsReplication(101, true));
    }

    [TestMethod]
    public void ReadyNativeEnemiesReplaceUnitEffectsWhileStaticTowersKeepTheirEvents()
    {
        TestAssert.IsTrue(NebulaeWorld.Combat.BattleVisualManager.NativeEnemyEffect(BattleEffectKind.RaiderLaser, 101, 101, true, false));
        TestAssert.IsFalse(NebulaeWorld.Combat.BattleVisualManager.NativeEnemyEffect(BattleEffectKind.RaiderLaser, 101, 102, true, false));
        TestAssert.IsFalse(NebulaeWorld.Combat.BattleVisualManager.NativeEnemyEffect(BattleEffectKind.RaiderLaser, 101, 101, false, false));
        TestAssert.IsTrue(NebulaeWorld.Combat.BattleVisualManager.NativeEnemyEffect(BattleEffectKind.LancerPlasma, 1000001, 101, false, true));
        TestAssert.IsFalse(NebulaeWorld.Combat.BattleVisualManager.NativeEnemyEffect(BattleEffectKind.GroundTowerLaser, 101, 101, true, true));
        TestAssert.IsFalse(NebulaeWorld.Combat.BattleVisualManager.NativeEnemyEffect(BattleEffectKind.Impact, 101, 101, true, true));
    }

    [TestMethod]
    public void MalformedEventsCannotInstallAnUnknownGenerationOrHidePayloadInAnEnd()
    {
        var frame = new BattleVisualFrame();
        var effect = new BattleEffectData
        { Kind = BattleEffectKind.TurretGauss, Id = 1, Generation = 1, Life = 60, Payload = [1, 2, 3] };
        frame.Effects.Add(effect);
        var bytes = frame.Export();
        TestAssert.Throws<InvalidDataException>(() => BattleVisualFrame.Import(bytes.Take(bytes.Length - 2).ToArray()));
        TestAssert.Throws<InvalidDataException>(() => BattleVisualFrame.Import(bytes.Concat(new byte[] { 0 }).ToArray()));
        effect.Generation = 0;
        TestAssert.Throws<InvalidDataException>(() => BattleVisualFrame.Import(frame.Export()));
        effect.Generation = 1; effect.End = true;
        TestAssert.Throws<InvalidDataException>(() => BattleVisualFrame.Import(frame.Export()));
        effect.Payload = []; effect.Life = 0; effect.Tick = -1;
        TestAssert.Throws<InvalidDataException>(() => BattleVisualFrame.Import(frame.Export()));
    }
}
