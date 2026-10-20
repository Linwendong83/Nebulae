#region

using NebulaeModel.Packets.Authority;

#endregion

namespace NebulaeModel.Authority;

/// <summary>Estimates the wire bytes of an authority envelope from its DTO fields.</summary>
/// <remarks>
/// <para>The estimate combines the fixed envelope header, per-family scalar framing and declared payload
/// length. Batch payload length is the exact encoded blob length.</para>
/// <para><see cref="AuthorityLimits.HeaderBytes"/> includes one byte for the family discriminator even though
/// the concrete packet type carries it, making the estimate conservative by one byte. Serializer framing,
/// including length prefixes and string encoding, is excluded.</para>
/// </remarks>
internal static class AuthorityWireSize
{
    /// <summary>
    /// Bytes of the base envelope's subscription epoch field.
    /// </summary>
    /// <remarks>
    /// Counted once, globally, because it is an inherited field on every family: the families that
    /// re-declare it in their own class list the rest of their scalars here and this one carries the
    /// field, so no family is counted twice.
    /// </remarks>
    public const int SubscriptionEpochBytes = 8;

    /// <summary>Estimated wire bytes of one envelope.</summary>
    public static long Of(AuthorityEnvelopePacket packet) =>
        packet == null ? 0 : ForFamily(packet.Family, packet.DeclaredPayloadLength);

    /// <summary>Estimated wire bytes of a family with the given declared payload length.</summary>
    public static long ForFamily(AuthorityFamily family, int declaredPayloadLength)
    {
        var payload = declaredPayloadLength > 0 ? declaredPayloadLength : 0;
        return AuthorityLimits.HeaderBytes + SubscriptionEpochBytes + FramingBytes(family) + payload;
    }

    /// <summary>
    /// The family's fixed scalar framing, counted field by field from the concrete packet class.
    /// </summary>
    /// <remarks>
    /// An unknown family counts the payload only. That is deliberately not a panic path: the estimate
    /// is a metric, and refusing to measure an unmodelled family would take the session down over
    /// bookkeeping instead of reporting it.
    /// </remarks>
    public static int FramingBytes(AuthorityFamily family)
    {
        switch (family)
        {
            // WelcomeHostTick(8).
            case AuthorityFamily.Welcome:
                return 8;
            // TargetKind(1) + TargetScope(4) + TargetNativeId(4) + TargetGeneration(8)
            // + TargetEpoch(16) + Category(1).
            case AuthorityFamily.Command:
                return 34;
            // ResultCode(1) + AppliedHostTick(8) + TransactionId(8) + ResourceRevision(8) + result-array length(4).
            case AuthorityFamily.CommandResult:
                return 29;
            // ScopeKind(1) + Scope(4) + BaselineId(8) + CutoffSequence(8) + TotalBytes(8)
            // + ChunkCount(4) + SnapshotHash(8) + RequestId(8).
            case AuthorityFamily.SnapshotBegin:
                return 49;
            // ScopeKind(1) + Scope(4) + BaselineId(8) + ChunkIndex(4).
            case AuthorityFamily.SnapshotChunk:
                return 17;
            // ScopeKind(1) + Scope(4) + BaselineId(8) + ChunkCount(4) + SnapshotHash(8).
            case AuthorityFamily.SnapshotCommit:
                return 25;
            // BaselineId(8) + LastAppliedSequence(8) + Accepted(1).
            case AuthorityFamily.SnapshotAck:
                return 17;
            // ScopeKind(1) + Scope(4) + DeclaredBaselineId(8) + RecordCount(4).
            case AuthorityFamily.WorldState:
                return 17;
            // ScopeKind(1) + Scope(4) + RecordCount(4).
            case AuthorityFamily.Lifecycle:
                return 9;
            // Op(1) + ScopeKind(1) + Scope(4) + DigestOnly(1) + Reason(1) + LastAppliedSequence(8) + ObservedStarId(4) + RequestId(8).
            case AuthorityFamily.ScopeControl:
                return 28;
            // ScopeKind(1) + Scope(4) + BaselineId(8) + DeclaredStreamSequence(8) + MemberCount(4)
            // + Digest(8).
            case AuthorityFamily.ScopeDigest:
                return 33;
            case AuthorityFamily.PlayerCombatState:
                return 67; // Settled attack array length(4), Actor(2), connection(8), revisions(24), core debit(8), life(1), recovery/death(16), blob length(4).
            default:
                return 0;
        }
    }
}
