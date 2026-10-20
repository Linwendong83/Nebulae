using System;
using System.Collections.Generic;
using System.Linq;

namespace NebulaeModel.Networking;

/// <summary>A connection's immutable changes after its world/factory snapshot cuts.</summary>
internal sealed class JoinSyncBuffer
{
    public const long ConnectionLimit = 64L * 1024 * 1024;
    public const long ServerLimit = 256L * 1024 * 1024;
    public const int TimeoutSeconds = 300;
    public const int BatchCount = 256;
    public const int BatchBytes = 256 * 1024;

    public readonly struct Change
    {
        public Change(int scope, byte[] data) { Scope = scope; Data = data; }
        // 0 = global snapshot; positive values = factory planet id.
        public int Scope { get; }
        public byte[] Data { get; }
    }

    private readonly HashSet<int> cuts = new();
    private readonly List<Change> pending = new();
    private readonly Queue<(long Sequence, int Bytes)> outstanding = new();
    public long Generation { get; }
    public long Bytes { get; private set; }
    public long SentSequence { get; private set; }
    public long AcknowledgedSequence { get; private set; } = -1;
    public long RequiredSequence { get; private set; }
    public double LastProgress { get; private set; }
    public bool Replaying { get; private set; }
    public bool Failed { get; private set; }
    private long remoteProgress;

    public JoinSyncBuffer(long generation, double now)
    {
        Generation = generation;
        LastProgress = now;
    }

    public void Checkpoint(int scope, double now)
    {
        if (Replaying) throw new InvalidOperationException("Snapshot changed during join replay.");
        cuts.Add(scope);
        var retained = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            var entry = pending[i];
            if (entry.Scope == scope || scope > 0 && entry.Scope == -scope) Bytes -= entry.Data.Length;
            else pending[retained++] = entry;
        }
        if (retained < pending.Count) pending.RemoveRange(retained, pending.Count - retained);
        LastProgress = now;
    }

    public bool HasCheckpoint(int scope) => cuts.Contains(scope);

    public bool Append(int scope, byte[] data, long limit = ConnectionLimit)
    {
        if (Failed || !cuts.Contains(scope < 0 ? 0 : scope)) return !Failed;
        if (data == null || data.Length == 0) throw new ArgumentException("Empty join change.", nameof(data));
        if (Bytes + data.Length > limit) { Failed = true; return false; }
        // Transport serialization already returned an owned, immutable byte array.
        pending.Add(new Change(scope, data));
        Bytes += data.Length;
        return true;
    }

    public void BeginReplay(double now)
    {
        if (!cuts.Contains(0)) throw new InvalidOperationException("Missing world snapshot.");
        Replaying = true;
        LastProgress = now;
    }

    public Change[][] Drain()
    {
        var batches = new List<Change[]>();
        var batch = new List<Change>();
        var bytes = 0;
        foreach (var entry in pending)
        {
            if (batch.Count > 0 && (batch.Count >= BatchCount || bytes + entry.Data.Length > BatchBytes))
            { batches.Add(batch.ToArray()); batch.Clear(); bytes = 0; }
            batch.Add(entry); bytes += entry.Data.Length;
            SentSequence++;
            outstanding.Enqueue((SentSequence, entry.Data.Length));
        }
        if (batch.Count > 0) batches.Add(batch.ToArray());
        pending.Clear();
        return batches.ToArray();
    }

    public bool Acknowledge(long generation, long sequence, double now)
    {
        if (Failed || !Replaying || generation != Generation || sequence < RequiredSequence ||
            sequence > SentSequence || sequence <= AcknowledgedSequence)
            return false;
        AcknowledgedSequence = sequence;
        while (outstanding.Count > 0 && outstanding.Peek().Sequence <= sequence)
            Bytes -= outstanding.Dequeue().Bytes;
        LastProgress = now;
        return true;
    }

    public void SealInitialReplay() => RequiredSequence = SentSequence;

    public void ReportProgress(long generation, long progress, double now)
    {
        if (generation != Generation || progress <= remoteProgress) return;
        remoteProgress = progress;
        LastProgress = now;
    }

    public bool Expired(double now) => Failed || now - LastProgress >= TimeoutSeconds;
}
