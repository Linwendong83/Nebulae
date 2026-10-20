using System;

namespace NebulaeModel.Authority;

/// <summary>Which DTO family an envelope carries.</summary>
/// <remarks>
/// The families are separate packet types on the wire; this enum is the discriminator the gate uses
/// to decide direction and size limits without knowing the concrete type.
/// </remarks>
internal enum AuthorityFamily : byte
{
    None = 0,

    /// <summary>Host to client: epoch and host tick.</summary>
    Welcome = 1,

    /// <summary>Client to host: one intent with a dedup key.</summary>
    Command = 2,

    /// <summary>Host to client: the cached answer to one command.</summary>
    CommandResult = 3,

    /// <summary>Host to client: a new baseline for one scope starts.</summary>
    SnapshotBegin = 4,

    /// <summary>Host to client: one bounded chunk of a baseline.</summary>
    SnapshotChunk = 5,

    /// <summary>Host to client: the baseline is complete and its hash is known.</summary>
    SnapshotCommit = 6,

    /// <summary>Client to host: the baseline was applied up to a sequence.</summary>
    SnapshotAck = 7,

    /// <summary>Host to client: absolute state for known objects in a scope.</summary>
    WorldState = 8,

    /// <summary>
    /// Host to client: membership changes of one scope.
    /// </summary>
    /// <remarks>
    /// Lifecycle is separate from <see cref="WorldState"/> because the two mean different
    /// things on the replica: a lifecycle record establishes identity or publishes death, while a
    /// state record may only be applied to an identity that already exists ("新对象先有身份再有
    /// HP"). Mixing them into one batch would let a half-applied batch decide its own meaning.
    /// </remarks>
    Lifecycle = 9,

    /// <summary>
    /// Client to host: subscribe, unsubscribe or resync one scope.
    /// </summary>
    ScopeControl = 10,

    /// <summary>
    /// Host to client: the canonical digest of one scope at one stream position.
    /// </summary>
    ScopeDigest = 11,
    PlayerCombatState = 12
}

/// <summary>What one <see cref="AuthorityFamily.ScopeControl"/> message asks for.</summary>
internal enum ScopeControlOp : byte
{
    None = 0,

    /// <summary>Begin (or upgrade/downgrade) a subscription of one scope.</summary>
    Subscribe = 1,

    /// <summary>Leave one scope. The host keeps simulating it regardless.</summary>
    Unsubscribe = 2,

    /// <summary>The scope's stream is broken; the host must re-baseline it.</summary>
    Resync = 3
}

/// <summary>Which way an envelope is allowed to travel.</summary>
internal enum AuthorityDirection : byte
{
    ClientToServer = 0,
    ServerToClient = 1
}

/// <summary>Why an authority message was refused. Every rejection is one of these, never a silent drop.</summary>
internal enum AuthorityRejectCode : byte
{
    None = 0,

    /// <summary>The session handshake has not completed; packets cannot be applied yet.</summary>
    SessionNotReady = 1,

    /// <summary>The family byte is not a known family.</summary>
    UnknownFamily = 4,

    /// <summary>The message traveled the wrong way, for example a command arriving from the host.</summary>
    WrongDirection = 5,

    /// <summary>The message names a different authority epoch than the loaded world.</summary>
    EpochMismatch = 6,

    /// <summary>The message claims a player identity that does not match its connection.</summary>
    ForgedPlayer = 7,

    /// <summary>The scope in the message is not legal for its pool kind.</summary>
    IllegalScope = 8,

    /// <summary>A declared length is negative.</summary>
    NegativeLength = 9,

    /// <summary>A declared payload exceeds the limit for its family.</summary>
    PayloadTooLarge = 10,

    /// <summary>A snapshot chunk exceeds the chunk limit.</summary>
    FragmentTooLarge = 11,

    /// <summary>The envelope is truncated or internally inconsistent.</summary>
    MalformedEnvelope = 12,

    /// <summary>An object key is malformed or names a different world than the envelope.</summary>
    InvalidObjectKey = 13,

    /// <summary>A command key is malformed.</summary>
    InvalidCommandKey = 14
}

/// <summary>One rejection: a code plus a short, loggable explanation.</summary>
/// <remarks>
/// The detail is for diagnostics. Callers branch on <see cref="Code"/> only, so wording changes can
/// never change behaviour.
/// </remarks>
internal readonly struct AuthorityReject
{
    public AuthorityReject(AuthorityRejectCode code, string detail = null)
    {
        Code = code;
        Detail = detail;
    }

    public AuthorityRejectCode Code { get; }

    public string Detail { get; }

    public bool IsRejected => Code != AuthorityRejectCode.None;

    public static AuthorityReject Accepted => new(AuthorityRejectCode.None);

    public override string ToString() =>
        Detail == null ? Code.ToString() : Code + ": " + Detail;
}

/// <summary>
/// The size and count ceilings of the authority protocol.
/// </summary>
/// <remarks>
/// Limits live in one place so the gate, the codec and
/// the tests all agree; a deployment that needs larger values must change them deliberately rather
/// than silently raising a bound at one call site.
/// </remarks>
internal static class AuthorityLimits
{
    /// <summary>Maximum size of one snapshot chunk payload, 64 KiB.</summary>
    public const int ChunkMaxBytes = 64 * 1024;

    /// <summary>Maximum size of one command payload, 16 KiB.</summary>
    public const int CommandPayloadMaxBytes = 16 * 1024;

    /// <summary>Maximum decompressed size of one scope snapshot, 64 MiB.</summary>
    public const long ScopeSnapshotMaxBytes = 64L * 1024 * 1024;

    /// <summary>Maximum pending data per connection, 128 MiB.</summary>
    public const long ConnectionPendingMaxBytes = 128L * 1024 * 1024;

    /// <summary>Maximum number of queued commands per connection.</summary>
    public const int CommandQueueMax = 1024;

    /// <summary>
    /// Maximum scopes one subscriber may hold at once. A subscribe past it is refused rather than
    /// silently growing delivery cost; a subscriber that needs more scopes is a design question,
    /// not a per-call-site decision.
    /// </summary>
    public const int ScopesPerSubscriberMax = 16;

    /// <summary>Digest statements buffered per scope while the stream catches up to them.</summary>
    public const int PendingDigestsPerScopeMax = 4;

    /// <summary>Maximum size of a fixed-size world-state record in a <see cref="AuthorityFamily.WorldState"/> batch.</summary>
    public const int StateRecordMaxBytes = 1024;

    /// <summary>Maximum number of state records in one <see cref="AuthorityFamily.WorldState"/> batch.</summary>
    public const int StateRecordCountMax = 4096;

    /// <summary>Maximum number of lifecycle records in one <see cref="AuthorityFamily.Lifecycle"/> batch.</summary>
    public const int LifecycleRecordCountMax = 4096;

    /// <summary>
    /// Bytes of one compact lifecycle record: operation, native id, generation and revision.
    /// </summary>
    public const int LifecycleRecordBytes = 1 + CompactObjectKeyBytes + 8;

    public const int CompactObjectKeyBytes = 4 + 8;
    public const int RecordBatchHeaderMaxBytes = 4 + AuthorityEpoch.SizeBytes + 1 + 4;

    /// <summary>
    /// Bytes of the fixed envelope header: family, epoch, connection epoch, sequence, host
    /// tick, claimed player id, payload length.
    /// </summary>
    public const int HeaderBytes =
        1 + AuthorityEpoch.SizeBytes + 8 + 8 + 8 + 2 + 4;

    /// <summary>Bytes of one wire-encoded <see cref="ObjectKey"/>.</summary>
    public const int ObjectKeyBytes = AuthorityEpoch.SizeBytes + 1 + 4 + 4 + 8;

    /// <summary>
    /// Payload ceiling for a family. Zero means the family carries no variable payload.
    /// </summary>
    public static int PayloadMaxBytes(AuthorityFamily family)
    {
        switch (family)
        {
            case AuthorityFamily.Command:
                return CommandPayloadMaxBytes;
            case AuthorityFamily.PlayerCombatState:
                return 65536;
            case AuthorityFamily.SnapshotChunk:
                return ChunkMaxBytes;
            case AuthorityFamily.CommandResult:
                return CommandPayloadMaxBytes;
            case AuthorityFamily.SnapshotCommit:
                return 128;
            case AuthorityFamily.SnapshotAck:
                return 64;
            case AuthorityFamily.Welcome:
                return 64;
            case AuthorityFamily.SnapshotBegin:
                return 64;
            case AuthorityFamily.WorldState:
                // One record is a key, a revision and a bounded state blob; the batch ceiling is the
                // per-record ceiling times the record count, plus the small per-record framing.
                return RecordBatchHeaderMaxBytes + StateRecordCountMax *
                       (CompactObjectKeyBytes + 8 + 1 + 8 + 4 + StateRecordMaxBytes);
            case AuthorityFamily.Lifecycle:
                // One record is a fixed-size operation, key and revision; the ceiling is the record
                // count times that fixed size, plus the small per-record framing.
                return RecordBatchHeaderMaxBytes + LifecycleRecordCountMax * LifecycleRecordBytes;
            case AuthorityFamily.ScopeControl:
            case AuthorityFamily.ScopeDigest:
                // Both carry fixed scalar fields and no variable payload; the ceiling exists so a
                // declared length can never grow into an allocation.
                return 32;
            default:
                return 0;
        }
    }
}

/// <summary>Which direction each family may travel.</summary>
internal static class AuthorityFamilyDirection
{
    /// <summary>Resolves the only legal direction for a family.</summary>
    public static bool TryGetDirection(AuthorityFamily family, out AuthorityDirection direction)
    {
        switch (family)
        {
            case AuthorityFamily.Command:
            case AuthorityFamily.SnapshotAck:
            case AuthorityFamily.ScopeControl:
                direction = AuthorityDirection.ClientToServer;
                return true;
            case AuthorityFamily.Welcome:
            case AuthorityFamily.CommandResult:
            case AuthorityFamily.SnapshotBegin:
            case AuthorityFamily.SnapshotChunk:
            case AuthorityFamily.SnapshotCommit:
            case AuthorityFamily.WorldState:
            case AuthorityFamily.Lifecycle:
            case AuthorityFamily.ScopeDigest:
            case AuthorityFamily.PlayerCombatState:
                direction = AuthorityDirection.ServerToClient;
                return true;
            default:
                direction = default;
                return false;
        }
    }

    /// <summary>True when a message of this family may arrive from <paramref name="direction"/>.</summary>
    public static bool IsAllowed(AuthorityFamily family, AuthorityDirection direction) =>
        TryGetDirection(family, out var expected) && expected == direction;
}

/// <summary>The immutable session identity used to validate an incoming message.</summary>
internal readonly struct AuthoritySessionContext
{
    public AuthoritySessionContext(bool handshakeComplete, AuthorityEpoch epoch,
        ConnectionEpoch connection, ushort connectedPlayerId, bool isHost)
    {
        IsHandshakeComplete = handshakeComplete;
        Epoch = epoch;
        Connection = connection;
        ConnectedPlayerId = connectedPlayerId;
        IsHost = isHost;
    }

    /// <summary>True once the mod version handshake has completed.</summary>
    public bool IsHandshakeComplete { get; }

    public AuthorityEpoch Epoch { get; }
    public ConnectionEpoch Connection { get; }

    /// <summary>Transport-assigned player identity; a message cannot claim a different seat.</summary>
    public ushort ConnectedPlayerId { get; }

    public bool IsHost { get; }

    /// <summary>No completed handshake or world identity yet.</summary>
    public static AuthoritySessionContext Uninitialized => default;
}
