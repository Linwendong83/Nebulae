using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NebulaeModel.Logger;

internal readonly struct ReportSendResult
{
    public readonly int Status;
    public readonly TimeSpan RetryAfter;
    public ReportSendResult(int status, TimeSpan retryAfter = default) { Status = status; RetryAfter = retryAfter; }
    public bool Success => Status is 200 or 201;
    public bool Retry => !Success && !(Status >= 400 && Status < 500 && Status != 429);
}

internal interface IReportTransport : IDisposable
{
    Task<ReportSendResult> SendAsync(byte[] json, string reportId, CancellationToken cancellation);
}

internal sealed class HttpReportTransport : IReportTransport
{
    internal const string Endpoint = "https://4q105.top:8443/Nebulae/reports";
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = 4096
    };

    [DataContract]
    private sealed class Receipt { [DataMember(Name = "reportId")] public string ReportId { get; set; } }

    public async Task<ReportSendResult> SendAsync(byte[] json, string reportId, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.UserAgent.ParseAdd("Nebulae-ErrorReporter/1");
        request.Content = new ByteArrayContent(json);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date.HasValue == true ? retry.Date.Value - DateTimeOffset.UtcNow : TimeSpan.Zero);
        if (status is 200 or 201)
        {
            using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var receipt = (Receipt)new DataContractJsonSerializer(typeof(Receipt)).ReadObject(body);
            if (!string.Equals(receipt?.ReportId, reportId, StringComparison.Ordinal)) return new ReportSendResult(503);
        }
        return new ReportSendResult(status, delay);
    }

    public void Dispose() => client.Dispose();
}

/// <summary>Bounded capture on producer threads; all disk and network work runs on one worker.</summary>
internal sealed class ErrorReporter : IDisposable
{
    private sealed class Aggregate
    {
        public string Source, Role, Message, Stack;
        public DateTime First, Last, Reported;
        public long Count;
        public bool Truncated;
    }

    internal const long PendingBytes = 8 * 1024 * 1024;
    private readonly object gate = new();
    private readonly object storageGate = new();
    private static readonly Regex volatileDetail = new(@"(?i)\bframe\s*\[\d+\]|\b0x[0-9a-f]+\b|\bsuppressed=\d+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly Dictionary<string, Aggregate> errors = new();
    private readonly HashSet<string> knownSecrets = new(StringComparer.Ordinal);
    private readonly Queue<string> context = new();
    private readonly string directory, sessionId = Guid.NewGuid().ToString("N");
    private readonly IReportTransport transport;
    private readonly Func<DateTime> utcNow;
    private readonly Action<string> warning;
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private CancellationTokenSource sending;
    private ReportEnvironment environment = new();
    private DateTime nextSend, lastWarning;
    private int contextBytes, retryAttempt, generation;
    private long droppedEvents;
    private bool enabled, purge, disposed;

    public ErrorReporter(string directory, IReportTransport transport, Action<string> warning,
        Func<DateTime> utcNow = null, bool startWorker = true)
    {
        this.directory = directory;
        this.transport = transport;
        this.warning = warning;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        sending = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        if (startWorker) _ = Task.Run(RunAsync);
    }

    public void SetEnvironment(ReportEnvironment value) { lock (gate) environment = value; }

    public void RememberSecret(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (gate) { if (knownSecrets.Count < 256) knownSecrets.Add(value); }
    }

    public void SetEnabled(bool value)
    {
        lock (gate)
        {
            if (disposed) return;
            if (enabled == value && (value || purge)) return;
            enabled = value;
            generation++;
            sending.Cancel();
            sending.Dispose();
            sending = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            if (!value)
            {
                errors.Clear(); context.Clear(); contextBytes = 0; droppedEvents = 0;
                purge = true;
            }
            retryAttempt = 0;
            nextSend = DateTime.MinValue;
        }
        Signal();
    }

    public void Capture(string source, string message, string stack, bool isError, bool unity = false, string role = null)
    {
        lock (gate)
        {
            role ??= environment.Role;
            if (!enabled || disposed || unity && role is not ("host" or "client" or "dedicated")) return;
            message ??= ""; stack ??= "";
            var truncated = message.Length > 16384 || stack.Length > 49152;
            message = ReportCodec.Clip(message, 16384);
            stack = ReportCodec.Clip(stack, 49152);
            var now = utcNow();
            if (!unity)
            {
                var line = ErrorReport.Time(now) + " " + source + " " + ReportCodec.ClipUtf8(message, 2048);
                var size = Encoding.UTF8.GetByteCount(line) + 1;
                context.Enqueue(line); contextBytes += size;
                while (contextBytes > 32768 && context.Count > 0) contextBytes -= Encoding.UTF8.GetByteCount(context.Dequeue()) + 1;
            }
            if (!isError) return;
            // Unity/BepInEx callbacks can race. Keep fingerprinting independent of thread ids and time.
            using var hash = SHA256.Create();
            string fingerprint;
            try { fingerprint = volatileDetail.Replace(message + "\n" + stack, "[volatile]"); }
            catch (RegexMatchTimeoutException) { fingerprint = message + "\n" + stack; }
            var key = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(source + "\n" + fingerprint + "\n" + role)));
            if (!errors.TryGetValue(key, out var entry))
            {
                if (errors.Count >= 128)
                {
                    var oldest = errors.Where(e => e.Value.Count == 0).OrderBy(e => e.Value.Last).FirstOrDefault();
                    if (oldest.Key == null) { droppedEvents++; return; }
                    errors.Remove(oldest.Key);
                }
                errors.Add(key, entry = new Aggregate { Source = source, Role = role, Message = message, Stack = stack });
            }
            if (entry.Count == 0) entry.First = now;
            entry.Last = now;
            if (entry.Count < long.MaxValue) entry.Count++;
            entry.Truncated |= truncated;
        }
        Signal();
    }

    private void Signal() { try { wake.Release(); } catch (SemaphoreFullException) { } }

    private async Task RunAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                try { await PumpAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception error) { Warn("Background reporting failed: " + error.GetType().Name); }
                await wake.WaitAsync(1000, shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try
            {
                // A disable followed immediately by quitting must still purge its queue.
                lock (storageGate)
                {
                    if (purge && Directory.Exists(directory)) foreach (var file in Directory.GetFiles(directory, "*.json*")) File.Delete(file);
                }
            }
            catch (Exception) { /* Shutdown never blocks on reporting. */ }
            transport.Dispose();
            sending.Dispose();
            shutdown.Dispose();
        }
    }

    // Kept separate from the timer so queue/retry behavior can be tested without sleeping.
    internal async Task PumpAsync()
    {
        CancellationToken cancellation;
        int currentGeneration;
        string path;
        ErrorReport report;
        byte[] json;
        lock (storageGate)
        {
            bool clear;
            lock (gate) { clear = purge; purge = false; }
            if (clear)
            {
                try
                {
                    if (Directory.Exists(directory)) foreach (var file in Directory.GetFiles(directory, "*.json*")) File.Delete(file);
                }
                catch { lock (gate) purge = true; throw; }
            }
            lock (gate)
            {
                if (!enabled || disposed) return;
                currentGeneration = generation;
                cancellation = sending.Token;
            }
            Directory.CreateDirectory(directory);
            PersistDue(utcNow(), currentGeneration);
            PrunePending(utcNow());
            lock (gate)
            {
                if (!enabled || disposed || currentGeneration != generation || utcNow() < nextSend) return;
            }
            path = new DirectoryInfo(directory).GetFiles("*.json").OrderBy(f => f.LastWriteTimeUtc).Select(f => f.FullName).FirstOrDefault();
            if (path == null) return;
            try
            {
                var file = new FileInfo(path);
                if (file.Length > ReportCodec.MaximumBytes) throw new InvalidDataException();
                json = File.ReadAllBytes(path);
                report = ReportCodec.Decode(json);
                if (report == null || !Guid.TryParseExact(report.ReportId, "N", out _) || !DateTime.TryParse(report.Timestamp, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp) ||
                    stamp < utcNow().AddDays(-7) || stamp > utcNow().AddMinutes(5)) throw new InvalidDataException();
            }
            catch (Exception error) when (error is SerializationException or InvalidDataException or ArgumentException)
            {
                File.Delete(path); Warn("Removed an invalid or expired queued report."); return;
            }
            lock (gate)
            {
                if (!enabled || disposed || currentGeneration != generation) return;
                nextSend = utcNow().AddSeconds(60);
            }
        }

        ReportSendResult result;
        try { result = await transport.SendAsync(json, report.ReportId, cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or SerializationException)
        { result = new ReportSendResult(0); }
        lock (storageGate)
        {
            lock (gate)
            {
                if (currentGeneration != generation || !enabled || disposed) return;
            }
            if (!result.Retry)
            {
                File.Delete(path);
                lock (gate) retryAttempt = 0;
                if (!result.Success) Warn("Receiver rejected a report (HTTP " + result.Status + ").");
                return;
            }
            lock (gate)
            {
                retryAttempt++;
                var seconds = retryAttempt == 1 ? 60 : retryAttempt == 2 ? 300 : 1800;
                seconds += (report.ReportId.GetHashCode() & int.MaxValue) % 16;
                var retry = TimeSpan.FromSeconds(seconds);
                if (result.RetryAfter > retry) retry = result.RetryAfter;
                if (retry > TimeSpan.FromDays(7)) retry = TimeSpan.FromDays(7);
                nextSend = utcNow() + retry;
            }
            Warn("Upload deferred (HTTP " + result.Status + "); report kept for retry.");
        }
    }

    private void PersistDue(DateTime now, int currentGeneration)
    {
        for (var turn = 0; turn < 8; turn++)
        {
            KeyValuePair<string, Aggregate>[] batch;
            ReportEnvironment snapshot;
            string[] lines;
            string[] secrets;
            long dropped;
            lock (gate)
            {
                if (!enabled || disposed || currentGeneration != generation) return;
                batch = errors.Where(e => e.Value.Count > 0 && (e.Value.Reported == default || now - e.Value.Reported >= TimeSpan.FromMinutes(10)))
                    .Take(16).Select(e => new KeyValuePair<string, Aggregate>(e.Key, new Aggregate
                    {
                        Source = e.Value.Source,
                        Role = e.Value.Role,
                        Message = e.Value.Message,
                        Stack = e.Value.Stack,
                        First = e.Value.First,
                        Last = e.Value.Last,
                        Count = e.Value.Count,
                        Reported = e.Value.Reported,
                        Truncated = e.Value.Truncated
                    })).ToArray();
                if (batch.Length == 0) return;
                foreach (var item in batch) { errors[item.Key].Count = 0; errors[item.Key].Reported = now; }
                snapshot = environment;
                secrets = snapshot.Secrets.Concat(knownSecrets).ToArray();
                lines = context.ToArray();
                dropped = droppedEvents;
                droppedEvents = 0;
            }
            try
            {
                var redactor = new ReportRedactor(secrets);
                var report = new ErrorReport
                {
                    SessionId = sessionId,
                    Timestamp = ErrorReport.Time(now),
                    NebulaeVersion = ReportCodec.ClipUtf8(snapshot.NebulaeVersion, 128),
                    GameVersion = ReportCodec.ClipUtf8(snapshot.GameVersion, 128),
                    Runtime = redactor.Clean(snapshot.Runtime, 512),
                    Role = snapshot.Role,
                    Mods = snapshot.Mods.Take(128).Select(m => redactor.Clean(m, 512)).ToArray(),
                    Context = redactor.Clean(string.Join("\n", lines), 32768),
                    Truncated = batch.Any(e => e.Value.Truncated) || snapshot.Mods.Length > 128,
                    DroppedEvents = dropped,
                    Errors = batch.Select(item => item.Value).Select(e => new ReportError
                    {
                        Source = e.Source,
                        Role = e.Role,
                        Message = redactor.Clean(e.Message, 16384),
                        Stack = redactor.Clean(e.Stack, 49152),
                        FirstAt = ErrorReport.Time(e.First),
                        LastAt = ErrorReport.Time(e.Last),
                        Count = e.Count
                    }).ToArray()
                };
                report.Truncated |= redactor.Truncated;
                var bytes = ReportCodec.EncodeBounded(report);
                lock (gate) { if (!enabled || disposed || currentGeneration != generation) return; }
                var path = Path.Combine(directory, report.ReportId + ".json");
                File.WriteAllBytes(path + ".tmp", bytes);
                File.Move(path + ".tmp", path);
            }
            catch
            {
                lock (gate)
                {
                    if (enabled && !disposed && currentGeneration == generation)
                    {
                        droppedEvents += dropped;
                        foreach (var item in batch)
                        {
                            if (errors.TryGetValue(item.Key, out var entry))
                            {
                                entry.Count = Math.Min(long.MaxValue - item.Value.Count, entry.Count) + item.Value.Count;
                                entry.First = item.Value.First;
                                entry.Reported = item.Value.Reported;
                            }
                            else if (errors.Count < 128) errors.Add(item.Key, item.Value);
                            else droppedEvents += item.Value.Count;
                        }
                    }
                }
                throw;
            }
        }
    }

    private void PrunePending(DateTime now)
    {
        var files = new DirectoryInfo(directory).GetFiles("*.json*").OrderBy(f => f.LastWriteTimeUtc).ToList();
        var bytes = files.Sum(f => f.Length);
        var count = files.Count;
        var removed = false;
        foreach (var file in files)
        {
            if (!file.Name.EndsWith(".tmp", StringComparison.Ordinal) && file.LastWriteTimeUtc >= now.AddDays(-7) && bytes <= PendingBytes && count <= 128) continue;
            bytes -= file.Length; count--; file.Delete(); removed = true;
        }
        if (removed) Warn("Pruned old or excess queued reports (7 days / 8 MiB / 128 files).");
    }

    private void Warn(string text)
    {
        var now = utcNow();
        if (now - lastWarning < TimeSpan.FromMinutes(5)) return;
        lastWarning = now;
        try { warning?.Invoke("[error-reporter] " + text); } catch (Exception) { /* Never recurse into reporting. */ }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            sending.Cancel();
            shutdown.Cancel();
        }
    }
}
