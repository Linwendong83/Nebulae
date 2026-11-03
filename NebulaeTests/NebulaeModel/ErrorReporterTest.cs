using System.Diagnostics;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using global::NebulaeModel;
using global::NebulaeModel.Logger;
using WebSocketSharp;
using WebSocketSharp.Server;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class ErrorReporterTest
{
    private sealed class CallerFrame(MethodBase method) : StackFrame
    {
        public override MethodBase GetMethod() => method;
    }

    private sealed class ReportingLogger(ErrorReporter reporter) : ILogger
    {
        public readonly List<string> Warnings = new(), Errors = new();
        public void LogDebug(object data) { }
        public void LogInfo(object data) => reporter.Capture("Nebulae", data.ToString(), "", false);
        public void LogWarning(object data)
        {
            Warnings.Add(data.ToString()!);
            reporter.Capture("Nebulae", data.ToString(), "", false);
        }
        public void LogError(object data)
        {
            Errors.Add(data.ToString()!);
            reporter.Capture("Nebulae", data.ToString(), "", true);
        }
    }

    private sealed class Transport : IReportTransport
    {
        public readonly List<byte[]> Requests = new();
        public readonly Queue<ReportSendResult> Results = new();
        public Func<CancellationToken, Task>? Wait;
        public async Task<ReportSendResult> SendAsync(byte[] json, string reportId, CancellationToken cancellation)
        {
            Requests.Add(json);
            if (Wait != null) await Wait(cancellation);
            return Results.Count == 0 ? new ReportSendResult(201) : Results.Dequeue();
        }
        public void Dispose() { }
    }

    private string root = null!;
    private DateTime now;
    private Transport transport = null!;
    private ErrorReporter reporter = null!;
    private readonly List<string> warnings = new();

    [TestInitialize]
    public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "NebulaeReportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        now = DateTime.UtcNow;
        transport = new Transport();
        reporter = NewReporter();
        warnings.Clear();
    }

    private ErrorReporter NewReporter()
    {
        var result = new ErrorReporter(root, transport, warnings.Add, () => now, startWorker: false);
        result.SetEnvironment(new ReportEnvironment { Role = "host", NebulaeVersion = "1.0.0", GameVersion = "0.10.35.29151", Secrets = new[] { "known-secret" } });
        result.SetEnabled(true);
        return result;
    }

    [TestCleanup]
    public void Cleanup()
    {
        reporter.Dispose();
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NebulaeReportTests")) + Path.DirectorySeparatorChar;
        Check.IsTrue(Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase));
        Directory.Delete(root, true);
    }

    [TestMethod]
    public void DefaultIsOnAndStableConfigKeyCanPersistOff()
    {
        Check.IsTrue(new MultiplayerOptions().AutoReportErrors);
        var path = Path.Combine(root, "options.cfg");
        var config = new ConfigFile(path, true);
        config.Bind("Nebulae - Settings", "AutoReportErrors", true).Value = false;
        config.Save();
        Check.IsFalse(new ConfigFile(path, false).Bind("Nebulae - Settings", "AutoReportErrors", true).Value);
    }

    [TestMethod]
    public async Task UnityOutsideMultiplayerIsIgnoredButNebulaeStartupErrorsAreCaptured()
    {
        reporter.SetEnvironment(new ReportEnvironment { Role = "menu" });
        reporter.Capture("Unity", "unrelated", "stack", true, unity: true);
        await reporter.PumpAsync();
        Check.HasCount(0, transport.Requests);
        reporter.Capture("Nebulae", "startup failure", "stack", true);
        await reporter.PumpAsync();
        Check.HasCount(1, transport.Requests);
        Check.AreEqual("startup failure", ReportCodec.Decode(transport.Requests[0]).Errors.Single().Message);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" \r\n")]
    public async Task KnownNativeAnimationNoiseIsIgnoredWithoutSuppressingOtherUnityErrors(string whitespace)
    {
        const string animation = "Non-Legacy animations cannot be sampled outside the Editor without an Animator. If you want to sample this clip outside the Editor, add an Animator Component to this GameObject";
        reporter.Capture("Unity", whitespace + animation + whitespace, "", true, unity: true);
        await reporter.PumpAsync();
        Check.IsEmpty(transport.Requests);
        Check.IsEmpty(Directory.GetFiles(root, "*.json"));

        reporter.Capture("Unity", animation + " Additional failure", "", true, unity: true);
        reporter.Capture("Unity", "NullReferenceException in UIMarkerTipIcons", "game stack", true, unity: true);
        reporter.Capture("Nebulae", animation, "", true);
        await reporter.PumpAsync();
        Check.HasCount(3, ReportCodec.Decode(transport.Requests.Single()).Errors);
    }

    [TestMethod]
    [DataRow("connect", LogLevel.Fatal, "Requires the authentication.", true)]
    [DataRow("connect", LogLevel.Fatal, "Connection refused", true)]
    [DataRow("connect", LogLevel.Fatal, "An error has occurred during a TLS handshake.", true)]
    [DataRow("connect", LogLevel.Fatal, "Not a WebSocket handshake response.", true)]
    [DataRow("http-request", LogLevel.Error, "A timeout has occurred while reading an HTTP request or response.", true)]
    [DataRow("sendBytes", LogLevel.Error, "Connection reset by peer.", true)]
    [DataRow("receive", LogLevel.Fatal, "System.IO.IOException: Connection reset by peer.", true)]
    [DataRow("send-frame", LogLevel.Error, "The connection is closing.", true)]
    [DataRow("send-cache", LogLevel.Error, "The connection is closing.", true)]
    [DataRow("messagec", LogLevel.Error, "OnMessage callback failed", false)]
    [DataRow("messages", LogLevel.Error, "OnMessage callback failed", false)]
    [DataRow("open", LogLevel.Error, "OnOpen callback failed", false)]
    [DataRow("close", LogLevel.Error, "OnClose callback failed", false)]
    [DataRow("refuseHandshake", LogLevel.Error, "OnClose callback failed", false)]
    [DataRow("error", LogLevel.Error, "OnError callback failed", false)]
    [DataRow("async-send", LogLevel.Error, "Send callback failed", false)]
    [DataRow("server-loop", LogLevel.Fatal, "Listener failed", false)]
    [DataRow("send-stream", LogLevel.Error, "Compression failed", false)]
    [DataRow("unknown", LogLevel.Error, "Unknown library failure", false)]
    public async Task SocketLogOriginControlsSeverityAndWhetherItCreatesAReport(string site, LogLevel level, string message, bool warning)
    {
        var previous = (ILogger)global::HarmonyLib.AccessTools.Field(typeof(Log), "logger").GetValue(null);
        var logger = new ReportingLogger(reporter);
        var data = (LogData)Activator.CreateInstance(typeof(LogData), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { level, new CallerFrame(SocketCaller(site)), message + " 192.0.2.5 " + Guid.NewGuid() }, null)!;
        var lastError = Log.LastErrorMsg;
        try
        {
            Log.Init(logger);
            Log.SocketOutput(data, "");
            Log.SocketOutput(data, ""); // The same diagnostic is still throttled.
            Check.AreEqual(level, data.Level, "Authentication detection must see the original Fatal level.");
            var lines = warning ? logger.Warnings : logger.Errors;
            Check.HasCount(1, lines);
            Check.IsEmpty(warning ? logger.Errors : logger.Warnings);
            Check.DoesNotContain("192.0.2.5", lines[0]);
            if (warning) Check.AreEqual(lastError, Log.LastErrorMsg);
            await reporter.PumpAsync();
            Check.HasCount(warning ? 0 : 1, transport.Requests);
            if (warning)
            {
                reporter.Capture("Nebulae", "real mod failure", "", true);
                await reporter.PumpAsync();
                var report = ReportCodec.Decode(transport.Requests.Single());
                Check.AreEqual("real mod failure", report.Errors.Single().Message);
                StringAssert.Contains(report.Context, "(IPv4 Address)");
            }
        }
        finally { Log.Init(previous); Log.LastErrorMsg = lastError; }
    }

    [TestMethod]
    public async Task TransportWarningCannotThrottleACallbackErrorWithTheSameText()
    {
        var previous = (ILogger)global::HarmonyLib.AccessTools.Field(typeof(Log), "logger").GetValue(null);
        var lastError = Log.LastErrorMsg;
        var logger = new ReportingLogger(reporter);
        var message = "same failure " + Guid.NewGuid();
        LogData Record(string site) => (LogData)Activator.CreateInstance(typeof(LogData), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { LogLevel.Error, new CallerFrame(SocketCaller(site)), message }, null)!;
        try
        {
            Log.Init(logger);
            Log.SocketOutput(Record("sendBytes"), "");
            Log.SocketOutput(Record("messages"), "");
            Check.HasCount(1, logger.Warnings);
            Check.HasCount(1, logger.Errors);
            await reporter.PumpAsync();
            Check.HasCount(1, ReportCodec.Decode(transport.Requests.Single()).Errors);
        }
        finally { Log.Init(previous); Log.LastErrorMsg = lastError; }
    }

    private static MethodBase SocketCaller(string site)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
        if (site == "unknown") return typeof(ErrorReporterTest).GetMethod(nameof(Setup))!;
        if (site == "server-loop") return typeof(WebSocketServer).GetMethod("receiveRequest", flags)!;
        if (site is "http-request" or "receive" or "async-send")
        {
            var owner = site == "http-request" ? typeof(WebSocketServer) : typeof(WebSocket);
            var method = site == "http-request" ? "receiveRequest" : site == "receive" ? "startReceiving" : "sendAsync";
            var argument = site == "receive" ? typeof(Exception) : site == "async-send" ? typeof(IAsyncResult) : typeof(object);
            return new[] { owner }.Concat(owner.GetNestedTypes(flags)).SelectMany(type => type.GetMethods(flags))
                .Single(candidate => candidate.Name.StartsWith("<" + method + ">", StringComparison.Ordinal) &&
                    candidate.GetParameters().Length == 1 && candidate.GetParameters()[0].ParameterType == argument);
        }
        if (site.StartsWith("send-", StringComparison.Ordinal))
        {
            return typeof(WebSocket).GetMethods(flags).Single(method => site switch
            {
                "send-frame" => method.Name == "send" && method.GetParameters().Length == 4,
                "send-stream" => method.Name == "send" && method.GetParameters().Length == 2,
                _ => method.Name == "Send" && method.GetParameters().Length == 3 && method.GetParameters()[1].ParameterType == typeof(byte[])
            });
        }
        return typeof(WebSocket).GetMethods(flags).First(method => method.Name == site);
    }

    [TestMethod]
    public async Task ConcurrentDuplicatesKeepCountsAndResumeAfterTenMinutes()
    {
        Parallel.For(0, 100, _ => reporter.Capture("Nebulae", "same fault", "stack", true));
        await reporter.PumpAsync();
        Check.AreEqual(100L, ReportCodec.Decode(transport.Requests[0]).Errors.Single().Count);
        now = now.AddSeconds(1);
        Parallel.For(0, 50, _ => reporter.Capture("Nebulae", "same fault", "stack", true));
        await reporter.PumpAsync();
        Check.HasCount(1, transport.Requests);
        now = now.AddMinutes(10);
        await reporter.PumpAsync();
        Check.HasCount(2, transport.Requests);
        Check.AreEqual(50L, ReportCodec.Decode(transport.Requests[1]).Errors.Single().Count);
    }

    [TestMethod]
    public async Task FrameNumbersAndInstructionAddressesDoNotDefeatDeduplication()
    {
        for (var i = 0; i < 100; i++) reporter.Capture("Unity", $"The game errored in frame [{i}]", "at game [0x" + i.ToString("x") + "]", true, unity: true);
        await reporter.PumpAsync();
        var report = ReportCodec.Decode(transport.Requests.Single());
        Check.HasCount(1, report.Errors);
        Check.AreEqual(100L, report.Errors[0].Count);
    }

    [TestMethod]
    public void RedactorHandlesQuotedCredentialsEndpointsAndLoopbackAddresses()
    {
        var redactor = new ReportRedactor(Array.Empty<string>());
        var clean = redactor.Clean("{\"password\":\"two word password\",\"token\":\"two word token\"}\nCookie: sid=secret; other=hidden\nhttp://user:pass@example.org/ 192.168.1.2:8469 ::1 [2001:db8::1]:8443 C:\\Users\\private\\game /home/alice/game", 32768);
        foreach (var secret in new[] { "two word", "sid=secret", "other=hidden", "user:pass", "192.168.1.2", "::1", "private", "alice" }) Check.DoesNotContain(secret, clean);
        StringAssert.Contains(clean, "example.org");
        StringAssert.Contains(clean, ":8469");
    }

    [TestMethod]
    public async Task BatchesAreAtMostSixteenAndRequestsAreAtLeastAMinuteApart()
    {
        for (var i = 0; i < 33; i++) reporter.Capture("Nebulae", "fault-" + i, "stack", true);
        await reporter.PumpAsync();
        Check.HasCount(1, transport.Requests);
        Check.HasCount(2, Directory.GetFiles(root, "*.json"));
        now = now.AddSeconds(59);
        await reporter.PumpAsync();
        Check.HasCount(1, transport.Requests);
        now = now.AddSeconds(1);
        await reporter.PumpAsync();
        now = now.AddSeconds(60);
        await reporter.PumpAsync();
        Check.HasCount(3, transport.Requests);
        Check.AreEqual(33, transport.Requests.Sum(bytes => ReportCodec.Decode(bytes).Errors.Length));
        Check.IsTrue(transport.Requests.All(bytes => ReportCodec.Decode(bytes).Errors.Length <= 16));
    }

    [TestMethod]
    public async Task RetryAfterBackoffAndRestartReuseTheSameReport()
    {
        transport.Results.Enqueue(new ReportSendResult(429, TimeSpan.FromSeconds(120)));
        transport.Results.Enqueue(new ReportSendResult(503));
        reporter.Capture("Nebulae", "retry", "stack", true);
        await reporter.PumpAsync();
        now = now.AddSeconds(119);
        await reporter.PumpAsync();
        Check.HasCount(1, transport.Requests);
        now = now.AddSeconds(2);
        await reporter.PumpAsync();
        now = now.AddSeconds(299);
        await reporter.PumpAsync();
        Check.HasCount(2, transport.Requests);
        reporter.Dispose();
        reporter = NewReporter();
        await reporter.PumpAsync();
        Check.HasCount(3, transport.Requests);
        Check.AreEqual(1, transport.Requests.Select(bytes => ReportCodec.Decode(bytes).ReportId).Distinct().Count());
        CollectionAssert.AreEqual(transport.Requests[0], transport.Requests[2]);
        Check.HasCount(0, Directory.GetFiles(root, "*.json"));
    }

    [TestMethod]
    public async Task DisablingCancelsInFlightWorkAndPurgesUnsentReports()
    {
        var entered = new TaskCompletionSource<bool>();
        transport.Wait = async cancellation => { entered.SetResult(true); await Task.Delay(Timeout.Infinite, cancellation); };
        reporter.Capture("Nebulae", "blocked", "stack", true);
        var pump = reporter.PumpAsync();
        await entered.Task;
        reporter.SetEnabled(false);
        await pump;
        await reporter.PumpAsync();
        Check.HasCount(0, Directory.GetFiles(root, "*.json*"));
        reporter.Capture("Nebulae", "disabled", "stack", true);
        await reporter.PumpAsync();
        Check.HasCount(1, transport.Requests);
    }

    [TestMethod]
    public async Task NonRetryableErrorsAreDroppedAndWarningsDoNotRecurse()
    {
        transport.Results.Enqueue(new ReportSendResult(400));
        reporter.Capture("Nebulae", "bad", "", true);
        await reporter.PumpAsync();
        Check.HasCount(0, Directory.GetFiles(root, "*.json"));
        Check.HasCount(1, warnings);
        await reporter.PumpAsync();
        Check.HasCount(1, transport.Requests);
    }

    [TestMethod]
    public async Task UnexpectedRedirectDoesNotLoseAReport()
    {
        transport.Results.Enqueue(new ReportSendResult(302));
        reporter.Capture("Nebulae", "redirect", "", true);
        await reporter.PumpAsync();
        Check.HasCount(1, Directory.GetFiles(root, "*.json"));
        now = now.AddSeconds(76);
        await reporter.PumpAsync();
        Check.HasCount(2, transport.Requests);
        Check.HasCount(0, Directory.GetFiles(root, "*.json"));
    }

    [TestMethod]
    public async Task InvalidQueuedJsonCannotBlockLaterReports()
    {
        transport.Results.Enqueue(new ReportSendResult(503));
        reporter.Capture("Nebulae", "queued", "", true);
        await reporter.PumpAsync();
        var path = Directory.GetFiles(root, "*.json").Single();
        File.WriteAllText(path, "null");
        now = now.AddSeconds(76);
        await reporter.PumpAsync();
        Check.IsFalse(File.Exists(path));
        reporter.Capture("Nebulae", "later", "", true);
        await reporter.PumpAsync();
        Check.HasCount(2, transport.Requests);
    }

    [TestMethod]
    public async Task SecretsAddressesAndUserPathsAreSanitizedBeforeSpooling()
    {
        transport.Results.Enqueue(new ReportSendResult(503));
        reporter.Capture("Nebulae", @"known-secret password=abc token=def Authorization: Bearer abcXYZ C:\Users\private\game /home/alice/game 192.168.1.2 2001:db8::1", "", true);
        await reporter.PumpAsync();
        var json = File.ReadAllText(Directory.GetFiles(root, "*.json").Single());
        foreach (var secret in new[] { "known-secret", "abcXYZ", "private", "alice", "192.168.1.2", "2001:db8::1", "=abc", "=def" })
            Check.DoesNotContain(secret, json);
        StringAssert.Contains(json, "redacted");
    }

    [TestMethod]
    public async Task ChineseAndEscapedLongStacksStayWithinTheActualByteBudget()
    {
        for (var i = 0; i < 16; i++) reporter.Capture("Nebulae", i + new string('\u0002', 20000), new string('错', 60000), true);
        await reporter.PumpAsync();
        foreach (var json in transport.Requests.Concat(Directory.GetFiles(root, "*.json").Select(File.ReadAllBytes)))
        {
            Check.IsLessThanOrEqualTo(ReportCodec.MaximumBytes, json.Length);
            var report = ReportCodec.Decode(json);
            Check.IsTrue(report.Truncated);
            Check.IsLessThanOrEqualTo(32768, Encoding.UTF8.GetByteCount(report.Context));
            Check.IsTrue(report.Errors.All(error => Encoding.UTF8.GetByteCount(error.Stack) <= 49152));
        }
    }

    [TestMethod]
    public async Task PendingStorageIsBoundedAndExpiredReportsAreNotSent()
    {
        reporter.Capture("Nebulae", "sample", "", true);
        transport.Results.Enqueue(new ReportSendResult(503));
        await reporter.PumpAsync();
        var original = Directory.GetFiles(root, "*.json").Single();
        var report = ReportCodec.Decode(File.ReadAllBytes(original));
        for (var i = 0; i < 140; i++)
        {
            report.ReportId = Guid.NewGuid().ToString("N");
            report.Context = new string('x', 64000);
            File.WriteAllBytes(Path.Combine(root, report.ReportId + ".json"), ReportCodec.Encode(report));
        }
        File.SetLastWriteTimeUtc(original, now.AddDays(-8));
        await reporter.PumpAsync();
        Check.IsFalse(File.Exists(original));
        var files = new DirectoryInfo(root).GetFiles("*.json");
        Check.IsLessThanOrEqualTo(128, files.Length);
        Check.IsLessThanOrEqualTo(ErrorReporter.PendingBytes, files.Sum(file => file.Length));
    }

}
