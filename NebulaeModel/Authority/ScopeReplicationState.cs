#region

using System;
using System.Collections.Generic;

#endregion

namespace NebulaeModel.Authority;

/// <summary>Which membership or state change one <see cref="ScopeEvent"/> publishes.</summary>
internal enum ScopeEventKind : byte
{
    /// <summary>The object exists. Identity first; state may follow.</summary>
    Spawn = 1,

    /// <summary>Absolute canonical state changed. Applies only to an existing identity.</summary>
    State = 2,

    /// <summary>
    /// The object left the pool without the host recording a death for it: a unit returning to its
    /// formation, a relay that landed, a builder the hive reclaimed. Removal only — vanilla's
    /// <c>RemoveEnemyFinal</c> path produces no wreckage, so a client must not present one either.
    /// </summary>
    Despawn = 3,

    /// <summary>
    /// The object of this exact key is dead: the host's death ledger holds its tombstone, which is
    /// the same fact <c>CombatStat.HandleZeroHp</c> settled through the Kill path. Final revision;
    /// never republished. A client may present the vanilla death effect for this one.
    /// </summary>
    Death = 4
}

/// <summary>One event in a scope's replication log.</summary>
/// <remarks>
/// The event carries everything a subscriber needs to apply it: the key with its generation, the
/// revision it was published at, and for state events the canonical bytes. It holds no game or
/// Unity reference, so a fake world can produce it in a plain test process.
/// </remarks>
internal readonly struct ScopeEvent
{
    public ScopeEvent(long logSequence, ScopeEventKind kind, in ObjectKey key, long revision, long hostTick,
        byte[] state, ReplicationInterest interest = default)
    {
        LogSequence = logSequence;
        Kind = kind;
        Key = key;
        Revision = revision;
        HostTick = hostTick;
        State = state;
        Interest = interest;
    }

    /// <summary>Host-internal position in the scope's log. Subscribers cursor over these.</summary>
    public long LogSequence { get; }

    public ScopeEventKind Kind { get; }

    public ObjectKey Key { get; }

    public long Revision { get; }

    public long HostTick { get; }

    /// <summary>Canonical state bytes for <see cref="ScopeEventKind.State"/> events; null otherwise.</summary>
    public byte[] State { get; }

    public ReplicationInterest Interest { get; }
}

/// <summary>The host's last published record of one scope member.</summary>
internal sealed class HostMemberRecord
{
    public HostMemberRecord(long revision, byte[] state, ReplicationInterest interest = default)
    {
        Revision = revision;
        State = state;
        Interest = interest;
    }

    public long Revision { get; set; }

    /// <summary>Last published canonical state, or null while none has been published.</summary>
    public byte[] State { get; set; }
    public ReplicationInterest Interest { get; }
}

/// <summary>Where one subscriber stands in a scope's stream.</summary>
internal sealed class SubscriberCursor
{
    public readonly Dictionary<ObjectKey, HostMemberRecord> DeltaStates = new();

    // A filtered scope is digested against exactly the identities and revisions delivered here.
    public Dictionary<ObjectKey, HostMemberRecord> VisibleMembers;
    public int InterestStarId;
    public long RequestId;

    /// <summary>Last log position delivered to this subscriber.</summary>
    public long LastLogSequence;

    /// <summary>Next per-subscriber, per-scope stream sequence.</summary>
    public long NextStreamSequence = 1;

    /// <summary>False while the subscriber has left this scope; the cursor is kept for continuity.</summary>
    public bool Active = true;

    /// <summary>
    /// Set when the subscriber fell behind the retained log. It recovers only through a new
    /// baseline: the next <c>Subscribe</c> replaces this cursor with a fresh subscription.
    /// </summary>
    public bool NeedsResync;

    /// <summary>
    /// True for a digest-only observation: the subscriber receives scope digests as a
    /// summary, no baseline and no stream events, and contributes nothing to log reclamation
    /// because it holds no log position.
    /// </summary>
    public bool DigestOnly;

    /// <summary>
    /// Subscription epoch the host minted for this subscription. Every stream packet
    /// of this subscription carries it, which is what lets a replica refuse the tail of a
    /// superseded subscription after a stream restart.
    /// </summary>
    public long SubscriptionEpoch;

    /// <summary>Baseline this subscriber's stream is an increment of.</summary>
    public long BaselineId;

    /// <summary>
    /// Baseline awaiting the subscriber's ack. Non-zero while the subscriber is in the
    /// Snapshotting phase and must not receive stream packets.
    /// </summary>
    public long PendingBaselineId;

    /// <summary>
    /// Log position the subscriber's baseline covers: the first position whose events are carried
    /// by the increments that follow the baseline install, and the point host log reclamation may
    /// advance to once this subscriber no longer needs anything older.
    /// </summary>
    public long StartLogSequence;

}

/// <summary>
/// The host's replication state of one subscribed scope: the member table, the bounded event log
/// and one cursor per subscriber.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle and state events share one log so their relative order is the order subscribers see;
/// the log position, not the stream sequence, is what cursors follow. Each subscriber's stream
/// sequence is assigned at delivery time, per subscriber and per scope, so one subscriber's interest
/// filter cannot manufacture false gaps in another's stream.
/// </para>
/// <para>
/// The log is bounded. Evicting the oldest events is what makes a lagging subscriber's gap visible
/// at the next delivery instead of letting memory grow without bound; a subscriber behind the
/// retained window is marked <see cref="SubscriberCursor.NeedsResync"/> and receives nothing.
/// </para>
/// <para>
/// Not thread-safe by design: subscriptions and capture run at the quiescent frame boundary, never on a socket
/// thread.
/// </para>
/// </remarks>
internal sealed class ScopeReplicationState
{
    private readonly Queue<ScopeEvent> log;
    private readonly Dictionary<ushort, SubscriberCursor> subscribers = [];
    private readonly int logCapacity;

    public ScopeReplicationState(ScopeKey scope, AuthorityEpoch epoch, int logCapacity)
    {
        if (!scope.IsValid) throw new ArgumentException("Invalid scope: " + scope, nameof(scope));
        if (!epoch.IsValid) throw new ArgumentException("ScopeReplicationState needs a valid epoch.", nameof(epoch));
        if (logCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(logCapacity));
        Scope = scope;
        this.logCapacity = logCapacity;
        log = new Queue<ScopeEvent>(logCapacity);
    }

    public ScopeKey Scope { get; }

    /// <summary>The member table: the last published record of every live member.</summary>
    public Dictionary<ObjectKey, HostMemberRecord> Members { get; } = new();

    /// <summary>Log position of the first retained event; events before it were evicted.</summary>
    public long FirstLogSequence { get; private set; } = 1;

    /// <summary>Log position the next appended event gets.</summary>
    public long NextLogSequence { get; private set; } = 1;

    public int LogCount => log.Count;

    /// <summary>Appends one event and evicts the oldest when the log outgrows its capacity.</summary>
    public void Append(in ScopeEvent @event)
    {
        if (@event.LogSequence != NextLogSequence)
        {
            throw new InvalidOperationException(
                "Scope log positions must be sequential: expected " + NextLogSequence + ", got " + @event.LogSequence);
        }
        log.Enqueue(@event);
        NextLogSequence++;
        while (log.Count > logCapacity)
        {
            log.Dequeue();
            FirstLogSequence++;
        }
    }

    /// <summary>Adds a subscriber whose stream starts after the given log position.</summary>
    /// <remarks>A digest-only subscriber receives no stream; the flag keeps its deliveries empty.</remarks>
    public void AddSubscriber(ushort subscriberId, long startAfterLogSequence, bool digestOnly = false)
    {
        subscribers[subscriberId] = new SubscriberCursor
        {
            LastLogSequence = startAfterLogSequence,
            DigestOnly = digestOnly
        };
    }

    /// <summary>Returns the subscriber's cursor, or false when it never subscribed here.</summary>
    public bool TryGetSubscriber(ushort subscriberId, out SubscriberCursor cursor) =>
        subscribers.TryGetValue(subscriberId, out cursor);

    /// <summary>Removes one subscriber. Used when the player disconnects.</summary>
    public bool RemoveSubscriber(ushort subscriberId) => subscribers.Remove(subscriberId);

    /// <summary>Enumerates the subscribers currently receiving this scope.</summary>
    public IEnumerable<KeyValuePair<ushort, SubscriberCursor>> Subscribers => subscribers;

    /// <summary>Takes the events one active subscriber has not received yet, in log order.</summary>
    /// <remarks>A subscriber behind the retained window gets <c>NeedsResync</c> and an empty list, because a
    /// partial event history cannot reconstruct the world.</remarks>
    public bool TryTakePending(SubscriberCursor cursor, List<ScopeEvent> pending)
    {
        pending.Clear();
        if (cursor.DigestOnly || cursor.NeedsResync || !cursor.Active) return false;
        if (cursor.LastLogSequence < FirstLogSequence - 1)
        {
            // The events this subscriber still needs were evicted. The gap is real; no packet
            // sequence can paper over it.
            cursor.NeedsResync = true;
            cursor.DeltaStates.Clear();
            return false;
        }
        foreach (var @event in log)
        {
            if (@event.LogSequence <= cursor.LastLogSequence) continue;
            pending.Add(@event);
        }
        return pending.Count > 0;
    }

    /// <summary>
    /// Drops every event before <paramref name="firstRetainedLogSequence"/> and returns how many
    /// were dropped.
    /// </summary>
    /// <remarks>
    /// The caller decides what is safe to reclaim: events are only trimmed once every active
    /// subscriber either received them or is covered by a baseline whose cutoff is at or after
    /// them. This is the deterministic form of "确认此前流截止点后才能回收" — the baseline cutoff,
    /// never a timer.
    /// </remarks>
    public int TrimEventsBefore(long firstRetainedLogSequence)
    {
        var trimmed = 0;
        while (log.Count > 0 && log.Peek().LogSequence < firstRetainedLogSequence)
        {
            log.Dequeue();
            FirstLogSequence++;
            trimmed++;
        }
        return trimmed;
    }
}
