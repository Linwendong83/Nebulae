using global::NebulaeModel.DataStructures;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using CollectionAssert = Microsoft.VisualStudio.TestTools.UnitTesting.CollectionAssert;

namespace NebulaeTests.Authority;

[TestClass]
internal class BattleEffectStreamTest
{
    private static void Observe(BattleEffectStream stream, BattleEffectKind kind, long tick, int nativeLife, int life, byte payload = 7)
    {
        if (stream.Observe(kind, 1, 1, nativeLife, tick, out var identity))
            stream.Capture(new BattleEffectData
            { Kind = kind, Id = 1, Generation = identity, Tick = tick, Life = life, Payload = [payload] });
    }

    [TestMethod]
    public void LocallyComputedFlightProducesOneStartAndOneEarlyEnd()
    {
        var stream = new BattleEffectStream();
        for (var tick = 100; tick < 180; tick++)
        {
            stream.BeginFrame();
            Observe(stream, BattleEffectKind.TurretGauss, tick, 200 - tick, 200 - tick);
            stream.EndFrame(tick);
            TestAssert.HasCount(tick == 100 ? 1 : 0, stream.Changes);
        }
        var baseline = stream.Snapshot(1, 180).Single();
        TestAssert.AreEqual(100L, baseline.Tick, "Join-in-flight keeps the original position's time anchor.");
        stream.BeginFrame();
        stream.EndFrame(180);
        var ended = stream.Changes.Single().Effect;
        TestAssert.IsTrue(ended.End);
        TestAssert.AreEqual(baseline.Generation, ended.Generation);
        TestAssert.AreEqual(180L, ended.Tick);
        TestAssert.AreEqual(0, stream.ActiveCount);
        TestAssert.IsEmpty(stream.Snapshot(1, 181));
    }

    [TestMethod]
    public void ReusedNativeSlotEndsTheOldGenerationBeforeTheNewLaunch()
    {
        var stream = new BattleEffectStream();
        Observe(stream, BattleEffectKind.RaiderLaser, 100, -2, 2);
        var original = stream.Changes.Single().Effect.Generation;
        stream.BeginFrame();
        Observe(stream, BattleEffectKind.RaiderLaser, 101, -15, 15);
        stream.EndFrame(101);
        TestAssert.HasCount(2, stream.Changes);
        TestAssert.IsTrue(stream.Changes[0].Effect.End);
        TestAssert.AreEqual(original, stream.Changes[0].Effect.Generation);
        TestAssert.IsGreaterThan(original, stream.Changes[1].Effect.Generation);
    }

    [TestMethod]
    public void EqualCountdownDetectsSameTickReuseButConstantPhaseDoesNot()
    {
        var stream = new BattleEffectStream();
        Observe(stream, BattleEffectKind.TurretGauss, 100, 20, 20);
        stream.BeginFrame();
        Observe(stream, BattleEffectKind.TurretGauss, 101, 20, 20);
        TestAssert.HasCount(2, stream.Changes);
        TestAssert.IsTrue(stream.Changes[0].Effect.End);
        stream = new BattleEffectStream();
        Observe(stream, BattleEffectKind.TurretCannon, 100, 2, 60);
        var generation = stream.Changes.Single().Effect.Generation;
        stream.BeginFrame();
        Observe(stream, BattleEffectKind.TurretCannon, 101, 2, 59);
        TestAssert.IsEmpty(stream.Changes);
        Observe(stream, BattleEffectKind.TurretCannon, 102, 1, 58);
        TestAssert.AreEqual(generation, stream.Changes.Single().Effect.Generation);
        TestAssert.IsFalse(stream.Changes[0].Effect.End);
    }

    [TestMethod]
    public void AContinuousBeamStaysAliveWithoutRepeatedPayloadsAndStopsExplicitly()
    {
        var stream = new BattleEffectStream();
        Observe(stream, BattleEffectKind.TurretLaser, 100, 1, 0);
        var generation = stream.Changes.Single().Effect.Generation;
        for (var tick = 101; tick <= 1000; tick++)
        {
            stream.BeginFrame();
            Observe(stream, BattleEffectKind.TurretLaser, tick, 1, 0);
            stream.EndFrame(tick);
            TestAssert.IsEmpty(stream.Changes);
        }
        TestAssert.AreEqual(generation, stream.Snapshot(1, 1000).Single().Generation);
        stream.BeginFrame(); stream.EndFrame(1001);
        TestAssert.IsTrue(stream.Changes.Single().Effect.End);
        var timeline = new BattleEffectTimeline();
        timeline.Observe(BattleEffectKind.TurretLaser, 100, 0, 100);
        TestAssert.IsGreaterThan(0, timeline.Remaining(1000));
        timeline.Despawn(1001);
        TestAssert.IsTrue(timeline.IsDespawned(1001));
    }

    [TestMethod]
    public void ABufferSuppliedByTheCallerIsCopiedBeforeItIsRetained()
    {
        // The host exports every observed effect into one reused buffer, so a published payload must
        // be copied rather than referenced: overwriting that buffer for the next effect would
        // otherwise rewrite the bytes of an effect the stream still holds, and the corruption would
        // only surface as a wrong-looking beam much later.
        var stream = new BattleEffectStream();
        stream.BeginFrame();
        TestAssert.IsTrue(stream.Observe(BattleEffectKind.TurretLaser, 1, 1, 5, 100, out var generation));
        var buffer = new byte[4];
        buffer[0] = 9; buffer[1] = 8; buffer[2] = 7; buffer[3] = 6;
        stream.Capture(BattleEffectKind.TurretLaser, 1, generation, 100, 0, 0, buffer, 4);
        var published = stream.Changes.Single().Effect;
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7, 6 }, published.Payload);

        // Overwrite the caller's buffer exactly as the next effect's export would.
        buffer[0] = 1; buffer[1] = 2; buffer[2] = 3; buffer[3] = 4;
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7, 6 }, published.Payload,
            "The retained payload must be an owned copy, not a view of the caller's buffer.");
    }

    [TestMethod]
    public void AStationaryBeamCapturedFromACallerBufferAddsNoFurtherRecord()
    {
        // A stationary continuous beam is re-observed every correction interval only to be discarded
        // as unchanged. The captured tick must still advance even then, or the stream would re-observe
        // the beam every tick instead of every interval and the suppression would cost more than it
        // saves.
        var stream = new BattleEffectStream();
        stream.BeginFrame();
        TestAssert.IsTrue(stream.Observe(BattleEffectKind.TurretLaser, 1, 1, 5, 100, out var generation));
        stream.Capture(BattleEffectKind.TurretLaser, 1, generation, 100, 0, 0, [9, 9, 9], 3);
        var first = stream.Changes.Single().Effect;

        for (var tick = 101; tick <= 106; tick++)
        {
            stream.BeginFrame();
            if (!stream.Observe(BattleEffectKind.TurretLaser, 1, 1, 5, tick, out var again)) continue;
            stream.Capture(BattleEffectKind.TurretLaser, 1, again, tick, 0, 0, [9, 9, 9], 3);
        }

        TestAssert.IsEmpty(stream.Changes, "A stationary beam adds no further record.");
        TestAssert.AreSame(first, stream.Snapshot(1, 106).Single(), "The first record stays the retained one.");
    }

    [TestMethod]
    [DataRow(BattleEffectKind.TurretMissile)]
    [DataRow(BattleEffectKind.MechaShieldBurst)]
    public void ExternallyDrivenEffectsKeepCorrectionCadenceWithoutReplacingTheGeneration(BattleEffectKind kind)
    {
        var stream = new BattleEffectStream();
        var captures = new List<BattleEffectData>();
        for (var tick = 100; tick < 120; tick++)
        {
            stream.BeginFrame();
            Observe(stream, kind, tick, kind == BattleEffectKind.TurretMissile ? tick - 99 : 200 - tick, 3600 - tick + 99);
            stream.EndFrame(tick);
            captures.AddRange(stream.Changes.Select(change => change.Effect));
        }
        CollectionAssert.AreEqual(new long[] { 100, 106, 112, 118 }, captures.Select(effect => effect.Tick).ToArray());
        TestAssert.AreEqual(1, captures.Select(effect => effect.Generation).Distinct().Count());
    }

    [TestMethod]
    public void LocalFlightContinuesBeyondTheFormerTwelveTickExtrapolationLimit()
    {
        var timeline = new BattleEffectTimeline();
        timeline.Observe(BattleEffectKind.TurretGauss, 100, 100, 100);
        TestAssert.AreEqual(70d, timeline.Age(170));
        TestAssert.AreEqual(30, timeline.Remaining(170));
        timeline.Despawn(180);
        TestAssert.IsTrue(timeline.IsDespawned(180));
    }

    [TestMethod]
    public void PausingAndChangingUpsDoNotRestartOrEraseLocalPresentation()
    {
        var clock = new BattleVisualClock();
        clock.Sample(100, 1, 60);
        TestAssert.AreEqual(160d, clock.Sample(160, 2, 60));
        TestAssert.AreEqual(160d, clock.Sample(160, 20, 60, paused: true));
        TestAssert.AreEqual(190d, clock.Sample(190, 21, 30));
        TestAssert.AreEqual(220d, clock.Sample(220, 22, 30));
        TestAssert.AreEqual(221d, clock.Sample(220, 122, 30), "A join stall without a pause flag cannot advance visuals by hundreds of ticks.");
        var flash = new BattleEffectTimeline();
        flash.Observe(BattleEffectKind.RaiderLaser, 100, 15, 100);
        TestAssert.AreEqual(15, flash.Remaining(100));
        TestAssert.AreEqual(0, flash.Remaining(115));
    }
}
