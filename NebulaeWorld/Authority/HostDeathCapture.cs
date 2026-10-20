#region

using System;
using System.Collections.Concurrent;
using System.Threading;
using NebulaeModel.Authority;

#endregion

namespace NebulaeWorld.Authority;

internal sealed class HostDeathCapture
{
    /// <summary>
    /// Resolves the authority identity of a dying vanilla object. Called on the thread the vanilla
    /// rule runs on; implementers must be safe for concurrent use.
    /// </summary>
    /// <remarks>
    /// False means "no key source can vouch for this object" — not "the object does not exist".
    /// The capture counts the refusal rather than minting a guess.
    /// </remarks>
    public delegate bool KeyResolver(int originAstroId, int objectType, int objectId, out ObjectKey key);

    /// <summary>Queued deaths before the capture refuses further ones.</summary>
    public const int PendingCapacity = 4096;

    private readonly HostDeathLedger ledger;
    private readonly KeyResolver resolveKey;
    private readonly int pendingCapacity;
    private readonly ConcurrentQueue<ObjectKey> pending = new();
    private int pendingCount;
    private long capturedTotal;
    private long refusedFullTotal;
    private long unresolvedTotal;
    private long refusedTotal;
    private long committedTotal;
    private long repeatTotal;
    private long saturatedTotal;

    /// <param name="ledger">The world's death ledger. One death transaction is opened per key.</param>
    /// <param name="resolveKey">Identity source for a dying vanilla object.</param>
    /// <param name="pendingCapacity">Queue bound; the default is <see cref="PendingCapacity"/>.</param>
    public HostDeathCapture(HostDeathLedger ledger, KeyResolver resolveKey, int pendingCapacity = PendingCapacity)
    {
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        this.resolveKey = resolveKey ?? throw new ArgumentNullException(nameof(resolveKey));
        if (pendingCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(pendingCapacity));
        this.pendingCapacity = pendingCapacity;
    }

    /// <summary>Deaths accepted into the queue.</summary>
    public long CapturedTotal => Interlocked.Read(ref capturedTotal);

    /// <summary>Captures refused because the queue was full. Fail-closed back-pressure.</summary>
    public long RefusedFullTotal => Interlocked.Read(ref refusedFullTotal);

    /// <summary>Captures no key source could vouch for. Never guessed into a key.</summary>
    public long UnresolvedTotal => Interlocked.Read(ref unresolvedTotal);

    /// <summary>Queued keys the ledger refused (invalid, foreign epoch, unclassifiable pool).</summary>
    public long RefusedTotal => Interlocked.Read(ref refusedTotal);

    /// <summary>Death transactions this capture opened (first report of each dead key).</summary>
    public long CommittedTotal => Interlocked.Read(ref committedTotal);

    /// <summary>Reports of an already-dead key — the dedup the ledger exists for.</summary>
    public long RepeatTotal => Interlocked.Read(ref repeatTotal);

    /// <summary>Deaths the ledger refused for capacity. The world is fail-closed from here.</summary>
    public long SaturatedTotal => Interlocked.Read(ref saturatedTotal);

    /// <summary>Deaths waiting for the frame boundary.</summary>
    public int PendingCount => Interlocked.CompareExchange(ref pendingCount, 0, 0);

    /// <summary>
    /// Observes one vanilla death and queues its identity. Called inside the vanilla rule, on any
    /// thread; returns false when the capture was refused (full queue or unresolvable identity).
    /// </summary>
    public bool Capture(int originAstroId, int objectType, int objectId)
    {
        // Resolve now, not at the drain: this is the one instant the pool slot is provably
        // occupied by the object that is dying. The tracker mints or returns the same generation
        // the replication scan uses, so the tombstone lands on the identity clients were given.
        if (!resolveKey(originAstroId, objectType, objectId, out var key))
        {
            Interlocked.Increment(ref unresolvedTotal);
            return false;
        }
        if (Interlocked.Increment(ref pendingCount) > pendingCapacity)
        {
            Interlocked.Decrement(ref pendingCount);
            Interlocked.Increment(ref refusedFullTotal);
            return false;
        }
        Interlocked.Increment(ref capturedTotal);
        pending.Enqueue(key);
        return true;
    }

    /// <summary>
    /// Opens one death transaction per queued key. Runs at the frame boundary, before the
    /// replicator's scan, so the tombstone and the task release precede the frame's published despawn.
    /// </summary>
    public void Drain(long hostTick)
    {
        while (pending.TryDequeue(out var key))
        {
            Interlocked.Decrement(ref pendingCount);
            var receipt = ledger.OpenDeath(key, hostTick, finalRevision: 0);
            if (receipt.Saturated)
            {
                Interlocked.Increment(ref saturatedTotal);
                continue;
            }
            if (receipt.IsFirst)
            {
                Interlocked.Increment(ref committedTotal);
                continue;
            }
            if (receipt.TransactionId != 0)
            {
                // A later report of a key this ledger already buried: the multi-source dedup.
                Interlocked.Increment(ref repeatTotal);
                continue;
            }
            Interlocked.Increment(ref refusedTotal);
        }
    }

    /// <summary>
    /// Drops the queued deaths without committing them. Used when the world ends: a queued key
    /// belongs to the epoch that is going away, and committing it into a new world would be exactly
    /// the cross-epoch leak the epoch gate exists to refuse.
    /// </summary>
    public void Clear()
    {
        while (pending.TryDequeue(out _))
        {
            Interlocked.Decrement(ref pendingCount);
        }
    }
}
