using System.Reflection;
using global::NebulaeModel.DataStructures;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
internal class BattleVisualSafetyTest
{
    private static readonly string[] CombatFields = ["damage", "damageIncoming", "damageTotal", "damagePerObject", "abilityValue", "mask"];
    private static readonly MethodInfo Decoder = typeof(BattleVisualRenderer).GetMethod("Decode", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Neutralizer = typeof(BattleVisualRenderer).GetMethod("NeutralizeNative", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static Type NativeType(BattleEffectKind kind) => kind switch
    {
        BattleEffectKind.GroundLaser or BattleEffectKind.RaiderLaser or BattleEffectKind.GroundTowerLaser or BattleEffectKind.MechaGroundLaser => typeof(LocalLaserOneShot),
        BattleEffectKind.GroundPlasma or BattleEffectKind.GroundShieldPlasma or BattleEffectKind.RangerPlasma or BattleEffectKind.GuardianPlasma or
            BattleEffectKind.GroundTowerPlasma or BattleEffectKind.TurretGauss or BattleEffectKind.MechaGroundGauss => typeof(LocalGeneralProjectile),
        BattleEffectKind.SpaceLaser or BattleEffectKind.SpaceTowerLaser or BattleEffectKind.LancerLaser or BattleEffectKind.MechaSpaceLaser => typeof(SpaceLaserOneShot),
        BattleEffectKind.SpacePlasmaF or BattleEffectKind.SpacePlasmaA or BattleEffectKind.SpaceTowerPlasma or BattleEffectKind.LancerPlasma or
            BattleEffectKind.TurretPlasma or BattleEffectKind.TurretLocalPlasma or BattleEffectKind.MechaSpaceGauss or BattleEffectKind.MechaPlasma => typeof(GeneralProjectile),
        BattleEffectKind.TurretMissile or BattleEffectKind.MechaMissile => typeof(GeneralMissile),
        BattleEffectKind.TurretCannon or BattleEffectKind.MechaLocalCannon => typeof(LocalCannonade),
        BattleEffectKind.MechaSpaceCannon => typeof(GeneralCannonade),
        BattleEffectKind.TurretLaser => typeof(LocalLaserContinuous),
        BattleEffectKind.TurretDisturb => typeof(LocalDisturbingWave),
        BattleEffectKind.LancerSweep => typeof(SpaceLaserSweep),
        BattleEffectKind.BomberProjectile => typeof(GeneralExpImpProjectile),
        BattleEffectKind.MechaShieldBurst => typeof(GeneralShieldBurst),
        BattleEffectKind.ExplosiveBomb => typeof(Bomb_Explosive),
        BattleEffectKind.LiquidBomb => typeof(Bomb_Liquid),
        BattleEffectKind.EMBomb => typeof(Bomb_EMCapsule),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Missing native effect fixture")
    };

    private static object Decode(BattleEffectKind kind, Action<BinaryWriter> export)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream)) export(writer);
        return Decoder.Invoke(null, [new BattleEffectData { Kind = kind, Payload = stream.ToArray() }])!;
    }

    [TestMethod]
    public void EveryNativeWeaponVisualNeutralizesCombatFieldsAndHitReferences()
    {
        foreach (BattleEffectKind kind in Enum.GetValues(typeof(BattleEffectKind)))
        {
            if (kind == BattleEffectKind.Impact) continue;
            var type = NativeType(kind);
            var armed = Activator.CreateInstance(type)!;
            foreach (var name in CombatFields.Concat(new[] { "id", "life", "hitId", "sfxId" }))
            {
                var field = type.GetField(name);
                if (field != null) field.SetValue(armed, field.FieldType.IsEnum ? Enum.ToObject(field.FieldType, 16) : Convert.ChangeType(123, field.FieldType));
            }
            // Bomb.Import resolves Unity item assets. The game runtime covers that import; here
            // the real neutralizer runs against the armed bomb structs without a scene.
            var visual = kind is BattleEffectKind.ExplosiveBomb or BattleEffectKind.LiquidBomb or BattleEffectKind.EMBomb
                ? Neutralizer.MakeGenericMethod(type).Invoke(null, [armed])!
                : Decode(kind, writer =>
                {
                    type.GetMethod("Export")!.Invoke(armed, [writer]);
                    if (kind == BattleEffectKind.LancerSweep) { writer.Write(1f); writer.Write(2f); writer.Write(3f); }
                });
            TestAssert.AreEqual(type, visual.GetType(), kind.ToString());
            foreach (var name in CombatFields)
            {
                var field = type.GetField(name);
                if (field != null) TestAssert.AreEqual(0L, Convert.ToInt64(field.GetValue(visual)), kind + "." + name);
            }
            if (kind == BattleEffectKind.TurretLaser)
                foreach (var name in new[] { "hitId", "sfxId" })
                    TestAssert.AreEqual(0, type.GetField(name)!.GetValue(visual), kind + "." + name);
        }
    }

    [TestMethod]
    public void ImpactParticlesPreserveIdentityAndDuration()
    {
        var particle = (ParticleData)Decode(BattleEffectKind.Impact,
            w => new ParticleData { id = 1, time = 0, duration = 100 }.Export(w));
        TestAssert.AreEqual(1, particle.id);
        TestAssert.AreEqual(100, particle.duration);
    }

    [TestMethod]
    public void EveryEmittedWeaponKindSurvivesTheWireValidator()
    {
        var frame = new BattleVisualFrame();
        foreach (BattleEffectKind kind in Enum.GetValues(typeof(BattleEffectKind)))
            frame.Effects.Add(new BattleEffectData { Kind = kind, Id = 1, Generation = 1, Life = 15, Payload = new byte[] { 0 } });
        var received = BattleVisualFrame.Import(frame.Export());
        TestAssert.HasCount(frame.Effects.Count, received.Effects);
        for (var i = 0; i < frame.Effects.Count; i++) TestAssert.AreEqual(frame.Effects[i].Kind, received.Effects[i].Kind);
        frame.Effects[0].Kind = (BattleEffectKind)255;
        TestAssert.ThrowsExactly<InvalidDataException>(() => BattleVisualFrame.Import(frame.Export()));
    }

    [TestMethod]
    public void ShortLaserFlashesSurviveLatencyAndClientClockLead()
    {
        foreach (var kind in new[] { BattleEffectKind.MechaGroundLaser, BattleEffectKind.RaiderLaser,
                     BattleEffectKind.GroundTowerLaser, BattleEffectKind.MechaSpaceLaser, BattleEffectKind.LancerLaser })
        {
            const long receiptTick = 120;
            var timeline = new NebulaeWorld.Combat.BattleEffectTimeline();
            timeline.Observe(kind, 100, 15, receiptTick);
            TestAssert.AreEqual(15, timeline.Remaining(receiptTick), "A flash starts at receipt even when the client clock leads the host.");
            TestAssert.AreEqual(15, NebulaeWorld.Combat.BattleVisualManager.RemainingLife(kind, -15));
        }
    }
}
