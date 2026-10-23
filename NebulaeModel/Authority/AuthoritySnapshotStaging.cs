#region

using System;
using System.Collections.Generic;
using NebulaeModel.Packets.Authority;

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
            RefusedTotal++;
            return false;
        }

        if (stagings.TryGetValue(scope, out var existing))
        {
            if (existing.BaselineId == baselineId)
            {
                // A repeated Begin for the baseline already staging is idempotent, not an error.
                return true;
            }
            // A different baseline while one is pending means the old attempt is dead; the client
            // never merges two baselines, so this Begin is refused rather than half-adopted.
            RefusedTotal++;
            return false;
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
            RefusedTotal++;
            return false;
        }
        if (staging.SubscriptionEpoch != subscriptionEpoch || staging.BaselineId != baselineId ||
            data == null || chunkIndex < 0 || chunkIndex >= staging.ChunkCount ||
            data.Length > AuthorityLimits.ChunkMaxBytes)
        {
            RefusedTotal++;
            return false;
        }

        var offset = (long)chunkIndex * AuthorityLimits.ChunkMaxBytes;
        var expected = (int)Math.Min(AuthorityLimits.ChunkMaxBytes, staging.TotalBytes - offset);
        if (data.Length != expected)
        {
            // Every chunk except the last is exactly the chunk size; a different length means the
            // reassembly would be a different image than the one Begin declared.
            RefusedTotal++;
            return false;
        }

        if (staging.PlacedChunks.Contains(chunkIndex))
        {
            for (var i = 0; i < data.Length; i++)
            {
                if (staging.Buffer[offset + i] != data[i])
                {
                    // Same position, different bytes: the senders disagree about the image.
                    RefusedTotal++;
                    return false;
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
            RefusedTotal++;
            return false;
        }
        if (staging.SubscriptionEpoch != subscriptionEpoch || staging.BaselineId != baselineId)
        {
            // A late commit from a superseded conversation must not destroy the conversation that
            // is actually in flight; it is refused whole.
            RefusedTotal++;
            return false;
        }
        stagings.Remove(scope);

        var coherent = staging.ChunkCount == chunkCount &&
                       staging.Hash == hash &&
                       staging.PlacedChunks.Count == staging.ChunkCount;
        if (!coherent)
        {
            RefusedTotal++;
            return false;
        }
        if (AuthoritySnapshotCodec.Hash(staging.Buffer) != staging.Hash)
        {
            // The bookkeeping agreed, but the reassembled bytes are not the image Begin declared
            // (a corrupted chunk survives length checks). The content hash is the last gate; the
            // buffer is discarded and nothing reaches the active world.
            RefusedTotal++;
            return false;
        }

        assembled = staging.Buffer;
        CommittedTotal++;
        return true;
    }
}
