
#region
using NebulaeModel.Authority;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Authority;

#endregion

namespace NebulaeModel.Networking;

/// <summary>The single runtime checkpoint every authority message passes.</summary>
/// <remarks>
/// <para>C2S messages reach the host directly, and S2C messages arrive only from the bound server connection.
/// Validation precedes any broadcast routing. Authority families use separate top-level packet types, so
/// wrapping one in a broadcast router causes a type mismatch.</para>
/// <para>The gate validates admission and records diagnostics without touching game state. Accepted messages
/// enter the frame-boundary queues.</para>
/// </remarks>
internal static class AuthorityEnvelopeGateRuntime
{
    private static long rejectedTotal;

    /// <summary>
    /// Validates a packet's header and family before anything else touches it.
    /// </summary>
    /// <param name="packet">The concrete envelope, used for its family.</param>
    /// <param name="context">What this session believes about itself.</param>
    /// <param name="direction">Which way the message traveled.</param>
    /// <param name="hostTick">Current host tick, for the log line.</param>
    public static AuthorityReject Validate(AuthorityEnvelopePacket packet, in AuthoritySessionContext context,
        AuthorityDirection direction, long hostTick = 0)
    {
        if (packet == null)
        {
            return Count(new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "null packet"), hostTick);
        }

        var header = packet.ToHeader(packet.DeclaredPayloadLength);
        var reject = AuthorityEnvelopeGate.Validate(context, direction, header);

        // A batch whose parallel counts disagree is malformed even when the header is fine, so the
        // family-specific consistency check belongs here rather than in the family's consumer.
        if (!reject.IsRejected)
        {
            reject = packet switch
            {
                AuthorityWorldStatePacket worldState => ValidateWorldState(worldState),
                AuthorityLifecyclePacket lifecycle => ValidateLifecycle(lifecycle),
                AuthoritySnapshotBeginPacket begin => ValidateSnapshotBegin(begin),
                AuthoritySnapshotChunkPacket chunk => ValidateSnapshotChunk(chunk),
                AuthoritySnapshotCommitPacket commit => ValidateSnapshotCommit(commit),
                AuthorityPlayerCombatStatePacket combat => combat.PlayerId == 0 || combat.ActorConnection == 0 || combat.PersonalRevision < 0 || combat.CombatRevision <= 0 ||
                    combat.CombatData == null || combat.CombatData.Length == 0 || double.IsNaN(combat.CoreEnergyDebitTotal) ||
                    double.IsInfinity(combat.CoreEnergyDebitTotal) || combat.CoreEnergyDebitTotal < 0 ||
                    combat.DebitItems == null || combat.DebitTotals == null || combat.DebitItems.Length != combat.DebitTotals.Length || combat.DebitItems.Length > 512
                    ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "player combat state") : AuthorityReject.Accepted,
                _ => AuthorityReject.Accepted
            };
        }

        return Count(reject, hostTick);
    }

    private static AuthorityReject ValidateWorldState(AuthorityWorldStatePacket packet) =>
        !packet.AreRecordsConsistent() ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "world state record count")
        : packet.SubscriptionEpoch <= 0 ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "world state subscription epoch")
        : AuthorityReject.Accepted;

    private static AuthorityReject ValidateLifecycle(AuthorityLifecyclePacket packet) =>
        !packet.AreRecordsConsistent() ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "lifecycle record count")
        : packet.SubscriptionEpoch <= 0 ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "lifecycle subscription epoch")
        : AuthorityReject.Accepted;

    private static AuthorityReject ValidateSnapshotBegin(AuthoritySnapshotBeginPacket packet) =>
        !packet.TryGetScopeKey(out _) ? new AuthorityReject(AuthorityRejectCode.IllegalScope, "snapshot begin scope")
        : packet.BaselineId <= 0 || packet.SubscriptionEpoch <= 0
            ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "snapshot begin identity")
        : packet.ChunkCount < 0 || packet.TotalBytes <= 0 ||
          packet.ChunkCount != AuthoritySnapshotCodec.ChunkCountFor(packet.TotalBytes)
            ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "snapshot begin bookkeeping")
        : AuthorityReject.Accepted;

    private static AuthorityReject ValidateSnapshotChunk(AuthoritySnapshotChunkPacket packet) =>
        !packet.TryGetScopeKey(out _) ? new AuthorityReject(AuthorityRejectCode.IllegalScope, "snapshot chunk scope")
        : packet.BaselineId <= 0 || packet.SubscriptionEpoch <= 0 || packet.ChunkIndex < 0 || packet.Data == null
            ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "snapshot chunk bookkeeping")
        : AuthorityReject.Accepted;

    private static AuthorityReject ValidateSnapshotCommit(AuthoritySnapshotCommitPacket packet) =>
        !packet.TryGetScopeKey(out _) ? new AuthorityReject(AuthorityRejectCode.IllegalScope, "snapshot commit scope")
        : packet.BaselineId <= 0 || packet.SubscriptionEpoch <= 0 || packet.ChunkCount < 0
            ? new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "snapshot commit bookkeeping")
        : AuthorityReject.Accepted;

    /// <summary>
    /// Validates the bootstrap welcome, which is the one message that precedes the world epoch.
    /// </summary>
    /// <remarks>
    /// The bootstrap takes a different path because it is what establishes the epoch, but it must
    /// still be counted like every other rejection: a welcome that a hostile peer spams is exactly
    /// the kind of thing the counters exist to make visible.
    /// </remarks>
    public static AuthorityReject ValidateBootstrap(AuthorityEnvelopePacket packet,
        in AuthoritySessionContext context, AuthorityDirection direction, long hostTick = 0)
    {
        if (packet == null)
        {
            return Count(new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "null packet"), hostTick);
        }
        var reject = AuthorityEnvelopeGate.ValidateBootstrapWelcome(context, direction,
            packet.ToHeader(packet.DeclaredPayloadLength));
        return Count(reject, hostTick);
    }

    /// <summary>Records a rejection and logs it once, so a malformed peer is visible without spam.</summary>
    private static AuthorityReject Count(AuthorityReject reject, long hostTick)
    {
        if (!reject.IsRejected)
        {
            return reject;
        }

        var total = ++rejectedTotal;
        Log.Warn($"[authority] rejected message #{total} at tick {hostTick}: {reject}");
        return reject;
    }
}
