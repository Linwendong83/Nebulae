using System;
using System.Collections.Generic;
using System.Linq;

namespace NebulaeModel.Networking;

/// <summary>A connection's immutable changes after its world/factory snapshot cuts.</summary>
/// <remarks>
/// <para>
/// The host keeps simulating while a peer joins, so the changes made after the peer's snapshot
/// cuts have to be replayed once it finishes loading. Absolute state updates for one object are
/// kept once, at their latest position; operations that carry an event (an item placed on a belt,
/// a building destroyed, ...) are always appended. Replacing a state update must move it to the
/// tail: a newer value may not be applied before the events that preceded it.
/// </para>
/// <para>
/// The budget is a guard against unbounded memory, not a rejection policy: the host never pauses
/// the shared world for a joining peer. A backlog that reaches the hard limit keeps
/// <see cref="Failed"/> so the existing disconnect path can ask the peer to reconnect.
/// </para>
/// </remarks>
internal sealed class JoinSyncBuffer
{
    public const long ConnectionLimit = 64L * 1024 * 1024;
    public const long ServerLimit = 256L * 1024 * 1024;
    public const int TimeoutSeconds = 300;
    public const int BatchCount = 256;
    public const int BatchBytes = 256 * 1024;

    /// <summary>One replayable change. <see cref="Key"/> is null for append-only operations.</summary>
    public sealed class Change
    {
        public Change(int scope, byte[] data, string key = null) { Scope = scope; Data = data; Key = key; }
        // 0 = global snapshot; positive values = factory planet id.
        public int Scope { get; }
        public byte[] Data { get; }
        /// <summary>Identity of the object whose latest absolute state this change carries.</summary>
        public string Key { get; }
    }

    private readonly HashSet<int> cuts = new();
    private readonly LinkedList<Change> pending = new();
    private readonly Dictionary<string, LinkedListNode<Change>> coalesced = new(StringComparer.Ordinal);
    private readonly Dictionary<int, long> scopeBytes = new();
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
        for (var node = pending.First; node != null;)
        {
            var next = node.Next;
            var entry = node.Value;
            if (entry.Scope == scope || scope > 0 && entry.Scope == -scope) Remove(node);
            node = next;
        }
        LastProgress = now;
    }

    /// <summary>Appends one change; a non-null key replaces that object's earlier state change.</summary>
    public bool Append(int scope, byte[] data, long limit = ConnectionLimit, string key = null)
    {
        if (Failed || !cuts.Contains(scope < 0 ? 0 : scope)) return !Failed;
        if (data == null || data.Length == 0) throw new ArgumentException("Empty join change.", nameof(data));
        if (Bytes + data.Length > limit) { Failed = true; return false; }
        // Transport serialization already returned an owned, immutable byte array.
        if (key != null && coalesced.TryGetValue(key, out var replaced)) Remove(replaced);
        var node = pending.AddLast(new Change(scope, data, key));
        if (key != null) coalesced[key] = node;
        Bytes += data.Length;
        scopeBytes[scope] = scopeBytes.TryGetValue(scope, out var held) ? held + data.Length : data.Length;
        return true;
    }

    private void Remove(LinkedListNode<Change> node)
    {
        pending.Remove(node);
        var entry = node.Value;
        Bytes -= entry.Data.Length;
        if (scopeBytes.TryGetValue(entry.Scope, out var held))
        {
            held -= entry.Data.Length;
            if (held > 0) scopeBytes[entry.Scope] = held; else scopeBytes.Remove(entry.Scope);
        }
        if (entry.Key != null) coalesced.Remove(entry.Key);
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
        coalesced.Clear();
        scopeBytes.Clear();
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

    /// <summary>Bytes held per scope, largest first, for the pressure log.</summary>
    public string ScopeSummary() => scopeBytes.Count == 0
        ? "none"
        : string.Join(",", scopeBytes.OrderByDescending(entry => entry.Value).Take(4)
            .Select(entry => entry.Key + ":" + entry.Value));
}