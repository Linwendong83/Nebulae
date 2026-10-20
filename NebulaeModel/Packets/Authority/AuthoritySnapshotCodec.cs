
#region
using System.Collections.Generic;
using NebulaeModel.Authority;

#endregion

namespace NebulaeModel.Packets.Authority;

/// <summary>
/// Encoding of a frozen scope snapshot and its chunking.
/// </summary>
/// <remarks>
/// <para>
/// The snapshot travels as one deterministic byte image that Begin/Chunk/Commit describe: Begin
/// declares the total size, chunk count and content hash, Commit repeats the hash over the
/// reassembled bytes, and the client installs only a reassembly that passes both checks. The image
/// is produced once, at the safe frame boundary, and never re-read from the live world — that is
/// what makes "在安全帧冻结 DTO" hold even though transmission is slower than the simulation.
/// </para>
/// <para>
/// Every field is written and read with the bounded reader/writer, so a truncated or hostile image
/// is refused instead of misparsed. The whole-image ceiling is
/// <see cref="AuthorityLimits.ScopeSnapshotMaxBytes"/>; the per-chunk ceiling is
/// <see cref="AuthorityLimits.ChunkMaxBytes"/>.
/// </para>
/// </remarks>
internal static class AuthoritySnapshotCodec
{
    /// <summary>Four longs and member count; a nonempty image also carries one shared scope.</summary>
    public const int HeaderBytes = 4 * 8 + 4;

    /// <summary>Smallest possible member record: key + revision + state length.</summary>
    public const int MinMemberRecordBytes = AuthorityLimits.CompactObjectKeyBytes + 8 + 4;

    /// <summary>
    /// Content hash of a snapshot image. FNV-1a 64: not cryptographic, but a deterministic check
    /// that sender and receiver hold the same bytes, which is the only claim the design makes.
    /// </summary>
    public static ulong Hash(byte[] data)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        if (data == null) return hash;
        foreach (var b in data)
        {
            hash = (hash ^ b) * prime;
        }
        return hash;
    }

    /// <summary>Number of chunks an image of <paramref name="totalBytes"/> splits into.</summary>
    public static int ChunkCountFor(long totalBytes)
    {
        if (totalBytes <= 0) return 0;
        return (int)((totalBytes + AuthorityLimits.ChunkMaxBytes - 1) / AuthorityLimits.ChunkMaxBytes);
    }

    /// <summary>
    /// Encodes a frozen snapshot. Refuses an image that would exceed the scope snapshot ceiling —
    /// the adapter that produced the members owns keeping them bounded, and a silent truncation
    /// would be a silent divergence.
    /// </summary>
    public static bool TryEncode(FrozenScopeSnapshot snapshot, out byte[] data)
    {
        data = null;
        if (snapshot == null || snapshot.Members == null) return false;
        if (snapshot.BaselineId <= 0 || snapshot.SubscriptionEpoch <= 0 || !snapshot.Scope.IsValid) return false;

        var writer = new AuthorityPayloadWriter();
        writer.WriteLong(snapshot.BaselineId);
        writer.WriteLong(snapshot.HostTick);
        writer.WriteLong(snapshot.CutoffLogSequence);
        writer.WriteLong(snapshot.SubscriptionEpoch);
        writer.WriteInt(snapshot.Members.Count);
        var shared = snapshot.Members.Count > 0 ? snapshot.Members[0].Key : default;
        if (snapshot.Members.Count > 0) AuthorityBatchCodec.WriteScope(writer, shared);
        foreach (var member in snapshot.Members)
        {
            if (!member.Key.IsValid || !AuthorityBatchCodec.SameScope(shared, member.Key) ||
                member.Key.Kind != snapshot.Scope.Kind || member.Key.Scope != snapshot.Scope.Scope) return false;
            if ((long)writer.Length + MinMemberRecordBytes + (member.State?.Length ?? 0) > AuthorityLimits.ScopeSnapshotMaxBytes)
                return false;
            AuthorityBatchCodec.WriteKey(writer, member.Key);
            writer.WriteLong(member.Revision);
            if (member.State == null)
            {
                // -1 marks "identity without state"; an empty array stays a legal empty state.
                writer.WriteInt(-1);
            }
            else
            {
                if (member.State.Length > AuthorityLimits.StateRecordMaxBytes) return false;
                writer.TryWriteBytes(member.State, AuthorityLimits.StateRecordMaxBytes);
            }
        }

        data = writer.ToArray();
        return data.Length <= AuthorityLimits.ScopeSnapshotMaxBytes;
    }

    /// <summary>
    /// Decodes a snapshot image and validates every member against the world epoch and the scope it
    /// claims. A foreign key, a foreign epoch or a trailing byte refuses the whole image.
    /// </summary>
    public static bool TryDecode(byte[] source, AuthorityEpoch epoch, ScopeKey scope,
        out FrozenScopeSnapshot snapshot, out AuthorityReject reject)
    {
        snapshot = null;
        reject = AuthorityReject.Accepted;
        if (!epoch.IsValid || !scope.IsValid)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "invalid epoch or scope");
            return false;
        }
        if (!AuthorityPayloadReader.TryCreate(source, 0, source?.Length ?? 0, out var reader, out reject))
        {
            return false;
        }
        if (reader.Remaining < HeaderBytes)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing snapshot header");
            return false;
        }
        if (!reader.TryReadLong(out var baselineId) || baselineId <= 0)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "baseline");
            return false;
        }
        if (!reader.TryReadLong(out var hostTick)) // informational; not validated
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "host tick");
            return false;
        }
        if (!reader.TryReadLong(out var cutoffLogSequence) || cutoffLogSequence < 0)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "cutoff");
            return false;
        }
        if (!reader.TryReadLong(out var subscriptionEpoch) || subscriptionEpoch <= 0)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "subscription epoch");
            return false;
        }
        if (!reader.TryReadInt(out var memberCount) || memberCount < 0)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "member count");
            return false;
        }

        ObjectKey shared = default;
        if (memberCount > 0 && (!AuthorityBatchCodec.TryReadScope(ref reader, out shared) ||
                !shared.Epoch.Equals(epoch) || shared.Kind != scope.Kind || shared.Scope != scope.Scope))
        {
            reject = new AuthorityReject(AuthorityRejectCode.InvalidObjectKey, "snapshot scope");
            return false;
        }

        // Every member needs at least its fixed floor of bytes; a count that cannot fit the
        // remaining payload is a lie about the image, not a count to allocate for.
        if (memberCount > reader.Remaining / MinMemberRecordBytes)
        {
            reject = new AuthorityReject(AuthorityRejectCode.PayloadTooLarge, "members=" + memberCount);
            return false;
        }

        var members = new List<SnapshotMemberRecord>(memberCount);
        for (var i = 0; i < memberCount; i++)
        {
            if (!AuthorityBatchCodec.TryReadKey(ref reader, shared, out var candidate))
            {
                reject = new AuthorityReject(AuthorityRejectCode.InvalidObjectKey, "member " + i);
                return false;
            }
            if (!candidate.IsValid || !candidate.Epoch.Equals(epoch) ||
                candidate.Kind != scope.Kind || candidate.Scope != scope.Scope)
            {
                reject = new AuthorityReject(AuthorityRejectCode.InvalidObjectKey,
                    "member " + i + " key=" + candidate);
                return false;
            }
            if (!reader.TryReadLong(out var revision) || revision <= 0)
            {
                reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "member " + i + " revision");
                return false;
            }
            if (!reader.TryReadInt(out var stateLength) || stateLength < -1)
            {
                reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "member " + i + " state");
                return false;
            }
            byte[] state = null;
            if (stateLength >= 0)
            {
                if (stateLength > AuthorityLimits.StateRecordMaxBytes)
                {
                    reject = new AuthorityReject(AuthorityRejectCode.PayloadTooLarge, "member " + i + " state");
                    return false;
                }
                if (!reader.TryReadRaw(stateLength, out state))
                {
                    reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "member " + i + " state");
                    return false;
                }
            }
            members.Add(new SnapshotMemberRecord(candidate, revision, state));
        }

        if (!reader.EndOfPayload)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "trailing bytes");
            return false;
        }

        snapshot = new FrozenScopeSnapshot(scope, baselineId, subscriptionEpoch, cutoffLogSequence, hostTick, members);
        return true;
    }
}
