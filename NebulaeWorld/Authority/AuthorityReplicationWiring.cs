#region

using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;

#endregion

namespace NebulaeWorld.Authority;

/// <summary>
/// Drives the host replicator from the frame capture hook.
/// </summary>
/// <remarks>
/// The frame patch calls <see cref="IAuthorityFrameCapture.Capture"/> at the quiescent frame boundary; this
/// adapter forwards it to the replicator's canonical scan. It exists so the
/// replicator itself can stay a pure-model type in NebulaeModel, free of the session types.
/// </remarks>
internal sealed class HostReplicationCapture : IAuthorityFrameCapture
{
    private readonly HostWorldReplicator replicator;

    public HostReplicationCapture(HostWorldReplicator replicator)
    {
        this.replicator = replicator ?? throw new System.ArgumentNullException(nameof(replicator));
    }

    public void Capture(long hostTick) => replicator.CaptureFrame(hostTick);
}

/// <summary>
/// The replica-message applier: decodes queued envelopes and writes the mirror — but only
/// inside the apply window the session opened for exactly that message's scope.
/// </summary>
/// <remarks>
/// <para>
/// The session
/// drains its inbox at the frame boundary and opens an <see cref="ReplicaApplyContext"/> scope per
/// message; before this applier writes anything it re-checks <see cref="ReplicaApplyContext.Allows"/>,
/// so a mirror write is only possible inside a validated, scope-bound apply.
/// </para>
/// <para>
/// The scope named by the message is also re-derived from the packet and required to match, so a
/// packet that claims planet 101 cannot be applied under an open window for planet 102.
/// </para>
/// </remarks>
internal sealed class AuthorityReplicaApplier : IReplicaMessageApplier
{
    private readonly AuthoritySessionState identity;
    private readonly ReplicaApplyContext applyContext;
    private readonly ClientWorldReplica replica;

    public AuthorityReplicaApplier(AuthoritySessionState identity, ReplicaApplyContext applyContext,
        ClientWorldReplica replica)
    {
        this.identity = identity ?? throw new System.ArgumentNullException(nameof(identity));
        this.applyContext = applyContext ?? throw new System.ArgumentNullException(nameof(applyContext));
        this.replica = replica ?? throw new System.ArgumentNullException(nameof(replica));
    }

    public bool Apply(in PendingReplicaMessage message)
    {
        if (message.Packet == null) return false;
        if (!applyContext.Allows(message.Scope))
        {
            // Not inside the window opened for this scope: refuse rather than write. The session's
            // drain always opens the window, so reaching this is a wiring defect, not a race.
            return false;
        }

        return message.Packet switch
        {
            AuthorityLifecyclePacket lifecycle => ApplyLifecycle(lifecycle, message.Scope),
            AuthorityWorldStatePacket worldState => ApplyWorldState(worldState, message.Scope),
            AuthoritySnapshotBeginPacket begin => ApplySnapshotBegin(begin, message.Scope),
            AuthoritySnapshotChunkPacket chunk => ApplySnapshotChunk(chunk, message.Scope),
            AuthoritySnapshotCommitPacket commit => ApplySnapshotCommit(commit, message.Scope),
            AuthorityScopeDigestPacket digest => ApplyScopeDigest(digest, message.Scope),
            AuthorityPlayerCombatStatePacket combat => Multiplayer.Session.CombatAuthority.ApplyState(combat),
            _ => false
        };
    }

    private bool ApplyScopeDigest(AuthorityScopeDigestPacket packet, in ApplyScope messageScope)
    {
        if (!packet.TryGetScopeKey(out var scope) || !scope.Equals(messageScope.Scope))
        {
            return false;
        }
        // The digest is verified inside the same apply window as the stream it checks, so a
        // mismatch suspends the scope before any later queued message can write it.
        return replica.ReceiveScopeDigest(scope, packet.SubscriptionEpoch, packet.HostTick,
            packet.DeclaredStreamSequence, packet.BaselineId, packet.MemberCount, packet.Digest);
    }

    private bool ApplyLifecycle(AuthorityLifecyclePacket packet, in ApplyScope messageScope)
    {
        if (!packet.TryGetScopeKey(out var scope) || !scope.Equals(messageScope.Scope)) return false;
        if (!AuthorityLifecycleCodec.TryDecode(packet.Data, 0, packet.Data.Length, packet.RecordCount,
                identity.Context, out var records, out _))
        {
            return false;
        }
        return replica.ApplyLifecycle(scope, packet.SubscriptionEpoch, packet.Sequence, records);
    }

    private bool ApplyWorldState(AuthorityWorldStatePacket packet, in ApplyScope messageScope)
    {
        if (!packet.TryGetScopeKey(out var scope) || !scope.Equals(messageScope.Scope)) return false;
        if (!AuthorityWorldStateCodec.TryDecode(packet.Data, 0, packet.Data.Length, packet.RecordCount,
                identity.Context, out var records, out _))
        {
            return false;
        }
        return replica.ApplyWorldState(scope, packet.SubscriptionEpoch, packet.Sequence,
            packet.DeclaredBaselineId, records);
    }

    private bool ApplySnapshotBegin(AuthoritySnapshotBeginPacket packet, in ApplyScope messageScope)
    {
        if (!packet.TryGetScopeKey(out var scope) || !scope.Equals(messageScope.Scope)) return false;
        return replica.BeginSnapshot(scope, packet.SubscriptionEpoch, packet.BaselineId, packet.ChunkCount,
            packet.TotalBytes, packet.SnapshotHash, packet.RequestId);
    }

    private bool ApplySnapshotChunk(AuthoritySnapshotChunkPacket packet, in ApplyScope messageScope)
    {
        if (!packet.TryGetScopeKey(out var scope) || !scope.Equals(messageScope.Scope)) return false;
        return replica.ReceiveSnapshotChunk(scope, packet.SubscriptionEpoch, packet.BaselineId,
            packet.ChunkIndex, packet.Data);
    }

    private bool ApplySnapshotCommit(AuthoritySnapshotCommitPacket packet, in ApplyScope messageScope)
    {
        if (!packet.TryGetScopeKey(out var scope) || !scope.Equals(messageScope.Scope)) return false;
        return replica.CommitSnapshot(scope, packet.SubscriptionEpoch, packet.BaselineId, packet.ChunkCount,
            packet.SnapshotHash);
    }
}
