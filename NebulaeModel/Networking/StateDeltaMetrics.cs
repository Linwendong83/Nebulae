using System.Threading;
using System.Diagnostics;
using System.Globalization;

namespace NebulaeModel.Networking;

internal static class StateDeltaMetrics
{
    private static long full, patches, before, after, encodingTicks, peakCache;
    public static long FullStates => Interlocked.Read(ref full);
    public static long DeltaStates => Interlocked.Read(ref patches);
    public static long BytesBefore => Interlocked.Read(ref before);
    public static long BytesAfter => Interlocked.Read(ref after);
    public static void RecordEncoding(long ticks) => Interlocked.Add(ref encodingTicks, ticks);
    public static void RecordCache(long bytes)
    {
        long previous;
        do { previous = Interlocked.Read(ref peakCache); if (bytes <= previous) return; }
        while (Interlocked.CompareExchange(ref peakCache, bytes, previous) != previous);
    }
    public static void Record(int original, int encoded, bool delta)
    {
        Interlocked.Add(ref before, original); Interlocked.Add(ref after, encoded);
        if (delta) Interlocked.Increment(ref patches); else Interlocked.Increment(ref full);
    }
    public static string Describe() => "field delta full=" + FullStates + " delta=" + DeltaStates +
        " bytesBefore=" + BytesBefore + " bytesAfter=" + BytesAfter +
        " encodingMs=" + (Interlocked.Read(ref encodingTicks) * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture) +
        " peakCacheBytes=" + Interlocked.Read(ref peakCache);
}
