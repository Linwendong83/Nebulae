using System.Reflection;
using global::NebulaeModel.DataStructures;
using HarmonyLib;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Combat;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class CombatPresentationRegressionTest
{
    [TestMethod]
    [DataRow(false, 0d)]
    [DataRow(false, 2d)]
    [DataRow(true, 1d)]
    [DataRow(true, 3d)]
    public void ShieldSettingsUseTheExistingCommandAndPreserveOtherFlags(bool enabled, double speed)
    {
        const int originalFlags = 0x1FFFF;
        var flags = CombatShieldSettings.Encode(originalFlags, enabled);
        var bytes = new CombatIntent(CombatIntentKind.Settings, flags, 123, 7, speed).Encode();
        TestAssert.HasCount(29, bytes);
        TestAssert.AreEqual((byte)CombatIntentKind.Settings, bytes[0]);
        TestAssert.IsTrue(CombatIntent.TryDecode(bytes, out var intent));
        TestAssert.IsTrue(CombatShieldSettings.TryDecode(intent, out var decoded, out var decodedSpeed));
        TestAssert.AreEqual(enabled, decoded);
        TestAssert.AreEqual((float)speed, decodedSpeed);
        TestAssert.AreEqual(originalFlags, intent.Argument & 0x1FFFF);
        TestAssert.AreEqual(originalFlags | (enabled ? CombatShieldSettings.Enabled : 0), intent.Argument);
    }

    [TestMethod]
    public void ShieldSettingsRejectInvalidSpeedsWithEitherSwitchValue()
    {
        foreach (var enabled in new[] { false, true })
            foreach (var invalid in new[] { -1d, 3.6, double.NaN, double.NegativeInfinity, double.PositiveInfinity })
                TestAssert.IsFalse(CombatShieldSettings.TryDecode(new CombatIntent(CombatIntentKind.Settings,
                    CombatShieldSettings.Encode(7, enabled), z: invalid), out _, out _));
    }

    [TestMethod]
    public void CombatBonesStayInTheAcceptedPlanetAndUniverseFrames()
    {
        var offset = new Vector3(0, 1.5f, 0);
        var local = RemoteCombatPose.LocalPoint(new Vector3(0, 202, 0), Quaternion.identity, offset);
        var universal = RemoteCombatPose.UniversalPoint(new VectorLF3(100000000, 200000000, -300000000), Quaternion.identity, offset);
        TestAssert.AreEqual(203.5f, local.y);
        TestAssert.AreEqual(200000001.5, universal.y);
        TestAssert.AreEqual(100000000d, universal.x);
        var halfTurn = new Quaternion(0, 0, 1, 0);
        TestAssert.AreEqual(200.5f, RemoteCombatPose.LocalPoint(new Vector3(0, 202, 0), halfTurn, offset).y);
    }

    [TestMethod]
    public void GroundAndSpaceVelocityUnitsMatchTheirNativeTravel()
    {
        var sample = new LocalGeneralProjectile { pos = new Vector3(10, 0, 0), dir = Vector3.right, speed = 2 };
        TestAssert.AreEqual(22f, BattleEffectMotion.LocalPosition(sample, 6).x);
        TestAssert.AreEqual(23f, BattleEffectMotion.LocalPosition(sample, 6.5).x);
        TestAssert.AreEqual(22d, BattleEffectMotion.UniversalPosition(new VectorLF3(10, 0, 0), new Vector3(120, 0, 0), 6).x);
        TestAssert.AreEqual(23d, BattleEffectMotion.UniversalPosition(new VectorLF3(10, 0, 0), new Vector3(120, 0, 0), 6.5).x);
        TestAssert.AreEqual(23f, BattleEffectMotion.CannonProgress(10, 100, 2, 6.5));
        TestAssert.AreEqual(45, BattleEffectMotion.CannonRemaining(10, 100, 2));
        TestAssert.AreEqual(100f, BattleEffectMotion.CannonProgress(90, 100, 2, 12));
    }

    [TestMethod]
    public void DelayedRenderingInterpolatesSnapshotsAndBoundsMissingPacketMotion()
    {
        var time = new BattleEffectTimeline();
        time.Observe(BattleEffectKind.MechaMissile, 100, 60, 100);
        time.Observe(BattleEffectKind.MechaMissile, 106, 54, 106);
        var render = time.RenderTick(109);
        TestAssert.AreEqual(103d, render);
        TestAssert.AreEqual(0.5f, time.Blend(render));
        TestAssert.AreEqual(0d, time.Age(render));
        TestAssert.AreEqual(2.5, time.Age(time.RenderTick(114.5)));
        TestAssert.AreEqual(12d, time.Age(140));
        TestAssert.AreEqual(0, time.Remaining(160));
        TestAssert.IsFalse(time.Observe(BattleEffectKind.MechaMissile, 105, 55, 200));
        TestAssert.AreEqual(106L, time.SampleTick);
    }

    [TestMethod]
    public void ADelayedFlashGetsOneLeaseAndRepeatedSamplesCannotRestartIt()
    {
        var time = new BattleEffectTimeline();
        time.Observe(BattleEffectKind.RaiderLaser, 100, 15, 130);
        TestAssert.AreEqual(15, time.Remaining(130));
        time.Observe(BattleEffectKind.RaiderLaser, 106, 9, 140);
        TestAssert.AreEqual(145d, time.ExpiresAt);
        TestAssert.AreEqual(0, time.Remaining(145));
        TestAssert.AreEqual(140d, time.RenderTick(140));
    }

    [TestMethod]
    public void DrainingADelayedStartAndEndTogetherCannotEraseAFlash()
    {
        var time = new BattleEffectTimeline();
        time.Observe(BattleEffectKind.RaiderLaser, 100, 15, 100);
        time.Despawn(106);
        TestAssert.IsGreaterThan(0, time.Remaining(106));
        TestAssert.AreEqual(0, time.Remaining(115));
    }

    [TestMethod]
    public void ShortProjectileSpawnAndRemovalFollowTheSameDelayedClock()
    {
        var time = new BattleEffectTimeline();
        time.Observe(BattleEffectKind.RangerPlasma, 100, 5, 100);
        time.Despawn(104);
        TestAssert.IsFalse(time.IsBorn(time.RenderTick(105)), "Do not hold a projectile motionless at its spawn point before playback starts.");
        TestAssert.IsTrue(time.IsBorn(time.RenderTick(107)));
        TestAssert.IsFalse(time.IsDespawned(time.RenderTick(109)), "An end event must not erase a short flight before delayed playback reaches it.");
        TestAssert.IsTrue(time.IsDespawned(time.RenderTick(110)));
        TestAssert.IsGreaterThan(0, time.Remaining(time.RenderTick(109)));
    }

    [TestMethod]
    [DataRow(30d)]
    [DataRow(60d)]
    public void RenderClockMovesBetweenSimulationTicksWithoutGoingBackwards(double ups)
    {
        var clock = new BattleVisualClock();
        clock.Sample(100, 10, ups);
        var early = clock.Sample(100, 10.005, ups);
        var late = clock.Sample(100, 10.01, ups);
        TestAssert.IsGreaterThan(early, late);
        TestAssert.AreEqual(100 + ups * 0.01, late, 1e-9);
        TestAssert.AreEqual(late, clock.Sample(100, 10.01, ups));
        TestAssert.IsGreaterThanOrEqualTo(late, clock.Sample(101, 10.02, ups));
    }

    [TestMethod]
    public void ReplicatedImpactsKeepTheFrameTheSimulationUsed()
    {
        // The hit-flash shader converts astro-local coordinates through the global astro buffer
        // every frame. A replicated particle must therefore travel with its astroId intact, and
        // only a world-frame particle may carry the upos the client re-derives its pos from.
        var planetary = new ParticleData { id = 7, time = 3, duration = 90, astroId = 101, pos = new Vector3(1, 2, 3), vel = new Vector3(4, 5, 6) };
        var universal = new ParticleData { id = 8, time = 5, duration = 80, astroId = 0, upos = new VectorLF3(40000, 0, -12000), pos = new Vector3(7, 8, 9) };
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            planetary.Export(writer);
            universal.Export(writer);
        }
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var planetaryCopy = new ParticleData(); planetaryCopy.Import(reader);
        var universalCopy = new ParticleData(); universalCopy.Import(reader);
        TestAssert.AreEqual(101, planetaryCopy.astroId, "An astro-local flash must keep its astroId so the shader can convert it.");
        TestAssert.AreEqual(1f, planetaryCopy.pos.x);
        TestAssert.AreEqual(2f, planetaryCopy.pos.y);
        TestAssert.AreEqual(3f, planetaryCopy.pos.z);
        TestAssert.AreEqual(4f, planetaryCopy.vel.x);
        TestAssert.AreEqual(0, universalCopy.astroId);
        TestAssert.AreEqual(40000d, universalCopy.upos.x);
        TestAssert.AreEqual(0d, universalCopy.upos.y);
        TestAssert.AreEqual(-12000d, universalCopy.upos.z);
        TestAssert.AreEqual(9f, universalCopy.pos.z);
    }

    [TestMethod]
    [DataRow(0, 90, 0d, 6)]
    [DataRow(3, 90, 12d, 21)]
    [DataRow(3, 90, 60d, 69)]
    [DataRow(3, 90, 200d, 90)]
    [DataRow(3, 0, 200d, 209)]
    public void AReplicatedFlashClockMatchesTheFrameItsPositionIsDrawnIn(int captured, int duration, double age, int expected)
    {
        // The flash position is expressed in the current render frame, so the shared playback delay
        // has to come back out of its own clock; otherwise the vel * f(t) drift lags behind the
        // position and drags the flash off a fast-moving shield.
        TestAssert.AreEqual(expected, BattleVisualRenderer.FlashClock(captured, duration, age));
    }

    [TestMethod]
    public void EveryShieldHitProducerIsCapturedForReplication()
    {
        // hitEffects[8] — the shield flash — is added inside these seven TickSkillLogic overloads,
        // wherever an enemy shot or laser meets the player's shield. Clients never run the skill tick,
        // so an unpatched producer means the shield is silent for everyone but the host.
        var producers = new[]
        {
            typeof(LocalGeneralProjectile), typeof(LocalLaserOneShot), typeof(SpaceLaserOneShot),
            typeof(GeneralProjectile), typeof(GeneralMissile), typeof(SpaceLaserSweep), typeof(GeneralExpImpProjectile)
        };
        var patched = PatchedMembers("NebulaePatcher.Patches.Dynamic.Combat.BattleImpact_Patch");
        foreach (var producer in producers)
            TestAssert.Contains(producer, patched, producer.Name + " must snapshot its hit effects.");
    }

    private static HashSet<object> PatchedMembers(string typeName, Func<HarmonyPatch, object>? select = null)
    {
        select ??= a => a.info.declaringType;
        var patch = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType(typeName, true)!;
        return patch.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(m => m.GetCustomAttributes<HarmonyPatch>())
            .Select(select)
            .ToHashSet();
    }
}
