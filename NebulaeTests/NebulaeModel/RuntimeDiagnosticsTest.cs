using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using global::NebulaeModel.Authority;
using global::NebulaeModel.Logger;
using global::NebulaeModel.Networking;
using global::NebulaeModel.Networking.Serialization;
using global::NebulaeModel.Packets.Authority;
using global::NebulaeModel.Utils;
using HarmonyLib;
using NebulaeWorld.Authority;
using WebSocketSharp;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class RuntimeDiagnosticsTest
{
    private sealed class RecordingLogger : ILogger
    {
        public readonly List<string> Debug = [], Warnings = [], Errors = [], Info = [];
        public void LogDebug(object data) => Debug.Add(data.ToString()!);
        public void LogInfo(object data) => Info.Add(data.ToString()!);
        public void LogWarning(object data) => Warnings.Add(data.ToString()!);
        public void LogError(object data) => Errors.Add(data.ToString()!);
    }

    private ILogger previous = null!;
    private RecordingLogger logger = null!;
    [TestInitialize]
    public void Setup()
    {
        previous = (ILogger)AccessTools.Field(typeof(Log), "logger").GetValue(null);
        logger = new RecordingLogger();
        Log.Init(logger);
    }
    [TestCleanup]
    public void Cleanup() => Log.Init(previous);

    [TestMethod]
    public void ConcurrentFaultsEmitOncePerWindowAndReportSuppressedCount()
    {
        double time = 0;
        var throttle = new DiagnosticThrottle(() => time);
        var admitted = new ConcurrentBag<bool>();
        Parallel.For(0, 100, worker => admitted.Add(throttle.TryAcquire("fault", out _)));
        Check.AreEqual(1, admitted.Count(value => value));
        time = 30;
        Check.IsTrue(throttle.TryAcquire("fault", out var suppressed));
        Check.AreEqual(99L, suppressed);
        time = 60;
        Check.IsTrue(throttle.TryAcquire("fault", out suppressed));
        Check.AreEqual(0L, suppressed, "Later windows must not become permanently silent.");
    }

    [TestMethod]
    public void ThrottleEvictsOldKeysWithoutHidingANewFault()
    {
        var throttle = new DiagnosticThrottle(() => 0, capacity: 2);
        Check.IsTrue(throttle.TryAcquire("old", out _));
        Check.IsTrue(throttle.TryAcquire("held", out _));
        Check.IsTrue(throttle.TryAcquire("new", out _));
        Check.IsFalse(throttle.TryAcquire("held", out _));
        Check.IsTrue(throttle.TryAcquire("old", out _));
    }

    [TestMethod]
    public void WatchdogUsesElapsedTimeAndProgressAndFormatsOnlyWhenReporting()
    {
        double time = 0;
        var watchdog = new DiagnosticWatchdog(() => time);
        var formatted = 0;
        string Context() { formatted++; return "command=17 checkpoint=9"; }
        watchdog.Observe("wait", true, Context, progress: 1);
        time = 20;
        watchdog.Observe("wait", true, Context, progress: 1);
        time = 25;
        watchdog.Observe("wait", true, Context, progress: 2);
        time = 54;
        watchdog.Observe("wait", true, Context, progress: 2);
        Check.AreEqual(0, formatted);
        time = 55;
        watchdog.Observe("wait", true, Context, progress: 2);
        time = 56;
        watchdog.Observe("wait", true, Context, progress: 2);
        Check.HasCount(1, logger.Warnings);
        StringAssert.Contains(logger.Warnings[0], "command=17");
        time = 60;
        watchdog.Observe("wait", false, Context);
        watchdog.Observe("wait", false, Context);
        Check.HasCount(1, logger.Debug);
        Check.AreEqual(2, formatted);
    }

    [TestMethod]
    public void ErrorContextAndInnerExceptionAreOneRecord()
    {
        var error = new InvalidOperationException("outer-cause", new IOException("inner-cause"));
        Log.Error("operation=save world=unit-test", error);
        Check.HasCount(1, logger.Errors);
        StringAssert.Contains(logger.Errors[0], "operation=save");
        StringAssert.Contains(logger.Errors[0], "inner-cause");
        Check.AreEqual(logger.Errors[0], Log.LastErrorMsg);
    }

    [TestMethod]
    public void UnknownSocketErrorKeepsItsLevelWithoutUsingPopupErrorPath()
    {
        var data = (LogData)Activator.CreateInstance(typeof(LogData), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { LogLevel.Error, new StackFrame(), "runtime-diagnostics-test 192.0.2.5" }, null)!;
        Log.SocketOutput(data, "");
        Check.HasCount(1, logger.Errors);
        Check.IsEmpty(logger.Warnings);
        Check.IsEmpty(logger.Info);
        Check.DoesNotContain("192.0.2.5", logger.Errors[0]);
    }

    [PacketId(0x7FFF00000000D101)]
    public class OuterDiagnosticPacket { [WireOrder(0)] public byte[] Data { get; set; } = []; }
    [PacketId(0x7FFF00000000D102)]
    public class InnerDiagnosticPacket { [WireOrder(0)] public int Value { get; set; } }

    [TestMethod]
    public void NestedPacketFailurePreservesBothEnvelopeAndFailingChild()
    {
        var processor = new NebulaeNetPacketProcessor();
        var cause = new InvalidOperationException("nested-root-cause");
        processor.SubscribeReusable<InnerDiagnosticPacket>(_ => throw cause);
        processor.SubscribeReusable<OuterDiagnosticPacket>(packet => processor.DispatchInline(packet.Data, null));
        var inner = processor.Write(new InnerDiagnosticPacket { Value = 7 });
        var outer = processor.Write(new OuterDiagnosticPacket { Data = inner });
        try { processor.DispatchInline(outer, null); Check.Fail("The handler must still throw."); }
        catch (InvalidOperationException error)
        {
            Check.AreSame(cause, error);
            var path = error.Data["Nebulae.PacketPath"]!.ToString()!;
            StringAssert.Contains(path, nameof(OuterDiagnosticPacket));
            StringAssert.Contains(path, nameof(InnerDiagnosticPacket));
            StringAssert.Contains(path, "depth=2");
        }
    }

    private sealed class FailingExecutor : IHostCommandExecutor
    {
        public int Calls;
        public CommandOutcome Execute(in QueuedHostCommand command) { Calls++; throw new IOException("command-root-cause"); }
    }

    [TestMethod]
    public void CorruptSnapshotReportsChecksumCauseAndKeepsTheOriginalRejection()
    {
        var staging = new ClientSnapshotStaging();
        var scope = new ScopeKey(PoolKind.GroundEnemy, 901);
        var bytes = new byte[100];
        var hash = AuthoritySnapshotCodec.Hash(bytes);
        Check.IsTrue(staging.Begin(scope, 71, 72, 1, bytes.Length, hash));
        bytes[50] = 1;
        Check.IsTrue(staging.Chunk(scope, 71, 72, 0, bytes));
        Check.IsFalse(staging.Commit(scope, 71, 72, 1, hash, out _));
        Check.HasCount(1, logger.Warnings);
        StringAssert.Contains(logger.Warnings[0], "commit:checksum");
        StringAssert.Contains(logger.Warnings[0], "expectedHash=");
        Check.AreEqual(1L, staging.RefusedTotal);
        Check.AreEqual(0, staging.Count);
    }

    [TestMethod]
    public void FailedCommandLogsItsIdentityAndIsStillNeverRetried()
    {
        var epoch = new AuthorityEpoch(71, 72);
        var connection = new ConnectionEpoch(73);
        var key = new CommandKey(epoch, connection, 74);
        var packet = AuthorityCommandPacket.Create(new AuthorityEnvelopeHeader(AuthorityFamily.Command, epoch, connection,
            74, 75, 76, 0), default, 77, []);
        var queue = new HostCommandQueue(epoch);
        var executor = new FailingExecutor();
        queue.TryEnqueue(key, packet, 76, 78, 75);
        queue.Drain(executor, null);
        queue.TryEnqueue(key, packet, 76, 78, 76);
        queue.Drain(executor, null);
        Check.AreEqual(1, executor.Calls);
        Check.HasCount(1, logger.Errors);
        StringAssert.Contains(logger.Errors[0], "connection=78");
        StringAssert.Contains(logger.Errors[0], "category=77");
        StringAssert.Contains(logger.Errors[0], "command-root-cause");
    }

    [TestMethod]
    public void MainThreadFailureRetainsCauseAndDoesNotDiscardRemainingActions()
    {
        var queue = new MainThreadActionQueue();
        var cause = new IOException("main-thread-root-cause");
        var finished = false;
        queue.Enqueue(() => throw cause);
        queue.Enqueue(() => finished = true);
        try { queue.Drain(); Check.Fail("The existing exception behavior must be retained."); }
        catch (IOException error) { Check.AreSame(cause, error); }
        Check.IsFalse(finished);
        queue.Drain();
        Check.IsTrue(finished);
        Check.HasCount(1, logger.Errors);
        StringAssert.Contains(logger.Errors[0], "remaining=1");
    }
}
