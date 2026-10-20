#region

using NebulaeModel.Authority;

#endregion

namespace NebulaeModel.Packets.Authority;

/// <summary>
/// Common wire shape of every authority envelope.
/// </summary>
/// <remarks>
/// <para>
/// The fixed header is a set of scalars so the <see cref="AuthorityEnvelopeGate"/> can validate a
/// message without knowing its family payload. Concrete families add their own fields; the family
/// itself is derived from the concrete type rather than carried in a field, which is what keeps a
/// family mismatch a type-system question instead of a runtime enum comparison.
/// </para>
/// <para>
/// The epoch is split into two <c>ulong</c> fields because the network serializer has no 128-bit
/// integer. The split is an encoding detail of this DTO only; the model always sees one
/// <see cref="AuthorityEpoch"/>.
/// </para>
/// </remarks>
internal abstract class AuthorityEnvelopePacket
{
    /// <summary>High half of the authority epoch.</summary>
    [NebulaeModel.Networking.WireOrder(10001)]
    public ulong EpochHigh { get; set; }

    /// <summary>Low half of the authority epoch.</summary>
    [NebulaeModel.Networking.WireOrder(10002)]
    public ulong EpochLow { get; set; }

    /// <summary>Connection epoch the message belongs to, or zero for host-originated streams.</summary>
    [NebulaeModel.Networking.WireOrder(10003)]
    public ulong ConnectionEpoch { get; set; }

    /// <summary>Per-connection stream position.</summary>
    [NebulaeModel.Networking.WireOrder(10004)]
    public long Sequence { get; set; }

    /// <summary>Host rule tick the message was produced on.</summary>
    [NebulaeModel.Networking.WireOrder(10005)]
    public long HostTick { get; set; }

    /// <summary>Player id the sender claims. Checked against the connection, never trusted.</summary>
    [NebulaeModel.Networking.WireOrder(10006)]
    public ushort ClaimedPlayerId { get; set; }

    /// <summary>
    /// Subscription epoch a per-scope stream packet belongs to. The families that
    /// ride the stream set it; the replica refuses any packet whose value is not its subscription's.
    /// Snapshot conversations and scoped streams share this one property. Unscoped packets
    /// such as the welcome leave it at zero.
    /// </summary>
    [NebulaeModel.Networking.WireOrder(10007)]
    public long SubscriptionEpoch { get; set; }

    /// <summary>Family this concrete type carries. Not serialized: it is the type itself.</summary>
    public abstract AuthorityFamily Family { get; }

    /// <summary>Reassembles the header for validation.</summary>
    public AuthorityEnvelopeHeader ToHeader(int payloadLength) =>
        new(Family, new AuthorityEpoch(EpochHigh, EpochLow),
            new ConnectionEpoch(ConnectionEpoch), Sequence, HostTick, ClaimedPlayerId, payloadLength);

    /// <summary>Fills the header fields from a validated header.</summary>
    protected void CopyHeaderFrom(in AuthorityEnvelopeHeader header)
    {
        EpochHigh = header.Epoch.High;
        EpochLow = header.Epoch.Low;
        ConnectionEpoch = header.Connection.Value;
        Sequence = header.Sequence;
        HostTick = header.HostTick;
        ClaimedPlayerId = header.ClaimedPlayerId;
    }

    /// <summary>Number of bytes this message declares as payload, for the gate's size check.</summary>
    public virtual int DeclaredPayloadLength => 0;

    /// <summary>
    /// Returns a message a deferred consumer can own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transport registers every processor through <c>SubscribeReusable</c>, which deserializes
    /// each incoming packet into the <em>same</em> instance per type. A processor that queues the
    /// packet for the frame boundary would then apply whatever the next packet
    /// of that type overwrote it with. Deferred consumers must own a copy so the queued scope and payload
    /// remain stable.
    /// </para>
    /// <para>
    /// The copy is shallow for value fields, which is correct because the reusable deserializer
    /// assigns fresh arrays rather than mutating the existing ones; the four array-carrying
    /// families override <see cref="CloneMutableBuffers"/> to copy their buffer as well, so the
    /// guarantee does not depend on the serializer's allocation behaviour.
    /// </para>
    /// </remarks>
    public AuthorityEnvelopePacket CreateOwnedCopy()
    {
        var copy = (AuthorityEnvelopePacket)MemberwiseClone();
        copy.CloneMutableBuffers();
        return copy;
    }

    /// <summary>Deep-copies this family's mutable buffers onto a freshly cloned message.</summary>
    protected virtual void CloneMutableBuffers()
    {
    }
}

/// <summary>
/// Host to client: the room's authority identity.
/// </summary>
/// <remarks>
/// The welcome is what turns a client from "connected" into "a peer of this authority session": it
/// carries the world epoch and the connection epoch the client must
/// use on its commands.
/// </remarks>
[NebulaeModel.Networking.PacketId(5777059311825509400UL)]
internal class AuthorityWelcomePacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.Welcome;

    /// <summary>Host tick at the moment of the welcome, so a client can anchor its clock.</summary>
    [NebulaeModel.Networking.WireOrder(1)]
    public long WelcomeHostTick { get; set; }

    public static AuthorityWelcomePacket Create(in AuthorityEnvelopeHeader header)
    {
        var packet = new AuthorityWelcomePacket { WelcomeHostTick = header.HostTick };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Client to host: one intent.</summary>
/// <remarks>
/// The command carries its dedup key, its target, and an opaque payload. It never carries
/// a trusted final damage, balance or kill result; those are the host's to compute.
/// </remarks>
[NebulaeModel.Networking.PacketId(9861866888284251590UL)]
internal class AuthorityPlayerCombatStatePacket : AuthorityEnvelopePacket
{
    public void SetHeader(in AuthorityEnvelopeHeader header) => CopyHeaderFrom(header);
    public override AuthorityFamily Family => AuthorityFamily.PlayerCombatState;
    [NebulaeModel.Networking.WireOrder(0)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public long LastCommandSequence { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public long CombatRevision { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public long PersonalRevision { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public ulong ActorConnection { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public double CoreEnergyDebitTotal { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public bool IsAlive { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public int DeathCount { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public int InvincibleTicks { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public int HpRecoverCD { get; set; }
    [NebulaeModel.Networking.WireOrder(10)]
    public int ShieldRecoverCD { get; set; }
    [NebulaeModel.Networking.WireOrder(11)]
    public byte[] CombatData { get; set; }
    [NebulaeModel.Networking.WireOrder(12)]
    public int[] DebitItems { get; set; } = System.Array.Empty<int>();
    [NebulaeModel.Networking.WireOrder(13)]
    public int[] DebitTotals { get; set; } = System.Array.Empty<int>();
    [NebulaeModel.Networking.WireOrder(14)]
    public long[] SettledAttacks { get; set; } = System.Array.Empty<long>();
    public override int DeclaredPayloadLength => (CombatData?.Length ?? 0) + (DebitItems?.Length ?? 0) * 8 + (SettledAttacks?.Length ?? 0) * 8;
    protected override void CloneMutableBuffers()
    {
        if (CombatData != null) CombatData = (byte[])CombatData.Clone();
        if (SettledAttacks != null) SettledAttacks = (long[])SettledAttacks.Clone();
        if (DebitItems != null) DebitItems = (int[])DebitItems.Clone();
        if (DebitTotals != null) DebitTotals = (int[])DebitTotals.Clone();
    }
}

[NebulaeModel.Networking.PacketId(17476840602699489961UL)]
internal class AuthorityCommandPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.Command;

    /// <summary>Target object, or an invalid key when the command has no object target.</summary>
    [NebulaeModel.Networking.WireOrder(0)]
    public byte TargetKind { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int TargetScope { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int TargetNativeId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public long TargetGeneration { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public ulong TargetEpochHigh { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public ulong TargetEpochLow { get; set; }

    /// <summary>Command category, kept a byte so an unknown value is detectable.</summary>
    [NebulaeModel.Networking.WireOrder(6)]
    public byte Category { get; set; }

    /// <summary>Command body. Bounded by <see cref="AuthorityLimits.CommandPayloadMaxBytes"/>.</summary>
    [NebulaeModel.Networking.WireOrder(7)]
    public byte[] Payload { get; set; }

    public override int DeclaredPayloadLength => Payload?.Length ?? 0;

    protected override void CloneMutableBuffers()
    {
        if (Payload != null) Payload = (byte[])Payload.Clone();
    }

    /// <summary>Reassembles the target key, or false when the command targets no object.</summary>
    public bool TryGetTargetKey(out ObjectKey key)
    {
        key = new ObjectKey(new AuthorityEpoch(TargetEpochHigh, TargetEpochLow), (PoolKind)TargetKind,
            TargetScope, TargetNativeId, TargetGeneration);
        return TargetNativeId > 0;
    }

    public static AuthorityCommandPacket Create(in AuthorityEnvelopeHeader header, in ObjectKey target,
        byte category, byte[] payload)
    {
        var packet = new AuthorityCommandPacket
        {
            TargetKind = (byte)target.Kind,
            TargetScope = target.Scope,
            TargetNativeId = target.NativeId,
            TargetGeneration = target.Generation,
            TargetEpochHigh = target.Epoch.High,
            TargetEpochLow = target.Epoch.Low,
            Category = category,
            Payload = payload
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Host to client: the answer to one command.</summary>
[NebulaeModel.Networking.PacketId(9517235733983236580UL)]
internal class AuthorityCommandResultPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.CommandResult;

    /// <summary>Result code from <see cref="CommandResultCode"/>.</summary>
    [NebulaeModel.Networking.WireOrder(0)]
    public byte ResultCode { get; set; }

    /// <summary>Host tick the command took effect on.</summary>
    [NebulaeModel.Networking.WireOrder(1)]
    public long AppliedHostTick { get; set; }

    /// <summary>Groups the lifecycle effects the command caused.</summary>
    [NebulaeModel.Networking.WireOrder(2)]
    public long TransactionId { get; set; }

    /// <summary>Resource ledger revision after the command.</summary>
    [NebulaeModel.Networking.WireOrder(3)]
    public long ResourceRevision { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public byte[] ResultData { get; set; } = System.Array.Empty<byte>();
    public override int DeclaredPayloadLength => ResultData?.Length ?? 0;
    protected override void CloneMutableBuffers()
    {
        if (ResultData != null) ResultData = (byte[])ResultData.Clone();
    }

    public static AuthorityCommandResultPacket Create(in AuthorityEnvelopeHeader header, in CommandOutcome outcome)
    {
        var packet = new AuthorityCommandResultPacket
        {
            ResultCode = (byte)outcome.Code,
            AppliedHostTick = outcome.AppliedHostTick,
            TransactionId = outcome.TransactionId,
            ResourceRevision = outcome.ResourceRevision,
            ResultData = outcome.ResultData == null ? System.Array.Empty<byte>() : (byte[])outcome.ResultData.Clone()
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Host to client: a new baseline for one scope begins.</summary>
[NebulaeModel.Networking.PacketId(15175122332845510561UL)]
internal class AuthoritySnapshotBeginPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.SnapshotBegin;

    [NebulaeModel.Networking.WireOrder(0)]
    public byte ScopeKind { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int Scope { get; set; }

    /// <summary>Identity of this baseline; increments must declare it.</summary>
    [NebulaeModel.Networking.WireOrder(2)]
    public long BaselineId { get; set; }

    /// <summary>Subscription epoch the chunks belong to; a resubscribe invalidates the old one.</summary>

    /// <summary>Stream sequence the baseline covers; the cutoff for tombstone reclamation.</summary>
    [NebulaeModel.Networking.WireOrder(3)]
    public long CutoffSequence { get; set; }

    /// <summary>Total bytes of the snapshot before chunking.</summary>
    [NebulaeModel.Networking.WireOrder(4)]
    public long TotalBytes { get; set; }

    /// <summary>Number of chunks the client should expect.</summary>
    [NebulaeModel.Networking.WireOrder(5)]
    public int ChunkCount { get; set; }

    /// <summary>Hash of the whole snapshot, checked at commit.</summary>
    [NebulaeModel.Networking.WireOrder(6)]
    public ulong SnapshotHash { get; set; }

    /// <summary>Client request this space-enemy baseline answers.</summary>
    [NebulaeModel.Networking.WireOrder(7)]
    public long RequestId { get; set; }

    /// <summary>Reassembles the scope key, or false when the scope is not legal for its kind.</summary>
    public bool TryGetScopeKey(out ScopeKey scopeKey)
    {
        scopeKey = new ScopeKey((PoolKind)ScopeKind, Scope);
        return scopeKey.IsValid;
    }

    public static AuthoritySnapshotBeginPacket Create(in AuthorityEnvelopeHeader header, in ScopeKey scope,
        long baselineId, long subscriptionEpoch, long cutoffSequence, long totalBytes, int chunkCount,
        ulong snapshotHash)
    {
        var packet = new AuthoritySnapshotBeginPacket
        {
            ScopeKind = (byte)scope.Kind,
            Scope = scope.Scope,
            BaselineId = baselineId,
            SubscriptionEpoch = subscriptionEpoch,
            CutoffSequence = cutoffSequence,
            TotalBytes = totalBytes,
            ChunkCount = chunkCount,
            SnapshotHash = snapshotHash
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Host to client: one bounded chunk of a baseline.</summary>
/// <remarks>
/// The chunk is the family whose size the design names explicitly, so its limit is enforced with a
/// dedicated reject code rather than the generic payload ceiling.
/// </remarks>
[NebulaeModel.Networking.PacketId(4787815529695424137UL)]
internal class AuthoritySnapshotChunkPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.SnapshotChunk;

    [NebulaeModel.Networking.WireOrder(0)]
    public byte ScopeKind { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int Scope { get; set; }

    /// <summary>Baseline this chunk belongs to.</summary>
    [NebulaeModel.Networking.WireOrder(2)]
    public long BaselineId { get; set; }

    /// <summary>Subscription epoch the baseline was produced for.</summary>

    /// <summary>Zero-based chunk index.</summary>
    [NebulaeModel.Networking.WireOrder(3)]
    public int ChunkIndex { get; set; }

    /// <summary>Chunk bytes. Bounded by <see cref="AuthorityLimits.ChunkMaxBytes"/>.</summary>
    [NebulaeModel.Networking.WireOrder(4)]
    public byte[] Data { get; set; }

    public override int DeclaredPayloadLength => Data?.Length ?? 0;

    protected override void CloneMutableBuffers()
    {
        if (Data != null) Data = (byte[])Data.Clone();
    }

    /// <summary>Reassembles the scope key, or false when the scope is not legal for its kind.</summary>
    public bool TryGetScopeKey(out ScopeKey scopeKey)
    {
        scopeKey = new ScopeKey((PoolKind)ScopeKind, Scope);
        return scopeKey.IsValid;
    }

    public static AuthoritySnapshotChunkPacket Create(in AuthorityEnvelopeHeader header, in ScopeKey scope,
        long baselineId, long subscriptionEpoch, int chunkIndex, byte[] data)
    {
        var packet = new AuthoritySnapshotChunkPacket
        {
            ScopeKind = (byte)scope.Kind,
            Scope = scope.Scope,
            BaselineId = baselineId,
            SubscriptionEpoch = subscriptionEpoch,
            ChunkIndex = chunkIndex,
            Data = data
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Host to client: the baseline is complete.</summary>
[NebulaeModel.Networking.PacketId(9010929356619418537UL)]
internal class AuthoritySnapshotCommitPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.SnapshotCommit;

    [NebulaeModel.Networking.WireOrder(0)]
    public byte ScopeKind { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int Scope { get; set; }

    [NebulaeModel.Networking.WireOrder(2)]
    public long BaselineId { get; set; }

    /// <summary>Subscription epoch the baseline was produced for.</summary>

    [NebulaeModel.Networking.WireOrder(3)]
    public int ChunkCount { get; set; }

    /// <summary>Hash of the reassembled snapshot. A mismatch discards the staging buffer.</summary>
    [NebulaeModel.Networking.WireOrder(4)]
    public ulong SnapshotHash { get; set; }

    /// <summary>Reassembles the scope key, or false when the scope is not legal for its kind.</summary>
    public bool TryGetScopeKey(out ScopeKey scopeKey)
    {
        scopeKey = new ScopeKey((PoolKind)ScopeKind, Scope);
        return scopeKey.IsValid;
    }

    public static AuthoritySnapshotCommitPacket Create(in AuthorityEnvelopeHeader header, in ScopeKey scope,
        long baselineId, long subscriptionEpoch, int chunkCount, ulong snapshotHash)
    {
        var packet = new AuthoritySnapshotCommitPacket
        {
            ScopeKind = (byte)scope.Kind,
            Scope = scope.Scope,
            BaselineId = baselineId,
            SubscriptionEpoch = subscriptionEpoch,
            ChunkCount = chunkCount,
            SnapshotHash = snapshotHash
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Client to host: a baseline was applied up to a sequence.</summary>
[NebulaeModel.Networking.PacketId(2106625843895811469UL)]
internal class AuthoritySnapshotAckPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.SnapshotAck;

    [NebulaeModel.Networking.WireOrder(0)]
    public long BaselineId { get; set; }

    /// <summary>Highest sequence applied contiguously. The host may reclaim tombstones up to it.</summary>
    [NebulaeModel.Networking.WireOrder(1)]
    public long LastAppliedSequence { get; set; }

    /// <summary>False when the client rejected the baseline and is asking for a new one.</summary>
    [NebulaeModel.Networking.WireOrder(2)]
    public bool Accepted { get; set; }

    public static AuthoritySnapshotAckPacket Create(in AuthorityEnvelopeHeader header, long baselineId,
        long lastAppliedSequence, bool accepted)
    {
        var packet = new AuthoritySnapshotAckPacket
        {
            BaselineId = baselineId,
            LastAppliedSequence = lastAppliedSequence,
            Accepted = accepted
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Host to client: absolute state for known objects of one scope.</summary>
/// <remarks>
/// <para>
/// HP is sent as an absolute value with its maximum, never as a delta for the client to accumulate,
/// and the batch declares the baseline its revisions belong to. A batch whose declared baseline
/// does not match the installed one is refused instead of applied to a different world.
/// </para>
/// <para>
/// The records travel as one opaque blob rather than as parallel arrays, and are written and read
/// with <see cref="AuthorityWorldStateCodec"/>. The blob is bounded twice: by this packet's declared
/// payload length and again per record inside the codec, so a peer cannot make the reader allocate
/// more than the ceiling regardless of what the blob claims.
/// </para>
/// </remarks>
[NebulaeModel.Networking.PacketId(15186985009269377889UL)]
internal class AuthorityWorldStatePacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.WorldState;

    [NebulaeModel.Networking.WireOrder(0)]
    public byte ScopeKind { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int Scope { get; set; }

    /// <summary>Baseline these increments are computed against.</summary>
    [NebulaeModel.Networking.WireOrder(2)]
    public long DeclaredBaselineId { get; set; }

    /// <summary>Number of records the blob contains. Checked against the decoded count.</summary>
    [NebulaeModel.Networking.WireOrder(3)]
    public int RecordCount { get; set; }

    /// <summary>Encoded records, bounded by <see cref="AuthorityLimits.StateRecordMaxBytes"/> each.</summary>
    [NebulaeModel.Networking.WireOrder(4)]
    public byte[] Data { get; set; }

    public override int DeclaredPayloadLength => Data?.Length ?? 0;

    protected override void CloneMutableBuffers()
    {
        if (Data != null) Data = (byte[])Data.Clone();
    }

    /// <summary>
    /// True when the batch's own bookkeeping agrees with its blob.
    /// </summary>
    /// <remarks>
    /// The declared record count must be inside the ceiling and the blob must be present. The
    /// count actually encoded in the blob is checked during decoding, where a mismatch is refused
    /// rather than applied in part.
    /// </remarks>
    public bool AreRecordsConsistent() =>
        Data != null && RecordCount >= 0 && RecordCount <= AuthorityLimits.StateRecordCountMax;

    public bool TryGetScopeKey(out ScopeKey scopeKey)
    {
        scopeKey = new ScopeKey((PoolKind)ScopeKind, Scope);
        return scopeKey.IsValid;
    }

    public static AuthorityWorldStatePacket Create(in AuthorityEnvelopeHeader header, in ScopeKey scope,
        long declaredBaselineId, int recordCount, byte[] data)
    {
        var packet = new AuthorityWorldStatePacket
        {
            ScopeKind = (byte)scope.Kind,
            Scope = scope.Scope,
            DeclaredBaselineId = declaredBaselineId,
            RecordCount = recordCount,
            Data = data
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>
/// Host to client: membership changes of one scope.
/// </summary>
/// <remarks>
/// <para>
/// A spawn establishes identity; a despawn publishes death with its final revision. State records
/// travel in <see cref="AuthorityWorldStatePacket"/> and may only be applied to an identity this
/// family already established, which is what makes "delta before spawn" a refused message instead
/// of a guessed object. The records are encoded by <see cref="AuthorityLifecycleCodec"/>.
/// </para>
/// </remarks>
[NebulaeModel.Networking.PacketId(16723028566674122552UL)]
internal class AuthorityLifecyclePacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.Lifecycle;

    [NebulaeModel.Networking.WireOrder(0)]
    public byte ScopeKind { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int Scope { get; set; }

    /// <summary>Number of records the blob contains. Checked against the decoded count.</summary>
    [NebulaeModel.Networking.WireOrder(2)]
    public int RecordCount { get; set; }

    /// <summary>Encoded records, each exactly <see cref="AuthorityLimits.LifecycleRecordBytes"/>.</summary>
    [NebulaeModel.Networking.WireOrder(3)]
    public byte[] Data { get; set; }

    public override int DeclaredPayloadLength => Data?.Length ?? 0;

    protected override void CloneMutableBuffers()
    {
        if (Data != null) Data = (byte[])Data.Clone();
    }

    /// <summary>True when the batch's own bookkeeping agrees with its blob.</summary>
    public bool AreRecordsConsistent() =>
        Data != null && RecordCount >= 0 && RecordCount <= AuthorityLimits.LifecycleRecordCountMax;

    public bool TryGetScopeKey(out ScopeKey scopeKey)
    {
        scopeKey = new ScopeKey((PoolKind)ScopeKind, Scope);
        return scopeKey.IsValid;
    }

    public static AuthorityLifecyclePacket Create(in AuthorityEnvelopeHeader header, in ScopeKey scope,
        int recordCount, byte[] data)
    {
        var packet = new AuthorityLifecyclePacket
        {
            ScopeKind = (byte)scope.Kind,
            Scope = scope.Scope,
            RecordCount = recordCount,
            Data = data
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Client to host: subscribe, unsubscribe or resync one scope.</summary>
/// <remarks>
/// <para>
/// Subscription is the client asking for delivery, never for simulation: the host runs every scope
/// regardless. A resync is the replica's one recovery path after its stream or digest broke — the
/// host answers with a fresh baseline, and the old stream's tail stays refusable by the new
/// subscription epoch.
/// </para>
/// <para>
/// The player id rides the header as an assertion only; the host resolves identity and scope
/// eligibility from its own registry, so a forged request changes nothing but a log line.
/// </para>
/// </remarks>
[NebulaeModel.Networking.PacketId(4692612182555801457UL)]
internal class AuthorityScopeControlPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.ScopeControl;

    /// <summary>What is being asked, from <see cref="ScopeControlOp"/>.</summary>
    [NebulaeModel.Networking.WireOrder(0)]
    public byte Op { get; set; }

    [NebulaeModel.Networking.WireOrder(1)]
    public byte ScopeKind { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int Scope { get; set; }

    /// <summary>True for a digest-only subscription: summaries without baseline or stream.</summary>
    [NebulaeModel.Networking.WireOrder(3)]
    public bool DigestOnly { get; set; }

    /// <summary>Why a resync was requested, from <see cref="ScopeRecoveryReason"/>; zero otherwise.</summary>
    [NebulaeModel.Networking.WireOrder(4)]
    public byte Reason { get; set; }

    /// <summary>The client's current subscription epoch for the scope, or 0 when it has none.</summary>

    /// <summary>The client's last applied stream sequence, for host diagnostics only.</summary>
    [NebulaeModel.Networking.WireOrder(5)]
    public long LastAppliedSequence { get; set; }

    /// <summary>Client observation used to reject a subscription overtaken by a star change.</summary>
    [NebulaeModel.Networking.WireOrder(6)]
    public int ObservedStarId { get; set; }

    [NebulaeModel.Networking.WireOrder(7)]
    public long RequestId { get; set; }

    /// <summary>Reassembles the scope key, or false when the scope is not legal for its kind.</summary>
    public bool TryGetScopeKey(out ScopeKey scopeKey)
    {
        scopeKey = new ScopeKey((PoolKind)ScopeKind, Scope);
        return scopeKey.IsValid;
    }

    public static AuthorityScopeControlPacket Create(in AuthorityEnvelopeHeader header, ScopeControlOp op,
        in ScopeKey scope, bool digestOnly, ScopeRecoveryReason reason, long subscriptionEpoch,
        long lastAppliedSequence)
    {
        var packet = new AuthorityScopeControlPacket
        {
            Op = (byte)op,
            ScopeKind = (byte)scope.Kind,
            Scope = scope.Scope,
            DigestOnly = digestOnly,
            Reason = (byte)reason,
            SubscriptionEpoch = subscriptionEpoch,
            LastAppliedSequence = lastAppliedSequence
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}

/// <summary>Host to client: the canonical digest of one scope at one stream position.</summary>
/// <remarks>
/// <para>
/// The digest is the periodic integrity check and the observation summary: for a live subscriber it
/// must match the replica's mirrors exactly once the replica has applied
/// <see cref="DeclaredStreamSequence"/> — earlier is a future fact to defer, later is superseded. A
/// mismatch suspends the scope and requests a baseline; it is never papered over.
/// </para>
/// <para>
/// For a digest-only observer the same message *is* the summary: membership count and content hash
/// of a scope the observer deliberately does not hold.
/// </para>
/// </remarks>
[NebulaeModel.Networking.PacketId(481726606536129840UL)]
internal class AuthorityScopeDigestPacket : AuthorityEnvelopePacket
{
    public override AuthorityFamily Family => AuthorityFamily.ScopeDigest;

    [NebulaeModel.Networking.WireOrder(0)]
    public byte ScopeKind { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int Scope { get; set; }

    /// <summary>Subscription epoch the digest was stamped for; a superseded subscription ignores it.</summary>

    /// <summary>Baseline the digested membership belongs to; 0 for a digest-only observation.</summary>
    [NebulaeModel.Networking.WireOrder(2)]
    public long BaselineId { get; set; }

    /// <summary>Stream sequence the digest describes. The replica must have applied exactly it.</summary>
    [NebulaeModel.Networking.WireOrder(3)]
    public long DeclaredStreamSequence { get; set; }

    /// <summary>Number of live members the digest covers.</summary>
    [NebulaeModel.Networking.WireOrder(4)]
    public int MemberCount { get; set; }

    /// <summary>Content hash over member identity, revisions and canonical state.</summary>
    [NebulaeModel.Networking.WireOrder(5)]
    public ulong Digest { get; set; }

    /// <summary>Digest-only observations use epoch/baseline/sequence zero; live streams have an epoch and baseline.</summary>
    public bool HasValidBookkeeping() => DeclaredStreamSequence >= 0 && MemberCount >= 0 &&
        BaselineId >= 0 && (BaselineId == 0
            ? SubscriptionEpoch == 0 && DeclaredStreamSequence == 0
            : SubscriptionEpoch > 0);

    /// <summary>Reassembles the scope key, or false when the scope is not legal for its kind.</summary>
    public bool TryGetScopeKey(out ScopeKey scopeKey)
    {
        scopeKey = new ScopeKey((PoolKind)ScopeKind, Scope);
        return scopeKey.IsValid;
    }

    public static AuthorityScopeDigestPacket Create(in AuthorityEnvelopeHeader header, in ScopeKey scope,
        long subscriptionEpoch, long baselineId, long declaredStreamSequence, int memberCount, ulong digest)
    {
        var packet = new AuthorityScopeDigestPacket
        {
            ScopeKind = (byte)scope.Kind,
            Scope = scope.Scope,
            SubscriptionEpoch = subscriptionEpoch,
            BaselineId = baselineId,
            DeclaredStreamSequence = declaredStreamSequence,
            MemberCount = memberCount,
            Digest = digest
        };
        packet.CopyHeaderFrom(header);
        return packet;
    }
}
