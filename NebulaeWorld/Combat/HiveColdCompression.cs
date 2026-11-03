using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace NebulaeWorld.Combat;

// Workers own serialized bytes, never hive objects, builders or Unity resources.
// A full queue falls back to inline compression instead of retaining more layouts.
internal static class HiveColdCompression
{
    internal const int MaxPendingBytes = 16 * 1024 * 1024;
    internal static readonly int MaxWorkers = Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2));
    private static readonly object gate = new();
    private static readonly List<Task<byte[]>> jobs = new();
    private static int pendingBytes, workers;

    internal static bool TryQueue(byte[] source, int count, out Task<byte[]> job)
    {
        job = null;
        lock (gate)
        {
            jobs.RemoveAll(task => task.Status == TaskStatus.RanToCompletion);
            if (workers >= MaxWorkers || count > MaxPendingBytes - pendingBytes) return false;
            // The caller reuses its scratch buffer for the very next hive.
            var owned = new byte[count];
            Buffer.BlockCopy(source, 0, owned, 0, count);
            pendingBytes += count; workers++;
            try
            {
                job = Task.Run(() =>
                {
                    try { return Compress(owned, count); }
                    finally { lock (gate) { pendingBytes -= count; workers--; } }
                });
                jobs.Add(job);
                return true;
            }
            catch
            {
                pendingBytes -= count; workers--;
                throw;
            }
        }
    }

    internal static byte[] Compress(byte[] bytes, int count)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true))
            deflate.Write(bytes, 0, count);
        return output.ToArray();
    }

    // Teardown calls this after joining native import work, so no producer remains.
    internal static void Complete()
    {
        Task<byte[]>[] pending;
        lock (gate) pending = jobs.ToArray();
        try { Task.WaitAll(pending); }
        finally { lock (gate) foreach (var job in pending) jobs.Remove(job); }
    }
}
