#region

using System.Collections.Generic;
using NebulaeModel.Packets.Authority;

#endregion

namespace NebulaeModel.Authority;

/// <summary>One member's entry in a frozen scope snapshot.</summary>
/// <remarks>
/// The record carries the identity, the revision the state was published at and the canonical bytes
/// themselves. A null <see cref="State"/> means the host has no canonical state to publish for this
/// member yet — identity without hitpoints, never a guessed value.
/// </remarks>
internal readonly struct SnapshotMemberRecord
{
    public SnapshotMemberRecord(in ObjectKey key, long revision, byte[] state)
    {
        Key = key;
        Revision = revision;
        State = state;
    }

    public ObjectKey Key { get; }

    public long Revision { get; }

    /// <summary>Canonical state at freeze time, or null when the member has none.</summary>
    public byte[] State { get; }
}

/// <summary>
/// The host's frozen statement about one scope at one point of its log.
/// </summary>
/// <remarks>
/// <para>
/// The snapshot is taken at the quiescent frame boundary and is immutable afterwards:
/// compression, chunking and transmission read it but never change it, so a slow client cannot make
/// the host re-read a world that has moved on. The baseline replaces the subscriber's membership
/// atomically on install; the events after <see cref="CutoffLogSequence"/> are the increments that
/// carry the subscriber from the frozen world to the live one.
/// </para>
/// <para>
/// The subscription epoch is minted by the host and echoed by every packet of this subscription,
/// which is what makes an old stream's tail refusable after a restart.
/// </para>
/// </remarks>
internal sealed class FrozenScopeSnapshot
{
    public FrozenScopeSnapshot(ScopeKey scope, long baselineId, long subscriptionEpoch, long cutoffLogSequence,
        long hostTick, List<SnapshotMemberRecord> members)
    {
        Scope = scope;
        BaselineId = baselineId;
        SubscriptionEpoch = subscriptionEpoch;
        CutoffLogSequence = cutoffLogSequence;
        HostTick = hostTick;
        Members = members ?? new List<SnapshotMemberRecord>();
    }

    public ScopeKey Scope { get; }

    public long BaselineId { get; }

    /// <summary>Subscription epoch this baseline was produced for.</summary>
    public long SubscriptionEpoch { get; }

    /// <summary>
    /// Host log position the baseline covers: every event at or before it is superseded by this
    /// snapshot for its subscriber. The client's own stream cutoff after a baseline restart is
    /// always 0, because the restart discards the old stream entirely.
    /// </summary>
    public long CutoffLogSequence { get; }

    /// <summary>Host tick of the frame the snapshot was frozen on. Informational.</summary>
    public long HostTick { get; }

    public List<SnapshotMemberRecord> Members { get; }
}

/// <summary>
/// The client's seam for returning a snapshot acknowledgement to the host.
/// </summary>
/// <remarks>
/// The replica builds the ack but does not know its transport identity — the claimed player id and
/// connection epoch belong to the network wiring, which fills them before sending. A replica with
/// no sink installed still installs its baseline and counts the unsent ack, so the omission is
/// visible instead of silently stranding the host in Snapshotting.
/// </remarks>
internal interface ISnapshotAckSink
{
    void Send(AuthoritySnapshotAckPacket packet);
}
