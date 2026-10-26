using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace NebulaeModel.Logger;

/// <summary>Bounded, lazy runtime diagnostics. Repeated faults keep reporting after the first window.</summary>
internal static class Diagnostics
{
    private static readonly DiagnosticThrottle throttle = new();
    internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public static void Warn(string key, Func<string> message, Exception error = null)
    {
        if (throttle.TryAcquire(key, out var suppressed))
            Log.Warn(Format(message(), error, suppressed));
    }

    public static void Error(string key, Func<string> message, Exception error = null)
    {
        if (throttle.TryAcquire(key, out var suppressed))
            Log.ErrorWithoutPopup(Format(message(), error, suppressed));
    }

    private static string Format(string message, Exception error, long suppressed) =>
        message + (suppressed == 0 ? "" : $" suppressed={suppressed}") + (error == null ? "" : "\n" + error);
}

internal sealed class DiagnosticThrottle
{
    private sealed class Entry { public double Last; public long Suppressed; }
    private readonly Dictionary<string, Entry> entries = new();
    private readonly Queue<string> insertionOrder = new();
    private readonly Func<double> now;
    private readonly double intervalSeconds;
    private readonly int capacity;

    public DiagnosticThrottle(Func<double> now = null, double intervalSeconds = 30, int capacity = 1024)
    {
        if (intervalSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.now = now ?? (() => Diagnostics.Now);
        this.intervalSeconds = intervalSeconds;
        this.capacity = capacity;
    }

    public bool TryAcquire(string key, out long suppressed)
    {
        lock (entries)
        {
            var time = now();
            suppressed = 0;
            if (entries.TryGetValue(key, out var entry))
            {
                if (time - entry.Last < intervalSeconds) { entry.Suppressed++; return false; }
                suppressed = entry.Suppressed;
                entry.Last = time; entry.Suppressed = 0;
                return true;
            }
            if (entries.Count == capacity) entries.Remove(insertionOrder.Dequeue());
            entries.Add(key, new Entry { Last = time });
            insertionOrder.Enqueue(key);
            return true;
        }
    }
}

/// <summary>Tracks elapsed wall time, including paused simulation. Owners clear it with their session.</summary>
internal sealed class DiagnosticWatchdog
{
    private sealed class Wait { public double Started, Reported; public bool Warned; public long Progress; }
    private readonly Dictionary<string, Wait> waits = new();
    private readonly Func<double> now;
    public DiagnosticWatchdog(Func<double> now = null) => this.now = now ?? (() => Diagnostics.Now);

    public void Observe(string key, bool waiting, Func<string> context, double timeoutSeconds = 30, long progress = 0)
    {
        lock (waits)
        {
            var time = now();
            if (!waiting)
            {
                if (waits.TryGetValue(key, out var done) && done.Warned)
                    Log.Debug($"[diagnostics] wait recovered key={key} elapsed={time - done.Started:F1}s {context()}");
                waits.Remove(key);
                return;
            }
            if (!waits.TryGetValue(key, out var wait))
            {
                // A changing object population must not retain diagnostics indefinitely.
                if (waits.Count >= 1024) waits.Clear();
                waits.Add(key, new Wait { Started = time, Reported = time, Progress = progress });
                return;
            }
            if (wait.Progress != progress)
            {
                if (wait.Warned) Log.Debug($"[diagnostics] progress resumed key={key} elapsed={time - wait.Started:F1}s {context()}");
                wait.Started = wait.Reported = time; wait.Warned = false; wait.Progress = progress;
                return;
            }
            if (time - wait.Started < timeoutSeconds || wait.Warned && time - wait.Reported < 30) return;
            wait.Warned = true; wait.Reported = time;
            Log.Warn($"[diagnostics] waiting key={key} elapsed={time - wait.Started:F1}s {context()}");
        }
    }

    public void Clear() { lock (waits) waits.Clear(); }
}
