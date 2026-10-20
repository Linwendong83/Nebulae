#region

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;

#endregion

namespace NebulaeModel.Authority;

/// <summary>
/// Latency distribution of one periodic cost, in milliseconds.
/// </summary>
/// <remarks>
/// The percentile is computed from a bounded sample window rather than an unbounded history, so a
/// long run cannot grow the meter's memory: the meter's own footprint is fixed, which is what makes
/// "no continuous growth" a property a test can check instead of a hope.
/// </remarks>
internal readonly struct AuthorityLatencySummary
{
    public AuthorityLatencySummary(int count, double p50, double p95, double max, double mean)
    {
        Count = count;
        P50Ms = p50;
        P95Ms = p95;
        MaxMs = max;
        MeanMs = mean;
    }

    /// <summary>Samples in the retained window.</summary>
    public int Count { get; }

    public double P50Ms { get; }
    public double P95Ms { get; }
    public double MaxMs { get; }
    public double MeanMs { get; }

    public static AuthorityLatencySummary Empty => new(0, 0, 0, 0, 0);

    public override string ToString() => "n=" + Count + " p50=" + P50Ms.ToString("F3", CultureInfo.InvariantCulture) +
                                        "ms p95=" + P95Ms.ToString("F3", CultureInfo.InvariantCulture) +
                                        "ms max=" + MaxMs.ToString("F3", CultureInfo.InvariantCulture) + "ms";
}

/// <summary>
/// The authority session's performance meter.
/// </summary>
/// <remarks>
/// <para>
/// The meter answers four questions and nothing else: how long the host's capture takes per tick,
/// how long a client's apply takes per frame, how many bytes travel per family, and how deep the
/// queues get. Every counter here is either monotonic or a bounded window, so a two-hour run and a
/// two-second run have the same memory footprint.
/// </para>
/// <para>
/// Rates use host ticks because local wall clocks cannot compare two instances: bytes per host tick is
/// comparable across measurements, bytes per wall second
/// is a property of the machine that produced it.
/// </para>
/// <para>
/// <see cref="Describe"/> appends the transport's compression totals when the transport compressed
/// anything. Those counters are process-wide rather than per-session, so the line reads as
/// calibration data for the compression threshold, not as a session metric.
/// </para>
/// </remarks>
internal sealed class AuthorityPerfMeter
{
    public LoadingTimings Stages { get; } = new();
    /// <summary>Families the per-family counters cover; the enum's highest value plus one.</summary>
    public const int FamilyCount = 13;

    private readonly double[] captureWindow;
    private readonly double[] applyWindow;
    private readonly int windowSize;
    private readonly long[] packetsSentByFamily = new long[FamilyCount];
    private readonly long[] bytesSentByFamily = new long[FamilyCount];
    private readonly long[] packetsReceivedByFamily = new long[FamilyCount];
    private readonly long[] bytesReceivedByFamily = new long[FamilyCount];

    private int captureCount;
    private int captureNext;
    private double captureTotalMs;
    private int applyCount;
    private int applyNext;
    private double applyTotalMs;

    private long firstHostTick;
    private long lastHostTick;
    private bool hasHostTick;

    /// <param name="label">Role label used in <see cref="Describe"/> ("host"/"client1").</param>
    /// <param name="ticksPerSecond">Host tick rate the byte rate is expressed against.</param>
    /// <param name="windowSize">Retained latency samples per cost.</param>
    public AuthorityPerfMeter(string label = "authority", double ticksPerSecond = 60.0, int windowSize = 512)
    {
        if (windowSize <= 0) throw new ArgumentOutOfRangeException(nameof(windowSize));
        if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
        Label = label ?? "authority";
        TicksPerSecond = ticksPerSecond;
        this.windowSize = windowSize;
        captureWindow = new double[windowSize];
        applyWindow = new double[windowSize];
    }

    public string Label { get; }

    public double TicksPerSecond { get; }

    public int WindowSize => windowSize;

    // ---- host capture ---------------------------------------------------------------------

    public long CaptureFrames { get; private set; }
    public long CapturePackets { get; private set; }
    private long captureBytes;
    public long CaptureBytes => Interlocked.Read(ref captureBytes);
    internal void RecordDeferredCaptureBytes(long bytes) => Interlocked.Add(ref captureBytes, bytes);

    /// <summary>Worst capture cost observed, kept as a monotonic high-water mark.</summary>
    public double PeakCaptureMs { get; private set; }

    /// <summary>Deepest retained-event backlog seen after a capture, across all scopes.</summary>
    public int PeakPendingEvents { get; private set; }

    /// <summary>
    /// Canonical state reads the capture pass asked the world view for.
    /// </summary>
    /// <remarks>
    /// The read count is the sampling side of the capture pass, and it is deliberately separate from
    /// <see cref="StatesPublished"/>: an adapter that suppresses an unchanged record still costs a
    /// read, so "reads" is what a cadence change moves and "published" is what the wire moves. A run
    /// that only reports bytes cannot tell a smaller packet from a smaller scan.
    /// </remarks>
    public long StateReads { get; private set; }

    /// <summary>State events the capture pass actually appended.</summary>
    public long StatesPublished { get; private set; }

    /// <summary>Records one host capture pass.</summary>
    public void RecordHostCapture(long hostTick, double milliseconds, int packets, long bytes,
        int pendingEvents)
    {
        CaptureFrames++;
        CapturePackets += packets;
        Interlocked.Add(ref captureBytes, bytes);
        if (milliseconds > PeakCaptureMs) PeakCaptureMs = milliseconds;
        if (pendingEvents > PeakPendingEvents) PeakPendingEvents = pendingEvents;
        Push(captureWindow, ref captureCount, ref captureNext, ref captureTotalMs, milliseconds);
        TouchTick(hostTick);
    }

    /// <summary>Records one canonical state read, whether or not it produced an event.</summary>
    public void RecordStateRead() => StateReads++;

    /// <summary>Records one state event appended by the capture pass.</summary>
    public void RecordStatePublished() => StatesPublished++;

    /// <summary>State reads that produced no event: suppressed by an adapter or byte-identical.</summary>
    public long StatesUnchanged => StateReads - StatesPublished;

    // ---- client apply ---------------------------------------------------------------------

    public long ApplyFrames { get; private set; }
    public long ApplyMessages { get; private set; }
    public long ApplyBytes { get; private set; }

    public double PeakApplyMs { get; private set; }

    /// <summary>Deepest apply queue left behind after a frame, in messages.</summary>
    public int PeakQueuedApplyMessages { get; private set; }

    /// <summary>Records one client apply pass.</summary>
    public void RecordClientApply(long hostTick, double milliseconds, int messages, long bytes,
        int queuedAfter)
    {
        ApplyFrames++;
        ApplyMessages += messages;
        ApplyBytes += bytes;
        if (milliseconds > PeakApplyMs) PeakApplyMs = milliseconds;
        if (queuedAfter > PeakQueuedApplyMessages) PeakQueuedApplyMessages = queuedAfter;
        Push(applyWindow, ref applyCount, ref applyNext, ref applyTotalMs, milliseconds);
        TouchTick(hostTick);
    }

    // ---- traffic --------------------------------------------------------------------------

    private long packetsSent, bytesSent;
    public long PacketsSent => Interlocked.Read(ref packetsSent);
    public long BytesSent => Interlocked.Read(ref bytesSent);
    public long PacketsReceived { get; private set; }
    public long BytesReceived { get; private set; }

    public void RecordPacketSent(AuthorityFamily family, long bytes)
    {
        var index = (int)family;
        if ((uint)index >= FamilyCount) return;
        Interlocked.Increment(ref packetsSentByFamily[index]);
        Interlocked.Add(ref bytesSentByFamily[index], bytes);
        Interlocked.Increment(ref packetsSent);
        Interlocked.Add(ref bytesSent, bytes);
    }

    public void RecordPacketReceived(AuthorityFamily family, long bytes)
    {
        var index = (int)family;
        if ((uint)index >= FamilyCount) return;
        packetsReceivedByFamily[index]++;
        bytesReceivedByFamily[index] += bytes;
        PacketsReceived++;
        BytesReceived += bytes;
    }

    public long PacketsSentFor(AuthorityFamily family) => Bucket(packetsSentByFamily, family);

    public long BytesSentFor(AuthorityFamily family) => Bucket(bytesSentByFamily, family);

    public long PacketsReceivedFor(AuthorityFamily family) => Bucket(packetsReceivedByFamily, family);

    public long BytesReceivedFor(AuthorityFamily family) => Bucket(bytesReceivedByFamily, family);

    /// <summary>Host ticks spanned by every recorded sample, or zero before two samples exist.</summary>
    public long ElapsedTicks => lastHostTick > firstHostTick ? lastHostTick - firstHostTick : 0;

    /// <summary>Sent bytes per second, derived from host ticks rather than wall clock.</summary>
    public double SentBytesPerSecond => Rate(BytesSent);

    public double ReceivedBytesPerSecond => Rate(BytesReceived);

    // ---- summaries ------------------------------------------------------------------------

    public AuthorityLatencySummary CaptureLatency => Summarize(captureWindow, captureCount, captureTotalMs);

    public AuthorityLatencySummary ApplyLatency => Summarize(applyWindow, applyCount, applyTotalMs);

    /// <summary>One line per fact, for the harness's perf report.</summary>
    public List<string> Describe()
    {
        var lines = new List<string>
        {
            "perf label=" + Label + " frames=" + CaptureFrames + " applyFrames=" + ApplyFrames +
            " elapsedTicks=" + ElapsedTicks,
            "capture " + CaptureLatency + " peak=" + Ms(PeakCaptureMs) + " packets=" + CapturePackets +
            " bytes=" + CaptureBytes + " stateReads=" + StateReads +
            " statesPublished=" + StatesPublished + " statesUnchanged=" + StatesUnchanged,
            "apply " + ApplyLatency + " peak=" + Ms(PeakApplyMs) + " messages=" + ApplyMessages +
            " bytes=" + ApplyBytes,
            "queues peakPendingEvents=" + PeakPendingEvents + " peakQueuedApplyMessages=" + PeakQueuedApplyMessages,
            "stages " + Stages.Describe(),
            "traffic sentPackets=" + PacketsSent + " sentBytes=" + BytesSent +
            " sentBytesPerSecond=" + Rate(BytesSent).ToString("F0", CultureInfo.InvariantCulture) +
            " receivedPackets=" + PacketsReceived + " receivedBytes=" + BytesReceived +
            " receivedBytesPerSecond=" + Rate(BytesReceived).ToString("F0", CultureInfo.InvariantCulture)
        };

        if (PacketCompression.HasTraffic) lines.Add(PacketCompression.Describe());

        foreach (AuthorityFamily family in Enum.GetValues(typeof(AuthorityFamily)))
        {
            var sent = BytesSentFor(family);
            var received = BytesReceivedFor(family);
            if (sent == 0 && received == 0) continue;
            lines.Add("family " + family + " sentPackets=" + PacketsSentFor(family) + " sentBytes=" + sent +
                      " receivedPackets=" + PacketsReceivedFor(family) + " receivedBytes=" + received);
        }
        return lines;
    }

    /// <summary>
    /// Deterministic per-family rate table, for the machine-readable report.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="Describe"/> so a script can read counters without parsing prose,
    /// and so a test can assert on exact numbers instead of formatted text.
    /// </remarks>
    public string ToCsv()
    {
        var builder = new StringBuilder();
        builder.AppendLine("label,family,sentPackets,sentBytes,receivedPackets,receivedBytes,seconds,bytesPerSecond");
        var seconds = TicksPerSecond > 0 ? ElapsedTicks / TicksPerSecond : 0;
        foreach (AuthorityFamily family in Enum.GetValues(typeof(AuthorityFamily)))
        {
            var sent = BytesSentFor(family);
            var received = BytesReceivedFor(family);
            if (sent == 0 && received == 0) continue;
            var perSecond = seconds > 0 ? sent / seconds : 0;
            builder.Append(Label).Append(',').Append(family).Append(',')
                .Append(PacketsSentFor(family)).Append(',').Append(sent).Append(',')
                .Append(PacketsReceivedFor(family)).Append(',').Append(received).Append(',')
                .Append(seconds.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                .Append(perSecond.ToString("F0", CultureInfo.InvariantCulture)).AppendLine();
        }
        return builder.ToString();
    }

    private double Rate(long bytes)
    {
        var seconds = TicksPerSecond > 0 ? ElapsedTicks / TicksPerSecond : 0;
        return seconds > 0 ? bytes / seconds : 0;
    }

    private void TouchTick(long hostTick)
    {
        // Tracked with a flag rather than by comparing against zero: a host tick of 0 is a real
        // moment (the first frame after a load), and treating it as "unset" would silently report an
        // elapsed time that starts one sample late.
        if (!hasHostTick)
        {
            firstHostTick = hostTick;
            hasHostTick = true;
        }
        lastHostTick = hostTick;
    }

    private static long Bucket(long[] table, AuthorityFamily family)
    {
        var index = (int)family;
        return (uint)index < FamilyCount ? Interlocked.Read(ref table[index]) : 0;
    }

    private static string Ms(double value) => value.ToString("F3", CultureInfo.InvariantCulture) + "ms";

    private static void Push(double[] window, ref int count, ref int next, ref double total, double value)
    {
        if (count < window.Length)
        {
            window[next] = value;
            count++;
        }
        else
        {
            // Overwrite the oldest sample: the window is a ring, so the summary always reflects the
            // most recent window rather than a session-long average that hides a regression.
            total -= window[next];
            window[next] = value;
        }
        total += value;
        next = (next + 1) % window.Length;
    }

    private static AuthorityLatencySummary Summarize(double[] window, int count, double total)
    {
        if (count == 0) return AuthorityLatencySummary.Empty;
        var sorted = new double[count];
        Array.Copy(window, sorted, count);
        Array.Sort(sorted);
        double max = sorted[count - 1];
        return new AuthorityLatencySummary(count, Percentile(sorted, 0.50), Percentile(sorted, 0.95), max,
            total / count);
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        var index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        if (index < 0) index = 0;
        if (index >= sorted.Length) index = sorted.Length - 1;
        return sorted[index];
    }
}
