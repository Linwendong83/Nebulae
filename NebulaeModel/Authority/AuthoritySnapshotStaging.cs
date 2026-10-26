#region

using System;
using System.Collections.Generic;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Logger;

#endregion

namespace NebulaeModel.Authority;

/// <summary>
/// The client's staging area for one incoming baseline.
/// </summary>
/// <remarks>
/// <para>
/// Chunks accumulate here until Commit proves the reassembly complete and hash-identical. Nothing
/// in the staging area touches the active world: the mirror and the version state only change when
/// the replica installs a snapshot that passed every check. A staging buffer is discarded on any
/// verification failure — the recovery is a new baseline, never "half of the old one plus guesses".
/// </para>
/// <para>
/// Not thread-safe by design: staging messages are applied at the frame boundary like every other
/// replica message, never on the socket thread.
/// </para>
/// </remarks>
internal sealed class ClientSnapshotStaging
{
    private sealed class ScopeStaging
    {
        public long SubscriptionEpoch;
        public long BaselineId;
        public byte[] Buffer;
        public long TotalBytes;
        public int ChunkCount;
        public ulong Hash;
        public readonly HashSet<int> PlacedChunks = new();
    }

    private readonly Dictionary<ScopeKey, ScopeStaging> stagings = new();

    public int Count => stagings.Count;
    internal int ProgressFor(ScopeKey scope) => stagings.TryGetValue(scope, out var staging) ? staging.PlacedChunks.Count : 0;
    private bool Refuse(string reason, ScopeKey scope, long subscriptionEpoch, long baselineId, string detail)
    {
        RefusedTotal++;
        Diagnostics.Warn("snapshot:staging:" + scope + ":" + reason,
            () => $"[authority] snapshot staging refused reason={reason} scope={scope} subscription={subscriptionEpoch} baseline={baselineId} {detail}");
        return false;
    }

    public long BeginsTotal { get; private set; }
    public long ChunksTotal { get; private set; }
    public long DuplicateChunksTotal { get; private set; }
    public long RefusedTotal { get; private set; }
    public long CommittedTotal { get; private set; }

    /// <summary>Drops a scope's staging without installing. Used on unsubscribe and world reset.</summary>
    public bool Abandon(ScopeKey scope) => stagings.Remove(scope);

    /// <summary>Counts a refusal the replica detected outside the staging calls themselves.</summary>
    internal void CountRefused() => RefusedTotal++;

    /// <summary>True when a baseline is currently being staged for this scope.</summary>
    public bool IsStaging(ScopeKey scope) => stagings.ContainsKey(scope);

    public bool IsStaging(ScopeKey scope, long subscriptionEpoch, long baselineId) =>
        stagings.TryGetValue(scope, out var staging) && staging.SubscriptionEpoch == subscriptionEpoch &&
        staging.BaselineId == baselineId;

    /// <summary>
    /// Opens a staging buffer from a validated Begin. Refuses incoherent bookkeeping before any
    /// allocation beyond the buffer itself.
    /// </summary>
    public bool Begin(ScopeKey scope, long subscriptionEpoch, long baselineId, int chunkCount, long totalBytes,
        ulong hash)
    {
        if (baselineId <= 0 || subscriptionEpoch <= 0 || totalBytes <= 0 ||
            totalBytes > AuthorityLimits.ScopeSnapshotMaxBytes ||
            chunkCount != AuthoritySnapshotCodec.ChunkCountFor(totalBytes))
        {
            return Refuse("begin:description", scope, subscriptionEpoch, baselineId, $"chunks={chunkCount} bytes={totalBytes} maxBytes={AuthorityLimits.ScopeSnapshotMaxBytes}");
        }

        if (stagings.TryGetValue(scope, out var existing))
        {
            if (existing.BaselineId == baselineId && existing.SubscriptionEpoch == subscriptionEpoch &&
                existing.TotalBytes == totalBytes && existing.ChunkCount == chunkCount && existing.Hash == hash)
            {
                // A repeated Begin for the baseline already staging is idempotent, not an error.
                return true;
            }
            // A newer host subscription replaces the failed conversation atomically. Old or
            // conflicting duplicates must leave the current staging buffer intact.
            if (subscriptionEpoch <= existing.SubscriptionEpoch)
                return Refuse("begin:overlap", scope, subscriptionEpoch, baselineId, $"activeBaseline={existing.BaselineId} activeSubscription={existing.SubscriptionEpoch}");
        }

        stagings[scope] = new ScopeStaging
        {
            SubscriptionEpoch = subscriptionEpoch,
            BaselineId = baselineId,
            TotalBytes = totalBytes,
            ChunkCount = chunkCount,
            Hash = hash,
            Buffer = new byte[totalBytes]
        };
        BeginsTotal++;
        return true;
    }

    /// <summary>Places one chunk. A repeated identical chunk is a benign transport duplicate.</summary>
    public bool Chunk(ScopeKey scope, long subscriptionEpoch, long baselineId, int chunkIndex, byte[] data)
    {
        if (!stagings.TryGetValue(scope, out var staging))
        {
            return Refuse("chunk:no-staging", scope, subscriptionEpoch, baselineId, $"index={chunkIndex} bytes={data?.Length}");
        }
        if (staging.SubscriptionEpoch != subscriptionEpoch || staging.BaselineId != baselineId ||
            data == null || chunkIndex < 0 || chunkIndex >= staging.ChunkCount ||
            data.Length > AuthorityLimits.ChunkMaxBytes)
        {
            return Refuse("chunk:identity-or-shape", scope, subscriptionEpoch, baselineId, $"index={chunkIndex}/{staging.ChunkCount} bytes={data?.Length} activeBaseline={staging.BaselineId} activeSubscription={staging.SubscriptionEpoch}");
        }

        var offset = (long)chunkIndex * AuthorityLimits.ChunkMaxBytes;
        var expected = (int)Math.Min(AuthorityLimits.ChunkMaxBytes, staging.TotalBytes - offset);
        if (data.Length != expected)
        {
            // Every chunk except the last is exactly the chunk size; a different length means the
            // reassembly would be a different image than the one Begin declared.
            return Refuse("chunk:length", scope, subscriptionEpoch, baselineId, $"index={chunkIndex} expectedBytes={expected} actualBytes={data.Length}");
        }

        if (staging.PlacedChunks.Contains(chunkIndex))
        {
            for (var i = 0; i < data.Length; i++)
            {
                if (staging.Buffer[offset + i] != data[i])
                {
                    // Same position, different bytes: the senders disagree about the image.
                    return Refuse("chunk:conflicting-duplicate", scope, subscriptionEpoch, baselineId, $"index={chunkIndex} differingOffset={i}");
                }
            }
            DuplicateChunksTotal++;
            return true;
        }

        Buffer.BlockCopy(data, 0, staging.Buffer, (int)offset, data.Length);
        staging.PlacedChunks.Add(chunkIndex);
        ChunksTotal++;
        return true;
    }

    /// <summary>
    /// Verifies Commit against the staged chunks and hands back the reassembled image. The staging
    /// buffer is consumed on success and discarded on a verification failure; a Commit naming a
    /// different subscription or baseline is foreign and leaves the live staging untouched.
    /// </summary>
    public bool Commit(ScopeKey scope, long subscriptionEpoch, long baselineId, int chunkCount, ulong hash,
        out byte[] assembled)
    {
        assembled = null;
        if (!stagings.TryGetValue(scope, out var staging))
        {
            return Refuse("commit:no-staging", scope, subscriptionEpoch, baselineId, $"chunks={chunkCount} hash={hash}");
        }
        if (staging.SubscriptionEpoch != subscriptionEpoch || staging.BaselineId != baselineId)
        {
            // A late commit from a superseded conversation must not destroy the conversation that
            // is actually in flight; it is refused whole.
            return Refuse("commit:identity", scope, subscriptionEpoch, baselineId, $"activeBaseline={staging.BaselineId} activeSubscription={staging.SubscriptionEpoch}");
        }
        stagings.Remove(scope);

        var coherent = staging.ChunkCount == chunkCount &&
                       staging.Hash == hash &&
                       staging.PlacedChunks.Count == staging.ChunkCount;
        if (!coherent)
        {
            return Refuse("commit:bookkeeping", scope, subscriptionEpoch, baselineId, $"receivedChunks={staging.PlacedChunks.Count} expectedChunks={staging.ChunkCount} declaredChunks={chunkCount} expectedHash={staging.Hash} declaredHash={hash}");
        }
        if (AuthoritySnapshotCodec.Hash(staging.Buffer) != staging.Hash)
        {
            // The bookkeeping agreed, but the reassembled bytes are not the image Begin declared
            // (a corrupted chunk survives length checks). The content hash is the last gate; the
            // buffer is discarded and nothing reaches the active world.
            return Refuse("commit:checksum", scope, subscriptionEpoch, baselineId, $"bytes={staging.Buffer.Length} expectedHash={staging.Hash} actualHash={AuthoritySnapshotCodec.Hash(staging.Buffer)}");
        }

        assembled = staging.Buffer;
        CommittedTotal++;
        return true;
    }
}
