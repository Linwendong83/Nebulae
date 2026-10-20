#region

using System.Collections.Generic;
using NebulaeModel.Authority;

#endregion

namespace NebulaeModel.Packets.Authority;

/// <summary>Which membership change one lifecycle record publishes.</summary>
internal enum AuthorityLifecycleOp : byte
{
    /// <summary>Not a lifecycle operation. A record with this value is refused.</summary>
    None = 0,

    /// <summary>The object exists. Its identity is established; state may follow.</summary>
    Spawn = 1,

    /// <summary>
    /// The object of this exact key left the host's pool without a recorded death: a unit that
    /// returned to its formation, a relay that landed. The revision is its final one.
    /// </summary>
    Despawn = 2,

    /// <summary>
    /// The object of this exact key is dead — the host's death ledger holds its tombstone, the same
    /// transaction <c>CombatStat.HandleZeroHp</c> settled through the Kill path. The revision is its
    /// final one. A client may present the vanilla death effect for this one; a plain
    /// <see cref="Despawn"/> must never produce one.
    /// </summary>
    Death = 3
}

/// <summary>One membership change inside an <see cref="AuthorityLifecyclePacket"/>.</summary>
internal readonly struct AuthorityLifecycleRecord
{
    public AuthorityLifecycleRecord(AuthorityLifecycleOp op, in ObjectKey key, long revision)
    {
        Op = op;
        Key = key;
        Revision = revision;
    }

    public AuthorityLifecycleOp Op { get; }

    public ObjectKey Key { get; }

    /// <summary>Revision the change was published at. For a despawn this is the final revision.</summary>
    public long Revision { get; }
}

/// <summary>
/// Encoding of the records inside one <see cref="AuthorityLifecyclePacket"/>.
/// </summary>
/// <remarks>
/// <para>
/// The batch shares its epoch and scope once; each record carries its operation, native id,
/// generation and revision. Every record is exactly
/// <see cref="AuthorityLimits.LifecycleRecordBytes"/> long, so a batch's size is a linear function
/// of its count and neither side can be surprised by the other's framing.
/// </para>
/// <para>
/// Like the world-state codec, every key is validated against the session while decoding, so an
/// illegal scope or a foreign epoch is refused before a replica can see the record.
/// </para>
/// </remarks>
internal static class AuthorityLifecycleCodec
{
    /// <summary>
    /// Encodes records, refusing a batch that would exceed the batch ceiling.
    /// </summary>
    public static bool TryEncode(IReadOnlyList<AuthorityLifecycleRecord> records, out byte[] data)
    {
        data = null;
        if (records == null) return false;
        if (records.Count > AuthorityLimits.LifecycleRecordCountMax) return false;

        var writer = new AuthorityPayloadWriter();
        writer.WriteInt(records.Count);
        var shared = records.Count > 0 ? records[0].Key : default;
        if (records.Count > 0) AuthorityBatchCodec.WriteScope(writer, shared);
        foreach (var record in records)
        {
            if (record.Op == AuthorityLifecycleOp.None) return false;
            if (!AuthorityBatchCodec.SameScope(shared, record.Key)) return false;
            writer.WriteByte((byte)record.Op);
            AuthorityBatchCodec.WriteKey(writer, record.Key);
            writer.WriteLong(record.Revision);
        }

        data = writer.ToArray();
        return data.Length <= AuthorityLimits.PayloadMaxBytes(AuthorityFamily.Lifecycle);
    }

    /// <summary>
    /// The operations this codec round-trips. An unknown value is a framing disagreement, not a
    /// record to skip: the batch is refused whole.
    /// </summary>
    private static bool IsKnownOp(AuthorityLifecycleOp op) =>
        op is AuthorityLifecycleOp.Spawn or AuthorityLifecycleOp.Despawn or AuthorityLifecycleOp.Death;

    /// <summary>
    /// Decodes records from a validated payload region.
    /// </summary>
    /// <remarks>
    /// The declared record count must equal the number actually present and every record must fit
    /// exactly, so a truncated or padded batch is refused rather than applied in part.
    /// </remarks>
    public static bool TryDecode(byte[] source, int offset, int length, int declaredRecordCount,
        in AuthoritySessionContext context, out List<AuthorityLifecycleRecord> records, out AuthorityReject reject)
    {
        records = null;
        reject = AuthorityReject.Accepted;
        if (!AuthorityPayloadReader.TryCreate(source, offset, length, out var reader, out reject)) return false;
        if (!reader.TryReadInt(out var count))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing record count");
            return false;
        }
        if (count < 0 || count > AuthorityLimits.LifecycleRecordCountMax)
        {
            reject = new AuthorityReject(AuthorityRejectCode.PayloadTooLarge, "records=" + count);
            return false;
        }
        if (count != declaredRecordCount)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope,
                "declared=" + declaredRecordCount + " encoded=" + count);
            return false;
        }

        ObjectKey shared = default;
        if (count > 0 && !AuthorityBatchCodec.TryReadScope(ref reader, out shared))
        {
            reject = new AuthorityReject(AuthorityRejectCode.InvalidObjectKey, "batch scope");
            return false;
        }
        if (count > reader.Remaining / AuthorityLimits.LifecycleRecordBytes)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated records");
            return false;
        }
        var result = new List<AuthorityLifecycleRecord>(count);
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadEnumByte(out AuthorityLifecycleOp op) || !IsKnownOp(op))
            {
                reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "record " + i + " op");
                return false;
            }
            if (!AuthorityBatchCodec.TryReadKey(ref reader, shared, out var key))
            {
                reject = new AuthorityReject(AuthorityRejectCode.InvalidObjectKey, "record " + i);
                return false;
            }
            reject = AuthorityEnvelopeGate.ValidateObjectKey(context, key);
            if (reject.IsRejected) return false;
            if (!reader.TryReadLong(out var revision) || revision <= 0)
            {
                reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "record " + i + " revision");
                return false;
            }
            result.Add(new AuthorityLifecycleRecord(op, key, revision));
        }

        if (!reader.EndOfPayload)
        {
            // Trailing bytes mean the sender and receiver disagree about the record layout.
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "trailing bytes");
            return false;
        }

        records = result;
        return true;
    }
}
