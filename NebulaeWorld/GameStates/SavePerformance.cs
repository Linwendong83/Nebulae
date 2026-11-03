using System;
using System.Diagnostics;
using System.IO;
using NebulaeModel.Logger;

namespace NebulaeWorld.GameStates;

internal static class SavePerformance
{
    internal static bool Enabled = Environment.GetEnvironmentVariable("NEBULAE_BUFFERED_SAVE") != "0";
    [ThreadStatic] private static double exportSeconds, writeSeconds, companionSeconds;
    [ThreadStatic] private static long bytes;
    internal static void Begin() { exportSeconds = writeSeconds = companionSeconds = 0; bytes = 0; }
    internal static void End(double total) => Log.Debug(
        $"[save-perf] total={total:F4}s worldExport={exportSeconds:F4}s fileWrite={writeSeconds:F4}s companion={companionSeconds:F4}s bytes={bytes} buffered={Enabled}");
    internal static void RecordWrite(long ticks, int count) { writeSeconds += (double)ticks / Stopwatch.Frequency; bytes += count; }
    internal static void RecordCompanion(double seconds) => companionSeconds += seconds;
    internal static void Export(GameData data, BinaryWriter writer)
    {
        var start = Stopwatch.GetTimestamp();
        try { data.Export(writer); }
        finally { exportSeconds += (double)(Stopwatch.GetTimestamp() - start) / Stopwatch.Frequency; }
    }
    internal static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share) =>
        Enabled && Multiplayer.Session?.IsServer == true ? new SaveWriteStream(path, mode, access, share) : new FileStream(path, mode, access, share);
    internal static BinaryWriter Writer(Stream stream) => Enabled && Multiplayer.Session?.IsServer == true
        ? new SaveBinaryWriter(stream) : new BinaryWriter(stream);
}

// Mono's floating point writer can allocate a conversion array per scalar. Reuse
// BinaryWriter's integer buffer, preserving every IEEE bit and the native file format.
internal sealed class SaveBinaryWriter : BinaryWriter
{
    internal SaveBinaryWriter(Stream stream) : base(stream) { }
    public override unsafe void Write(float value) => base.Write(*(int*)&value);
    public override unsafe void Write(double value) => base.Write(*(long*)&value);
}

// Buffer tiny scalar writes in managed memory. File IO timing is sampled per block,
// not per scalar; worldExport includes the writes made while exporting (not additive).
internal sealed class SaveWriteStream : FileStream
{
    private readonly byte[] buffer = new byte[1024 * 1024];
    private int used;
    internal SaveWriteStream(string path, FileMode mode, FileAccess access, FileShare share)
        : base(path, mode, access, share, 1) { }
    public override long Position { get => base.Position + used; set { FlushBuffer(); base.Position = value; } }
    public override long Length => Math.Max(base.Length, Position);
    public override void Write(byte[] source, int offset, int count)
    {
        if (!base.CanWrite) throw new ObjectDisposedException(nameof(SaveWriteStream));
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (offset < 0 || count < 0 || offset > source.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        while (count > 0)
        {
            if (used == 0 && count >= buffer.Length) { WriteBlock(source, offset, count); return; }
            var copy = Math.Min(count, buffer.Length - used);
            Buffer.BlockCopy(source, offset, buffer, used, copy);
            used += copy; offset += copy; count -= copy;
            if (used == buffer.Length) FlushBuffer();
        }
    }
    public override void WriteByte(byte value)
    {
        if (!base.CanWrite) throw new ObjectDisposedException(nameof(SaveWriteStream));
        buffer[used++] = value;
        if (used == buffer.Length) FlushBuffer();
    }
    private void WriteBlock(byte[] source, int offset, int count)
    {
        var started = Stopwatch.GetTimestamp();
        base.Write(source, offset, count);
        SavePerformance.RecordWrite(Stopwatch.GetTimestamp() - started, count);
    }
    private void FlushBuffer()
    {
        if (used == 0) return;
        WriteBlock(buffer, 0, used);
        used = 0;
    }
    public override long Seek(long offset, SeekOrigin origin)
    { FlushBuffer(); return base.Seek(offset, origin); }
    public override void SetLength(long value) { FlushBuffer(); base.SetLength(value); }
    public override void Flush() { FlushBuffer(); base.Flush(); }
    public override void Flush(bool flushToDisk) { FlushBuffer(); base.Flush(flushToDisk); }
    protected override void Dispose(bool disposing)
    {
        try { if (disposing) FlushBuffer(); }
        finally { base.Dispose(disposing); }
    }
}
