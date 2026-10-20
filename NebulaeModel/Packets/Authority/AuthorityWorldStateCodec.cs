#region

using System.Collections.Generic;
using NebulaeModel.Authority;

#endregion

namespace NebulaeModel.Packets.Authority;

/// <summary>
/// Encoding of the records inside one <see cref="AuthorityWorldStatePacket"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each record is written and read field by field, with the state blob length-prefixed and capped
/// per record. Nothing here reflects over property names, so a renamed field cannot silently change
/// the wire format.
/// </para>
/// <para>
/// The codec works on plain arrays, so the whole batch path is testable without a game process. The
/// packet's own declared payload length already bounds the total; the per-record ceiling is what
/// stops one record from consuming the whole batch budget.
/// </para>
/// </remarks>
internal static class AuthorityWorldStateCodec
{
    public const int MinRecordBytes = AuthorityLimits.CompactObjectKeyBytes + 8 + 1 + 4;
    /// <summary>
    /// Encodes records, refusing a batch that would exceed the batch ceiling.
    /// </summary>
    /// <param name="records">Records to encode.</param>
    /// <param name="data">Encoded blob, or null when the batch is too large.</param>
    public static bool TryEncode(IReadOnlyList<AuthorityWorldStateRecord> records, out byte[] data)
    {
        data = null;
        if (records == null) return false;
        if (records.Count > AuthorityLimits.StateRecordCountMax) return false;

        var capacity = AuthorityBatchCodec.PrefixBytes + (records.Count > 0 ? AuthorityBatchCodec.ScopeBytes : 0);
        foreach (var record in records)
        {
            if (record.State == null || record.State.Length > AuthorityLimits.StateRecordMaxBytes) return false;
            capacity += 12 + 8 + 1 + 4 + (record.IsDelta ? 8 : 0) + record.State.Length;
            if (capacity > AuthorityLimits.PayloadMaxBytes(AuthorityFamily.WorldState)) return false;
        }
        var writer = new AuthorityPayloadWriter(capacity);
        writer.WriteInt(records.Count);
        var shared = records.Count > 0 ? records[0].Key : default;
        if (records.Count > 0) AuthorityBatchCodec.WriteScope(writer, shared);
        foreach (var record in records)
        {
            if (!AuthorityBatchCodec.SameScope(shared, record.Key)) return false;
            AuthorityBatchCodec.WriteKey(writer, record.Key);
            writer.WriteLong(record.Revision);
            writer.WriteByte(record.IsDelta ? (byte)1 : (byte)0);
            if (record.IsDelta)
            {
                if (record.BaseRevision <= 0 || record.BaseRevision >= record.Revision) return false;
                writer.WriteLong(record.BaseRevision);
            }
            if (record.State == null || record.State.Length > AuthorityLimits.StateRecordMaxBytes) return false;
            writer.TryWriteBytes(record.State, AuthorityLimits.StateRecordMaxBytes);
        }

        data = writer.Finish();
        return data.Length <= AuthorityLimits.PayloadMaxBytes(AuthorityFamily.WorldState);
    }

    /// <summary>
    /// Decodes records from a validated payload region.
    /// </summary>
    /// <remarks>
    /// The declared record count must equal the number actually present, so a truncated or padded
    /// batch is refused rather than silently applied in part. Every key is checked against the
    /// session, which is where an illegal scope or a foreign epoch is caught.
    /// </remarks>
    public static bool TryDecode(byte[] source, int offset, int length, int declaredRecordCount,
        in AuthoritySessionContext context, out List<AuthorityWorldStateRecord> records, out AuthorityReject reject)
    {
        records = null;
        reject = AuthorityReject.Accepted;
        if (!AuthorityPayloadReader.TryCreate(source, offset, length, out var reader, out reject)) return false;
        if (!reader.TryReadInt(out var count))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing record count");
            return false;
        }
        if (count < 0 || count > AuthorityLimits.StateRecordCountMax)
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
        if (count > reader.Remaining / MinRecordBytes)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated records");
            return false;
        }
        var result = new List<AuthorityWorldStateRecord>(count);
        for (var i = 0; i < count; i++)
        {
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
            if (!reader.TryReadByte(out var mode) || mode > 1)
            {
                reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "state encoding");
                return false;
            }
            long baseRevision = 0;
            if (mode == 1 && (!reader.TryReadLong(out baseRevision) || baseRevision <= 0 || baseRevision >= revision))
            {
                reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "state base revision");
                return false;
            }
            if (!reader.TryReadInt(out var stateLength) || stateLength < 0 ||
                stateLength > AuthorityLimits.StateRecordMaxBytes)
            {
                reject = new AuthorityReject(AuthorityRejectCode.PayloadTooLarge, "record " + i + " state");
                return false;
            }
            if (!reader.TryReadRaw(stateLength, out var state))
            {
                reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "record " + i + " state");
                return false;
            }
            result.Add(new AuthorityWorldStateRecord(key, revision, state, baseRevision));
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

/// <summary>One object's absolute state inside a world-state batch.</summary>
internal readonly struct AuthorityWorldStateRecord
{
    public AuthorityWorldStateRecord(in ObjectKey key, long revision, byte[] state, long baseRevision = 0)
    {
        Key = key;
        Revision = revision;
        State = state;
        BaseRevision = baseRevision;
    }

    public long BaseRevision { get; }

    public bool IsDelta => BaseRevision != 0;

    public ObjectKey Key { get; }

    public long Revision { get; }

    /// <summary>Opaque canonical state. Its layout is owned by the adapter that produced it.</summary>
    public byte[] State { get; }
}
