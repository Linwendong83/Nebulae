using System;
using System.Collections.Generic;

namespace NebulaeModel.Authority;

/// <summary>
/// Identifies one client command: <c>AuthorityEpoch + ConnectionEpoch + CommandSequence</c>.
/// </summary>
/// <remarks>
/// The key deliberately contains no tick and no wall clock. Commands are ordered by the host's
/// receive batch and a stable per-connection sequence, so a client cannot reorder host work by
/// adjusting a timestamp it controls.
/// </remarks>
internal readonly struct CommandKey : IEquatable<CommandKey>
{
    private readonly AuthorityEpoch epoch;
    private readonly ConnectionEpoch connection;
    private readonly long sequence;

    public CommandKey(AuthorityEpoch epoch, ConnectionEpoch connection, long sequence)
    {
        this.epoch = epoch;
        this.connection = connection;
        this.sequence = sequence;
    }

    public AuthorityEpoch Epoch => epoch;

    public ConnectionEpoch Connection => connection;

    /// <summary>Per-connection command number, assigned by the sending client and strictly increasing.</summary>
    public long Sequence => sequence;

    public bool IsValid => epoch.IsValid && connection.IsValid && sequence > 0;

    public bool Equals(CommandKey other) =>
        epoch.Equals(other.epoch) && connection.Equals(other.connection) && sequence == other.sequence;

    public override bool Equals(object obj) => obj is CommandKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = epoch.GetHashCode();
            hash = (hash * 397) ^ connection.GetHashCode();
            hash = (hash * 397) ^ sequence.GetHashCode();
            return hash;
        }
    }

    public static bool operator ==(CommandKey left, CommandKey right) => left.Equals(right);

    public static bool operator !=(CommandKey left, CommandKey right) => !left.Equals(right);

    public override string ToString() =>
        "epoch=" + epoch + "|conn=" + connection + "|seq=" + sequence;
}

/// <summary>What the host should do with a command that just arrived.</summary>
internal enum CommandAdmission : byte
{
    /// <summary>First time this key is seen. Execute exactly once, then call <see cref="CommandWindow.Complete"/>.</summary>
    Execute = 0,

    /// <summary>Admitted but not yet completed. Do not start a second execution.</summary>
    Pending = 1,

    /// <summary>Already completed. Re-send the cached outcome; do not execute again.</summary>
    Duplicate = 2,

    /// <summary>
    /// Sequence is at or below the retained high-water mark but its result is no longer cached.
    /// Reject explicitly. Treating it as new would execute a command twice after window eviction.
    /// </summary>
    TooOld = 3,

    /// <summary>Key belongs to a different authority epoch or connection epoch. Reject.</summary>
    WrongEpoch = 4,

    /// <summary>Malformed key. Reject.</summary>
    Invalid = 5
}

/// <summary>The host's answer to one command, cached so a retry returns the same answer.</summary>
internal readonly struct CommandOutcome
{
    public CommandOutcome(CommandResultCode code, long appliedHostTick = 0, long transactionId = 0,
        long resourceRevision = 0, byte[] resultData = null)
    {
        Code = code;
        AppliedHostTick = appliedHostTick;
        TransactionId = transactionId;
        ResourceRevision = resourceRevision;
        ResultData = resultData == null ? System.Array.Empty<byte>() : (byte[])resultData.Clone();
    }

    public CommandResultCode Code { get; }

    /// <summary>Host tick the command took effect on. Zero when the command was rejected.</summary>
    public long AppliedHostTick { get; }

    /// <summary>Groups the lifecycle effects this command caused.</summary>
    public long TransactionId { get; }

    /// <summary>Revision of the resource ledger after the command, for the client's optimistic overlay.</summary>
    public long ResourceRevision { get; }
    public byte[] ResultData { get; }

    public bool Accepted => Code == CommandResultCode.Applied;

    public override string ToString() =>
        "code=" + Code + "|tick=" + AppliedHostTick + "|tx=" + TransactionId + "|rev=" + ResourceRevision;
}

/// <summary>Result codes a host can return for a command.</summary>
internal enum CommandResultCode : byte
{
    Applied = 0,
    RejectedInvalid = 1,
    RejectedStale = 2,
    RejectedUnauthorized = 3,
    RejectedNotReady = 4,
    RejectedResource = 5,
    RejectedTarget = 6,
    RejectedDuplicate = 7
}

/// <summary>
/// The host's per-connection command dedup window.
/// </summary>
/// <remarks>
/// <para>
/// Guarantees, in the order the implementation enforces them:
/// </para>
/// <list type="number">
/// <item>A command key is executed at most once, even if the client retries it many times.</item>
/// <item>A retry of a completed command returns the cached outcome, so a client that lost the
/// reply sees the same answer as one that did not.</item>
/// <item>Once a result leaves the bounded window, its sequence stays at or below the high-water
/// mark, so a late retry is rejected as <see cref="CommandAdmission.TooOld"/> instead of being
/// executed a second time.</item>
/// <item>Keys from another authority or connection epoch are rejected, so a reconnect cannot
/// replay a command whose sequence number this window still remembers.</item>
/// </list>
/// <para>
/// The window is per connection by construction: <see cref="AuthorityEpoch"/> and
/// <see cref="ConnectionEpoch"/> are fixed at construction. A host that accepts a reconnect builds
/// a new window, which is what makes the replay space fresh.
/// </para>
/// </remarks>
internal sealed class CommandWindow
{
    private readonly Dictionary<long, CommandOutcome> results;
    private readonly HashSet<long> pending = [];
    private readonly Queue<long> resultOrder = new();

    public CommandWindow(AuthorityEpoch epoch, ConnectionEpoch connection, int resultCapacity = 1024)
    {
        if (!epoch.IsValid) throw new ArgumentException("CommandWindow needs a valid authority epoch.", nameof(epoch));
        if (!connection.IsValid) throw new ArgumentException("CommandWindow needs a valid connection epoch.", nameof(connection));
        if (resultCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(resultCapacity));
        Epoch = epoch;
        Connection = connection;
        ResultCapacity = resultCapacity;
        results = new Dictionary<long, CommandOutcome>(resultCapacity);
    }

    public AuthorityEpoch Epoch { get; }

    public ConnectionEpoch Connection { get; }

    /// <summary>Maximum number of cached outcomes before the oldest are evicted.</summary>
    public int ResultCapacity { get; }

    /// <summary>
    /// Highest sequence this window has admitted. Retained after eviction, which is the only reason
    /// an evicted retry cannot be mistaken for a new command.
    /// </summary>
    public long HighWaterSequence { get; private set; }

    /// <summary>Number of outcomes currently cached.</summary>
    public int CachedResultCount => results.Count;

    /// <summary>Number of commands admitted but not yet completed.</summary>
    public int PendingCount => pending.Count;

    /// <summary>
    /// Decides what to do with an arriving command. Call this on the frame boundary, never from the
    /// socket callback.
    /// </summary>
    /// <param name="key">Identity of the command as sent by the client.</param>
    /// <param name="cachedOutcome">
    /// The previously produced outcome when the result is <see cref="CommandAdmission.Duplicate"/>;
    /// otherwise the default value.
    /// </param>
    public CommandAdmission Admit(CommandKey key, out CommandOutcome cachedOutcome)
    {
        cachedOutcome = default;
        if (!key.IsValid) return CommandAdmission.Invalid;
        if (!key.Epoch.Equals(Epoch) || !key.Connection.Equals(Connection)) return CommandAdmission.WrongEpoch;

        // A cached result is checked before the high-water mark so a retry still gets its original
        // answer rather than a bare "too old".
        if (results.TryGetValue(key.Sequence, out var previous))
        {
            cachedOutcome = previous;
            return CommandAdmission.Duplicate;
        }
        if (pending.Contains(key.Sequence)) return CommandAdmission.Pending;
        if (key.Sequence <= HighWaterSequence) return CommandAdmission.TooOld;

        pending.Add(key.Sequence);
        HighWaterSequence = key.Sequence;
        return CommandAdmission.Execute;
    }

    /// <summary>
    /// Records the outcome of a command admitted with <see cref="CommandAdmission.Execute"/> and
    /// makes it the answer for every future retry.
    /// </summary>
    public bool Complete(CommandKey key, in CommandOutcome outcome)
    {
        if (!key.IsValid || !key.Epoch.Equals(Epoch) || !key.Connection.Equals(Connection)) return false;
        if (key.Sequence > HighWaterSequence) return false;
        pending.Remove(key.Sequence);
        if (results.ContainsKey(key.Sequence)) return false;

        results[key.Sequence] = outcome;
        resultOrder.Enqueue(key.Sequence);
        while (resultOrder.Count > ResultCapacity)
        {
            // Eviction only drops the cached answer. The sequence remains at or below
            // HighWaterSequence, so a later retry is rejected as TooOld rather than re-executed.
            results.Remove(resultOrder.Dequeue());
        }
        return true;
    }

    /// <summary>
    /// Gives up an admitted-but-unexecuted command so the client may retry it.
    /// </summary>
    /// <remarks>
    /// Only valid when nothing was committed to the world. A command that partially executed must
    /// be completed with a rejected outcome instead, because allowing the retry would run the
    /// committed part twice.
    /// </remarks>
    public bool Abandon(CommandKey key)
    {
        if (!key.IsValid || !key.Epoch.Equals(Epoch) || !key.Connection.Equals(Connection)) return false;
        if (!pending.Remove(key.Sequence)) return false;
        if (key.Sequence == HighWaterSequence)
        {
            // Reopen the high-water mark only if no later sequence was admitted; otherwise the
            // mark must stay where the newest admitted command put it.
            HighWaterSequence = key.Sequence - 1;
            foreach (var sequence in pending)
            {
                if (sequence > HighWaterSequence) HighWaterSequence = sequence;
            }
            foreach (var sequence in results.Keys)
            {
                if (sequence > HighWaterSequence) HighWaterSequence = sequence;
            }
        }
        return true;
    }

    /// <summary>True when this window has already answered <paramref name="key"/>.</summary>
    public bool TryGetOutcome(CommandKey key, out CommandOutcome outcome)
    {
        outcome = default;
        if (!key.IsValid || !key.Epoch.Equals(Epoch) || !key.Connection.Equals(Connection)) return false;
        return results.TryGetValue(key.Sequence, out outcome);
    }

    /// <summary>Drops all cached state. Used when the connection or session ends.</summary>
    public void Clear()
    {
        results.Clear();
        pending.Clear();
        resultOrder.Clear();
        HighWaterSequence = 0;
    }
}
