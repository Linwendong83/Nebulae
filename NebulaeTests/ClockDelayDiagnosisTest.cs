using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.GameStates;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

/// <summary>Fault injection against the production clock handler; no Unity scene or wall-clock sleeps.</summary>
[TestClass]
public class ClockDelayDiagnosisTest
{
    private const long OriginMs = 1700000000000;
    private const long OriginTick = 100000;
    private static long nowMs;
    private static double requestedUps;
    private static int commands;
    private Harmony harmony = null!;
    private GameStatesManager manager = null!;
    private MultiplayerSession? previousSession;
    private GameMain? previousInstance;
    private GameData? previousData;
    private double previousSpeed;
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session;
        previousInstance = GameMain.instance;
        previousData = GameMain.data;
        previousSpeed = FPSController.SimulateSpeed;
        var game = Uninitialized<GameMain>();
        AccessTools.Field(typeof(GameMain), "m_CachedPtr").SetValue(game, new IntPtr(1));
        SetStatic(typeof(GameMain), "<instance>k__BackingField", game);
        GameMain.data = null;
        Multiplayer.Session = Uninitialized<MultiplayerSession>();
        manager = new GameStatesManager();
        manager.ClockTime = () => (nowMs - OriginMs) / 1000d;
        Multiplayer.Session.State = manager;
        requestedUps = 60; commands = 0; nowMs = OriginMs;
        harmony = new Harmony("nebulae.tests.clock-delay-diagnosis");
        harmony.Patch(AccessTools.Method(typeof(FPSController), nameof(FPSController.SetFixUPS)),
            prefix: new HarmonyMethod(typeof(ClockDelayDiagnosisTest), nameof(CaptureRate)));
        harmony.Patch(AccessTools.Method(typeof(GameStatesManager), nameof(GameStatesManager.ProcessGameStateUpdatePacket)),
            transpiler: new HarmonyMethod(typeof(ClockDelayDiagnosisTest), nameof(UseSimulatedTime)));
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchSelf();
        Multiplayer.Session = previousSession;
        SetStatic(typeof(GameMain), "<instance>k__BackingField", previousInstance);
        GameMain.data = previousData;
        FPSController.SimulateSpeed = previousSpeed;
    }

    private static IEnumerable<CodeInstruction> UseSimulatedTime(IEnumerable<CodeInstruction> instructions)
    {
        var getter = AccessTools.PropertyGetter(typeof(DateTimeOffset), nameof(DateTimeOffset.UtcNow));
        var replacements = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(getter))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(ClockDelayDiagnosisTest), nameof(Now));
                replacements++;
            }
            yield return instruction;
        }
        if (replacements != 1) throw new InvalidOperationException("Expected exactly one clock read in production handler.");
    }

    private static DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(nowMs);
    private static bool CaptureRate(double _fixups) { requestedUps = _fixups; commands++; return false; }

    private bool Observe(long elapsedMs, long sentMs, long reportedTick, float hostUps, long localTick,
        double queuedSeconds = 0)
    {
        nowMs = OriginMs + elapsedMs;
        GameMain.gameTick = localTick;
        var before = commands;
        manager.ProcessGameStateUpdatePacket(OriginMs + sentMs, reportedTick, hostUps, 1, queuedSeconds);
        TestAssert.AreEqual(localTick, GameMain.gameTick, "Clock feedback must not rewrite executed ticks.");
        return commands != before;
    }

    [TestMethod]
    public void PausedZeroRateSamplesCarryAnAbsoluteLimitWithoutProducingPhaseDebt()
    {
        GameMain.gameTick = OriginTick;
        manager.ProcessGameStateUpdatePacket(OriginMs, OriginTick, 0, 1, paused: true, pauseLimit: OriginTick);
        TestAssert.IsTrue(manager.HostClockPaused);
        TestAssert.IsFalse(manager.CanAdvanceHostClock);
        TestAssert.AreEqual(60d, requestedUps);
        nowMs += 1000;
        manager.ProcessGameStateUpdatePacket(OriginMs + 1000, OriginTick, 60, 1, paused: false);
        TestAssert.IsFalse(manager.HostClockPaused);
        TestAssert.IsTrue(manager.CanAdvanceHostClock);
        TestAssert.AreEqual(60d, requestedUps, "Resuming at the same tick must not incur a 1 UPS correction.");
    }

    [TestMethod]
    public void ReportedHostRateMeasuresWorldTicksRatherThanFixedUpdateCallbacks()
    {
        manager.ObserveHostTick(100);
        nowMs += 1000; manager.ObserveHostTick(105);
        TestAssert.AreEqual(5f, GameStatesManager.RealUPS);
        Multiplayer.Session.WorldPaused = true;
        TestAssert.AreEqual(0f, GameStatesManager.RealUPS);
        manager.ResetHostRate();
        nowMs += 60000; Multiplayer.Session.WorldPaused = false;
        manager.ObserveHostTick(105);
        nowMs += 1000; manager.ObserveHostTick(165);
        TestAssert.AreEqual(60f, GameStatesManager.RealUPS, "An idle pause is excluded from the next running rate.");
    }

    [TestMethod]
    public void ResumeEasesPhaseDebtButStillAdoptsARealRateDropAndExpiresItsGrace()
    {
        Observe(20, 0, OriginTick, 40, OriginTick);
        manager.OnWorldPauseChanged();
        Observe(100, 80, OriginTick + 1, 40, OriginTick + 20);
        TestAssert.AreEqual(30d, requestedUps);
        Observe(200, 180, OriginTick + 2, 4, OriginTick + 20);
        TestAssert.AreEqual(3d, requestedUps, "The real slow host rate is not replaced with the old 40 UPS.");
        Observe(3021, 3001, OriginTick + 3, 40, OriginTick + 200);
        TestAssert.AreEqual(1d, requestedUps, "Large verified debt is not masked indefinitely.");
    }

    [TestMethod]
    public void SaveResumeRetainsTheLastRunningRateUntilANewRunningWindowIsMeasured()
    {
        manager.ObserveHostTick(100); nowMs += 1000; manager.ObserveHostTick(143);
        TestAssert.AreEqual(43f, GameStatesManager.RealUPS);
        Multiplayer.Session.WorldPaused = true; manager.OnWorldPauseChanged();
        nowMs += 20000; manager.ObserveHostTick(143);
        TestAssert.AreEqual(0f, GameStatesManager.RealUPS);
        Multiplayer.Session.WorldPaused = false; manager.OnWorldPauseChanged();
        TestAssert.AreEqual(43f, GameStatesManager.RealUPS);
        manager.ObserveHostTick(143);
        nowMs += 1000; manager.ObserveHostTick(148);
        TestAssert.AreEqual(5f, GameStatesManager.RealUPS, "A genuine post-save slowdown is still measured normally.");
    }

    [TestMethod]
    public void RepliesFromOneStalledTickDoNotProgressivelyTightenThePhaseInterval()
    {
        foreach (var sent in new[] { 0L, 1066, 2078, 3117 })
        {
            Observe(4303, sent, OriginTick, 43, OriginTick + 180, .014);
            TestAssert.AreEqual(43d, requestedUps);
        }
        // A genuinely new, fresh observation can still prove a real lead.
        Observe(4400, 4380, OriginTick + 4, 43, OriginTick + 180, .014);
        TestAssert.AreEqual(1d, requestedUps);
    }

    [TestMethod]
    public void AnExplicitPauseDiscardsThePreviousPhaseCorrectionWithoutChangingTicks()
    {
        Observe(20, 0, OriginTick, 43, OriginTick + 180);
        TestAssert.AreEqual(1d, requestedUps);
        manager.OnWorldPauseChanged();
        TestAssert.AreEqual(43d, requestedUps);
        TestAssert.AreEqual(OriginTick + 180, GameMain.gameTick);
        nowMs += 10000; manager.ServiceClock();
        TestAssert.AreEqual(43d, requestedUps);
    }

    [TestMethod]
    public void SymmetricTwoSecondLatencyDoesNotBrakeAnAlignedClient()
    {
        // Request travels for 1s; the sample is taken halfway through a 2s round trip.
        TestAssert.IsTrue(Observe(2000, 0, OriginTick + 60, 60, OriginTick + 120));
        TestAssert.AreEqual(60d, requestedUps);
    }

    [TestMethod]
    public void TwoSecondsOfReplyQueueingDoesNotBrakeAnAlignedClient()
    {
        // The host takes its sample immediately; only the reply spends 2s in a queue.
        TestAssert.IsTrue(Observe(2000, 0, OriginTick, 60, OriginTick + 120));
        TestAssert.AreEqual(60d, requestedUps);
        TestContext.WriteLine("Actual host/client tick=100120; host interval=100000..100120; requested UPS=60.");
    }

    [TestMethod]
    public void RepeatedQueuedRepliesCannotTeachTheClockToApplyAFalseBrake()
    {
        Observe(20, 0, OriginTick, 60, OriginTick + 1);
        for (var sample = 1; sample <= 4; sample++)
        {
            var time = 2000 + sample * 1000L;
            var actualTick = OriginTick + time * 60 / 1000;
            var accepted = Observe(time, time - 2000, actualTick - 120, 60, actualTick);
            TestAssert.IsTrue(accepted);
            TestAssert.AreEqual(60d, requestedUps);
            TestContext.WriteLine($"reply={sample}, accepted={accepted}, requestedUps={requestedUps}");
        }
        TestAssert.AreEqual(60d, requestedUps);
    }

    [TestMethod]
    public void ARealHostDropToFiveUpsTakesEffectOnTheFirstReply()
    {
        Observe(20, 0, OriginTick, 60, OriginTick + 1);
        for (var sample = 1; sample <= 6; sample++)
        {
            var accepted = Observe(20 + sample * 1000, sample * 1000, OriginTick + sample * 5, 5, OriginTick + sample * 5);
            TestAssert.IsTrue(accepted);
            TestAssert.AreEqual(5d, requestedUps);
            TestContext.WriteLine($"reply={sample}, accepted={accepted}, reportedHostUps=5, requestedUps={requestedUps}");
        }
        TestAssert.AreEqual(5d, requestedUps);
    }

    [TestMethod]
    [DataRow(60f, 200L, 1d)]
    [DataRow(5f, 200L, 1d)]
    [DataRow(60f, -600L, 240d)]
    public void VerifiedPhaseCorrectionExpiresWithoutAnotherSimulationTick(float hostUps, long lead, double correction)
    {
        TestAssert.IsTrue(Observe(20, 0, OriginTick, hostUps, OriginTick + lead));
        TestAssert.AreEqual(correction, requestedUps);
        var tick = GameMain.gameTick;
        nowMs = OriginMs + 3010;
        manager.ServiceClock();
        TestAssert.AreEqual(correction, requestedUps);
        nowMs = OriginMs + 3020;
        manager.ServiceClock();
        TestAssert.AreEqual((double)hostUps, requestedUps, "Expire to the last host rate, including a genuinely slow host.");
        TestAssert.AreEqual(tick, GameMain.gameTick);
        var count = commands;
        manager.ServiceClock();
        TestAssert.AreEqual(count, commands, "Do not keep writing the expired correction every rendered frame.");
    }

    [TestMethod]
    public void DuplicateOrOlderRepliesCannotRenewABrakeOrRestoreAnOldHostRate()
    {
        TestAssert.IsTrue(Observe(20, 0, OriginTick, 5, OriginTick + 200));
        TestAssert.AreEqual(1d, requestedUps);
        TestAssert.IsFalse(Observe(1000, 0, OriginTick, 60, OriginTick + 200));
        TestAssert.IsFalse(Observe(2000, 1900, OriginTick - 1, 60, OriginTick + 200));
        TestAssert.AreEqual(5f, manager.GetServerUPS());
        nowMs = OriginMs + 3020;
        manager.ServiceClock();
        TestAssert.AreEqual(5d, requestedUps);
    }

    [TestMethod]
    public void AnExpiredSampleReportsRateWithoutApplyingAnUncertainPhaseCorrection()
    {
        TestAssert.IsTrue(Observe(20000, 0, OriginTick, 5, OriginTick + 1000));
        TestAssert.AreEqual(5d, requestedUps);
    }

    [TestMethod]
    public void MeasuredLocalQueueAgeCanProveThatTheClientNeedsToCatchUp()
    {
        TestAssert.IsTrue(Observe(2000, 0, OriginTick, 60, OriginTick + 60, queuedSeconds: 2));
        TestAssert.AreEqual(119d, requestedUps);
    }

    [TestMethod]
    public void InvalidSamplesCannotChangeSpeedOrExtendTheCorrection()
    {
        Observe(20, 0, OriginTick, 60, OriginTick + 200);
        var count = commands;
        nowMs = OriginMs + 2000;
        manager.ProcessGameStateUpdatePacket(OriginMs + 1990, OriginTick + 1, float.NaN, 1);
        manager.ProcessGameStateUpdatePacket(OriginMs + 1990, OriginTick + 1, 60, float.PositiveInfinity);
        manager.ProcessGameStateUpdatePacket(OriginMs + 1990, OriginTick + 1, 60, 0);
        manager.ProcessGameStateUpdatePacket(OriginMs + 3000, OriginTick + 1, 60, 1);
        TestAssert.AreEqual(count, commands);
        nowMs = OriginMs + 3020;
        manager.ServiceClock();
        TestAssert.AreEqual(60d, requestedUps);
    }

    [TestMethod]
    public void PausedWorldDefersLeaseExpiryUntilResume()
    {
        Observe(20, 0, OriginTick, 60, OriginTick + 200);
        Multiplayer.Session.WorldPaused = true;
        nowMs = OriginMs + 10000;
        manager.ServiceClock();
        TestAssert.AreEqual(1d, requestedUps);
        Multiplayer.Session.WorldPaused = false;
        manager.ServiceClock();
        TestAssert.AreEqual(60d, requestedUps);
    }

    [TestMethod]
    public void LeaseUsesMonotonicTimeEvenIfTheWallClockMovesBackwards()
    {
        double elapsed = 0;
        manager.ClockTime = () => elapsed;
        Observe(20, 0, OriginTick, 60, OriginTick + 200);
        nowMs -= 60000;
        elapsed = 3;
        manager.ServiceClock();
        TestAssert.AreEqual(60d, requestedUps);
    }

    [TestMethod]
    public void AHostActuallyRunningAtOneUpsIsNotArtificiallyAccelerated()
    {
        Observe(20, 0, OriginTick, 1, OriginTick);
        TestAssert.AreEqual(1d, requestedUps);
        nowMs = OriginMs + 60000;
        manager.ServiceClock();
        TestAssert.AreEqual(1d, requestedUps);
    }

    [TestMethod]
    public void InstallingANewWorldClockClearsThePreviousSampleOrderingAndLease()
    {
        Observe(20, 0, OriginTick + 600, 60, OriginTick);
        AccessTools.Field(typeof(GameStatesManager), "initialSimulationTick").SetValue(manager, 100L);
        manager.InstallWorldClock();
        TestAssert.IsTrue(Observe(1000, 980, 100, 60, 100));
        TestAssert.AreEqual(60d, requestedUps);
    }

    [TestMethod]
    public void ProductionReceiveQueuePassesItsLocalAgeToTheClockHandler()
    {
        harmony.Patch(AccessTools.PropertyGetter(typeof(global::NebulaeModel.Logger.Diagnostics), "Now"),
            prefix: new HarmonyMethod(typeof(ClockDelayDiagnosisTest), nameof(CaptureElapsed)));
        var processor = new global::NebulaeModel.Networking.Serialization.NebulaeNetPacketProcessor();
        new global::NebulaeNetwork.PacketProcessors.GameStates.GameStateUpdateProcessor().Register(processor, isHost: false);
        var packet = new global::NebulaeModel.Packets.GameStates.GameStateUpdate(OriginMs, OriginTick, 60, 1);
        processor.EnqueuePacketForProcessing(processor.Write(packet), null!);
        nowMs = OriginMs + 2000;
        GameMain.gameTick = OriginTick + 60;
        processor.ProcessPacketQueue();
        TestAssert.AreEqual(119d, requestedUps);
        TestAssert.AreEqual(0d, global::NebulaeModel.Networking.Serialization.NebulaeNetPacketProcessor.CurrentPacketQueueSeconds);
    }

    [TestMethod]
    public void QueueAgeIsInheritedByInlinePacketsAndClearedAfterAHandlerThrows()
    {
        harmony.Patch(AccessTools.PropertyGetter(typeof(global::NebulaeModel.Logger.Diagnostics), "Now"),
            prefix: new HarmonyMethod(typeof(ClockDelayDiagnosisTest), nameof(CaptureElapsed)));
        var processor = new global::NebulaeModel.Networking.Serialization.NebulaeNetPacketProcessor();
        var seen = new List<double>();
        var child = processor.Write(new global::NebulaeModel.Packets.GameStates.GameStateRequest());
        processor.SubscribeReusable<global::NebulaeModel.Packets.GameStates.GameStateRequest>(_ =>
            seen.Add(global::NebulaeModel.Networking.Serialization.NebulaeNetPacketProcessor.CurrentPacketQueueSeconds));
        processor.SubscribeReusable<global::NebulaeModel.Packets.GameStates.GameStateUpdate>(_ =>
        {
            seen.Add(global::NebulaeModel.Networking.Serialization.NebulaeNetPacketProcessor.CurrentPacketQueueSeconds);
            processor.DispatchInline(child, null!);
            throw new InvalidOperationException("Injected dispatch failure");
        });
        processor.EnqueuePacketForProcessing(processor.Write(new global::NebulaeModel.Packets.GameStates.GameStateUpdate()), null!);
        nowMs = OriginMs + 2000;
        processor.ProcessPacketQueue();
        CollectionAssert.AreEqual(new[] { 2d, 2d }, seen);
        TestAssert.AreEqual(0d, global::NebulaeModel.Networking.Serialization.NebulaeNetPacketProcessor.CurrentPacketQueueSeconds);
    }

    private static bool CaptureElapsed(ref double __result)
    {
        __result = (nowMs - OriginMs) / 1000d;
        return false;
    }

    private sealed class Reply
    {
        public long SentMs, HostSampleTick, DueMs;
        public float HostUps;
    }

    private sealed class Result
    {
        public double MinCommand = 60, MaxCommand = 60, LowSeconds, LongestLowSeconds;
        public double FinalCommand, FinalLag, TailMin = double.MaxValue, TailMax, TailMean;
        public int Accepted, Rejected;
    }

    // Only the execution environment is simulated: host ticks, FIFO delivery times, and a
    // client capacity cap. Every feedback decision runs ProcessGameStateUpdatePacket itself.
    private Result Run(string name, Func<long, double> hostTicks, Func<long, float> hostRate,
        Func<long, long> upstreamMs, Func<long, long> replyMs, long duration = 120000,
        double capacity = 240, long? stopRepliesAfter = null, long minDeliverySpacing = 0)
    {
        var replies = new Queue<Reply>();
        long lastDue = 0;
        for (long sent = 0; sent < duration; sent += 1000)
        {
            if (stopRepliesAfter.HasValue && sent > stopRepliesAfter.Value) continue;
            var sampled = sent + upstreamMs(sent);
            var due = Math.Max(sampled + replyMs(sent), lastDue + minDeliverySpacing);
            lastDue = due;
            replies.Enqueue(new Reply
            {
                SentMs = sent,
                HostSampleTick = OriginTick + (long)Math.Floor(hostTicks(sampled)),
                HostUps = hostRate(sampled),
                DueMs = due
            });
        }
        var result = new Result();
        var trace = new StringBuilder("time_s\thost_tick\tclient_tick\ttrue_lag_ticks\trtt_ms\treported_host_ups\taccepted\trequested_ups\tevent\n");
        double local = OriginTick, lowRun = 0, tailRateSum = 0;
        var tailSteps = 0;
        const int step = 10;
        for (long time = 0; time <= duration; time += step)
        {
            if (time > 0)
            {
                local += Math.Min(capacity, requestedUps) * step / 1000;
                if (requestedUps <= 2) { result.LowSeconds += step / 1000d; lowRun += step / 1000d; }
                else lowRun = 0;
                result.LongestLowSeconds = Math.Max(result.LongestLowSeconds, lowRun);
            }
            while (replies.Count > 0 && replies.Peek().DueMs <= time)
            {
                var reply = replies.Dequeue();
                var localTick = (long)Math.Floor(local + 1e-7);
                var accepted = Observe(time, reply.SentMs, reply.HostSampleTick, reply.HostUps, localTick);
                if (accepted) result.Accepted++; else result.Rejected++;
                result.MinCommand = Math.Min(result.MinCommand, requestedUps);
                result.MaxCommand = Math.Max(result.MaxCommand, requestedUps);
                trace.AppendLine(FormattableString.Invariant($"{time / 1000d:F2}\t{OriginTick + hostTicks(time):F2}\t{localTick}\t{OriginTick + hostTicks(time) - localTick:F2}\t{time - reply.SentMs}\t{reply.HostUps}\t{accepted}\t{requestedUps:F3}\tsample"));
            }
            nowMs = OriginMs + time;
            GameMain.gameTick = (long)Math.Floor(local + 1e-7);
            var beforeService = commands;
            manager.ServiceClock();
            if (commands != beforeService)
                trace.AppendLine(FormattableString.Invariant($"{time / 1000d:F2}\t{OriginTick + hostTicks(time):F2}\t{GameMain.gameTick}\t{OriginTick + hostTicks(time) - GameMain.gameTick:F2}\t\t{manager.GetServerUPS()}\tFalse\t{requestedUps:F3}\tlease-expired"));
            if (time >= duration - 30000)
            {
                result.TailMin = Math.Min(result.TailMin, requestedUps);
                result.TailMax = Math.Max(result.TailMax, requestedUps);
                tailRateSum += requestedUps; tailSteps++;
            }
        }
        result.FinalCommand = requestedUps;
        result.FinalLag = OriginTick + hostTicks(duration) - local;
        result.TailMean = tailRateSum / tailSteps;
        var summary = FormattableString.Invariant($"{name}: min={result.MinCommand:F3}, max={result.MaxCommand:F3}, seconds_at_2ups_or_less={result.LowSeconds:F2}, longest_low_seconds={result.LongestLowSeconds:F2}, final={result.FinalCommand:F3}, final_lag_ticks={result.FinalLag:F2}, last30s_min={result.TailMin:F3}, last30s_max={result.TailMax:F3}, last30s_mean={result.TailMean:F3}, accepted={result.Accepted}, rejected={result.Rejected}");
        TestContext.WriteLine(summary);
        var directory = Environment.GetEnvironmentVariable("NEBULAE_CLOCK_DIAG_DIR");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name + ".tsv"), trace.ToString());
            File.WriteAllText(Path.Combine(directory, name + ".txt"), summary);
        }
        return result;
    }

    [TestMethod]
    public void HealthySixtyUpsRemainsStable()
    {
        var result = Run("healthy_60", ms => ms * 0.06, _ => 60, _ => 10, _ => 10);
        TestAssert.AreEqual(0d, result.LowSeconds);
        TestAssert.AreEqual(60d, result.TailMean, 0.1);
    }

    [TestMethod]
    public void ConstantTwoSecondReplyDelayKeepsAnAlignedClientAtSixty()
    {
        var result = Run("constant_reply_queue_2s", ms => ms * 0.06, _ => 60, _ => 10, _ => 2010);
        TestAssert.AreEqual(0d, result.LowSeconds);
        TestAssert.AreEqual(60d, result.TailMean, 0.1);
        TestAssert.AreEqual(0d, result.FinalLag, 1);
    }

    [TestMethod]
    public void LargeSustainedQueueAfterWarmupCannotApplyAnOldPhaseCorrection()
    {
        var result = Run("reply_queue_20s_after_warmup", ms => ms * 0.06, _ => 60, _ => 10,
            sent => sent < 20000 ? 10 : 20010);
        TestAssert.AreEqual(0d, result.LowSeconds);
        TestAssert.AreEqual(60d, result.TailMean, 0.1);
    }

    [TestMethod]
    public void SilenceAfterADelayedFirstReplyDoesNotStrandTheClientAtOneUps()
    {
        var result = Run("delayed_first_reply_then_silence", ms => ms * 0.06, _ => 60,
            _ => 10, _ => 2010, duration: 60000, stopRepliesAfter: 0);
        TestAssert.AreEqual(60d, result.FinalCommand);
        TestAssert.AreEqual(0d, result.LowSeconds);
    }

    [TestMethod]
    public void SustainedHostSlowdownConvergesWithoutFirstOverrunningAndBraking()
    {
        var result = Run("host_60_to_5", ms => ms <= 20000 ? ms * 0.06 : 1200 + (ms - 20000) * 0.005,
            ms => ms < 20000 ? 60 : 5, _ => 10, _ => 10, duration: 180000);
        TestAssert.AreEqual(0d, result.LowSeconds);
        TestAssert.AreEqual(5d, result.TailMean, 0.2);
    }

    [TestMethod]
    public void TemporaryHostSlowdownCanRecoverAfterTheHostSpeedsUp()
    {
        var result = Run("host_60_to_5_to_60",
            ms => ms <= 20000 ? ms * 0.06 : ms <= 50000 ? 1200 + (ms - 20000) * 0.005 : 1350 + (ms - 50000) * 0.06,
            ms => ms >= 20000 && ms < 50000 ? 5 : 60, _ => 10, _ => 10);
        TestAssert.AreEqual(0d, result.LowSeconds);
        TestAssert.AreEqual(60d, result.TailMean, 0.2);
    }

    [TestMethod]
    public void RepeatedQueueBurstsDoNotCauseFalseRateCorrections()
    {
        var result = Run("periodic_reply_queue_bursts", ms => ms * 0.06, _ => 60, _ => 10,
            sent => sent >= 20000 && sent % 20000 < 6000 ? 5010 : 10,
            minDeliverySpacing: 200);
        TestAssert.AreEqual(60d, result.MaxCommand);
        TestAssert.AreEqual(60d, result.MinCommand);
    }

    [TestMethod]
    public void AClientThatCannotKeepUpRequestsAccelerationInsteadOfOneUps()
    {
        var result = Run("client_capacity_30_host_60", ms => ms * 0.06, _ => 60, _ => 10, _ => 10, capacity: 30);
        TestAssert.AreEqual(0d, result.LowSeconds);
        TestAssert.AreEqual(240d, result.FinalCommand);
        TestAssert.IsGreaterThan(3000d, result.FinalLag);
    }
}

