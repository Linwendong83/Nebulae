#region

using System;
using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Logger;

#endregion

namespace NebulaeWorld.Authority;

/// <summary>Receives the host frame clock before command execution.</summary>
internal interface IHostTickAware
{
    long HostTick { set; }
}

/// <summary>
/// One admitted command waiting for the host's frame boundary.
/// </summary>
/// <remarks>
/// The packet is carried unchanged: the socket thread only validates and copies it, so the frame
/// applier sees exactly what the peer sent. Nothing here is a game or Unity type, which is what lets
/// the queue be exercised without a running game.
/// </remarks>
internal readonly struct QueuedHostCommand
{
    public QueuedHostCommand(CommandKey key, AuthorityCommandPacket packet, ushort connectionPlayerId,
        int connectionId, long enqueuedTick)
    {
        Key = key;
        Packet = packet;
        ConnectionPlayerId = connectionPlayerId;
        ConnectionId = connectionId;
        EnqueuedTick = enqueuedTick;
    }

    /// <summary>Dedup identity of the command.</summary>
    public CommandKey Key { get; }

    /// <summary>The validated envelope, including its opaque payload.</summary>
    public AuthorityCommandPacket Packet { get; }

    /// <summary>Player id the transport assigned to the connection that sent it.</summary>
    public ushort ConnectionPlayerId { get; }

    /// <summary>Connection handle, so the result can be routed back to the sender.</summary>
    public int ConnectionId { get; }

    /// <summary>Host tick the command was accepted at, for diagnostics and ordering.</summary>
    public long EnqueuedTick { get; }
}

/// <summary>The host-side executor that receives a drained command.</summary>
/// <remarks>The interface keeps queue deduplication and back-pressure tests independent of game code.</remarks>
internal interface IHostCommandExecutor
{
    /// <summary>
    /// Executes one command that the dedup window has admitted for the first time.
    /// </summary>
    /// <remarks>
    /// Implementations must be idempotent-safe in the sense the design requires: if this throws, the
    /// queue records a rejected outcome rather than retrying, because the command may already have
    /// produced effects.
    /// </remarks>
    CommandOutcome Execute(in QueuedHostCommand command);
}

/// <summary>
/// What happened to one command during a drain, for logging and tests.
/// </summary>
internal enum CommandDrainDisposition : byte
{
    /// <summary>First time seen; the executor ran.</summary>
    Executed = 0,

    /// <summary>Already answered; the cached outcome was re-sent and the executor did not run.</summary>
    ReplayedDuplicate = 1,

    /// <summary>Sequence is below the retained window; refused without executing.</summary>
    RefusedTooOld = 2,

    /// <summary>Belongs to another authority or connection epoch; refused without executing.</summary>
    RefusedWrongEpoch = 3,

    /// <summary>Malformed key; refused without executing.</summary>
    RefusedInvalid = 4,

    /// <summary>The executor threw. The command is closed as rejected and is never retried.</summary>
    Failed = 5
}

/// <summary>
/// The host's bounded, thread-safe inbox of admitted commands.
/// </summary>
/// <remarks>
/// <para>
/// The queue is the boundary between the socket thread and the game rules. A processor that has
/// passed the admission gate calls <see cref="TryEnqueue"/> and returns; it never calls
/// <see cref="CommandWindow.Admit"/> and never touches the world. The frame applier calls
/// <see cref="Drain"/> at the quiescent frame boundary, and only there does a command become work.
/// </para>
/// <para>
/// Back-pressure is explicit: a full queue refuses the new command instead of growing without bound,
/// and the refusal is reported to the caller so it can answer the client with a rejected result. The
/// design forbids unbounded allocation, so "the queue grew" is never an acceptable outcome.
/// </para>
/// <para>
/// At-most-once execution is delegated to the per-connection <see cref="CommandWindow"/>, which is
/// only ever touched from the frame thread. A duplicate therefore re-sends the cached answer, and an
/// evicted retry is refused as <see cref="CommandDrainDisposition.RefusedTooOld"/> rather than being
/// executed a second time.
/// </para>
/// </remarks>
internal sealed class HostCommandQueue
{
    private readonly object gate = new();
    private readonly Queue<QueuedHostCommand> pending = new();
    private readonly Dictionary<ulong, CommandWindow> windows = [];
    private readonly Dictionary<ulong, int> queuedCountByConnection = [];
    private readonly Dictionary<ulong, long> queuedBytesByConnection = [];
    private readonly Dictionary<ulong, ushort> playerByConnection = [];
    private readonly AuthorityEpoch epoch;
    private readonly int capacity;
    private readonly long byteCapacity;
    private int ownerThreadId;
    private bool draining;

    private long refusedTotal;
    private long replayedTotal;

    /// <param name="epoch">World epoch every command must name.</param>
    /// <param name="capacity">Maximum queued commands per connection.</param>
    /// <param name="byteCapacity">Maximum queued payload bytes per connection.</param>
    /// <param name="resultCapacity">Cached outcomes retained per connection.</param>
    public HostCommandQueue(AuthorityEpoch epoch, int capacity = AuthorityLimits.CommandQueueMax,
        long byteCapacity = AuthorityLimits.ConnectionPendingMaxBytes, int resultCapacity = AuthorityLimits.CommandQueueMax)
    {
        if (!epoch.IsValid) throw new ArgumentException("HostCommandQueue needs a valid epoch.", nameof(epoch));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (byteCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(byteCapacity));
        if (resultCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(resultCapacity));
        this.epoch = epoch;
        this.capacity = capacity;
        this.byteCapacity = byteCapacity;
        ResultCapacity = resultCapacity;
    }

    /// <summary>Maximum queued commands per connection.</summary>
    public int Capacity => capacity;

    /// <summary>Cached outcomes retained per connection before the oldest are evicted.</summary>
    public int ResultCapacity { get; }

    /// <summary>Commands waiting to be drained across all connections.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return pending.Count;
            }
        }
    }

    /// <summary>Commands refused because a connection's queue was full.</summary>
    public long RefusedTotal => System.Threading.Interlocked.Read(ref refusedTotal);

    /// <summary>Duplicate commands answered from the cache instead of being executed again.</summary>
    public long ReplayedTotal => System.Threading.Interlocked.Read(ref replayedTotal);

    /// <summary>Number of connections that currently have a dedup window.</summary>
    public int ConnectionCount
    {
        get
        {
            lock (gate)
            {
                return windows.Count;
            }
        }
    }

    /// <summary>
    /// Offers one validated command to the host.
    /// </summary>
    /// <remarks>
    /// Called on the socket thread, so it must not touch the world or the dedup window. A false
    /// return means back-pressure: the caller must answer the client with a rejection and must not
    /// treat the command as accepted.
    /// </remarks>
    /// <param name="key">Command identity, already checked against the session by the gate.</param>
    /// <param name="packet">The envelope, kept verbatim.</param>
    /// <param name="connectionPlayerId">Player id the transport assigned to the sending connection.</param>
    /// <param name="connectionId">Connection handle for routing the result.</param>
    /// <param name="enqueuedTick">Host tick at acceptance, for diagnostics.</param>
    public bool TryEnqueue(CommandKey key, AuthorityCommandPacket packet, ushort connectionPlayerId,
        int connectionId, long enqueuedTick)
    {
        if (packet == null || !key.IsValid || !key.Epoch.Equals(epoch)) return false;

        var payloadBytes = packet.DeclaredPayloadLength;
        if (payloadBytes < 0) payloadBytes = 0;
        var bucket = key.Connection.Value;

        lock (gate)
        {
            queuedCountByConnection.TryGetValue(bucket, out var count);
            queuedBytesByConnection.TryGetValue(bucket, out var bytes);
            // Back-pressure is checked before anything is stored, so a full queue cannot be grown
            // past its ceiling by one more message.
            if (count >= capacity || bytes + payloadBytes > byteCapacity)
            {
                System.Threading.Interlocked.Increment(ref refusedTotal);
                return false;
            }

            queuedCountByConnection[bucket] = count + 1;
            queuedBytesByConnection[bucket] = bytes + payloadBytes;
            playerByConnection[bucket] = connectionPlayerId;
            // The transport reuses one packet instance per type (`SubscribeReusable`), so the queue
            // owns its own copy; otherwise the next command of the same type overwrites the payload
            // before the frame boundary executes it.
            pending.Enqueue(new QueuedHostCommand(key, (AuthorityCommandPacket)packet.CreateOwnedCopy(),
                connectionPlayerId, connectionId, enqueuedTick));
            return true;
        }
    }

    /// <summary>
    /// Executes every queued command on the frame boundary and reports each disposition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only place a command becomes work. It must run on the quiescent frame-boundary thread and
    /// refuses a nested call so an executor that
    /// somehow triggers a second drain cannot commit the same batch twice.
    /// </para>
    /// <para>
    /// The method is idempotent per tick in the sense that matters: draining twice runs nothing the
    /// second time, and a repeated key is answered from the window's cache instead of re-executed.
    /// </para>
    /// </remarks>
    /// <param name="executor">Executor for first-time commands. Null closes them as rejected instead.</param>
    /// <param name="onResult">Called once per drained command with its disposition and outcome.</param>
    /// <returns>Number of commands drained.</returns>
    public int Drain(IHostCommandExecutor executor, Action<QueuedHostCommand, CommandDrainDisposition, CommandOutcome> onResult)
    {
        var threadId = Environment.CurrentManagedThreadId;
        List<QueuedHostCommand> batch;

        lock (gate)
        {
            if (draining)
            {
                throw new InvalidOperationException("HostCommandQueue.Drain is not re-entrant.");
            }
            if (pending.Count == 0) return 0;

            // Only one thread ever drains; remember it so a different thread that tries is a clear
            // error rather than a race on the dedup windows.
            if (ownerThreadId != 0 && ownerThreadId != threadId)
            {
                throw new InvalidOperationException(
                    "HostCommandQueue is drained on thread " + ownerThreadId + ", not " + threadId + ".");
            }
            ownerThreadId = threadId;
            draining = true;

            batch = new List<QueuedHostCommand>(pending.Count);
            while (pending.Count > 0) batch.Add(pending.Dequeue());

            foreach (var command in batch)
            {
                var bucket = command.Key.Connection.Value;
                queuedCountByConnection.TryGetValue(bucket, out var count);
                if (count > 0) queuedCountByConnection[bucket] = count - 1;
                queuedBytesByConnection.TryGetValue(bucket, out var bytes);
                var payloadBytes = command.Packet?.DeclaredPayloadLength ?? 0;
                if (payloadBytes < 0) payloadBytes = 0;
                queuedBytesByConnection[bucket] = Math.Max(0, bytes - payloadBytes);
            }
        }

        try
        {
            foreach (var command in batch)
            {
                Execute(command, executor, onResult);
            }
        }
        finally
        {
            lock (gate)
            {
                draining = false;
            }
        }
        return batch.Count;
    }

    private void Execute(QueuedHostCommand command, IHostCommandExecutor executor,
        Action<QueuedHostCommand, CommandDrainDisposition, CommandOutcome> onResult)
    {
        var window = GetOrCreateWindow(command.Key.Connection);
        var admission = window.Admit(command.Key, out var cached);

        switch (admission)
        {
            case CommandAdmission.Duplicate:
                System.Threading.Interlocked.Increment(ref replayedTotal);
                onResult?.Invoke(command, CommandDrainDisposition.ReplayedDuplicate, cached);
                return;
            case CommandAdmission.TooOld:
                onResult?.Invoke(command, CommandDrainDisposition.RefusedTooOld,
                    new CommandOutcome(CommandResultCode.RejectedStale));
                return;
            case CommandAdmission.WrongEpoch:
            case CommandAdmission.Invalid:
                onResult?.Invoke(command, CommandDrainDisposition.RefusedWrongEpoch,
                    new CommandOutcome(CommandResultCode.RejectedInvalid));
                return;
            case CommandAdmission.Pending:
                // A pending key cannot normally be seen here because the drain completes each command
                // before moving on; refusing is safer than executing something already in flight.
                onResult?.Invoke(command, CommandDrainDisposition.RefusedInvalid,
                    new CommandOutcome(CommandResultCode.RejectedDuplicate));
                return;
        }

        CommandOutcome outcome;
        CommandDrainDisposition disposition;
        if (executor == null)
        {
            Diagnostics.Error("command:no-executor", () => $"[authority] no command executor player={command.ConnectionPlayerId} connection={command.ConnectionId} key={command.Key} category={command.Packet.Category} tick={command.EnqueuedTick}");
            outcome = new CommandOutcome(CommandResultCode.RejectedNotReady);
            disposition = CommandDrainDisposition.Failed;
        }
        else
        {
            try
            {
                outcome = executor.Execute(command);
                disposition = CommandDrainDisposition.Executed;
            }
            catch (Exception error)
            {
                Diagnostics.Error($"command:execute:{command.ConnectionId}:{command.Packet.Category}:{error.GetType().Name}",
                    () => $"[authority] command execution failed player={command.ConnectionPlayerId} connection={command.ConnectionId} key={command.Key} category={command.Packet.Category} target={command.Packet.TargetNativeId} scope={command.Packet.TargetScope} enqueuedTick={command.EnqueuedTick}", error);
                // The command may already have produced effects, so it is closed as rejected and
                // never retried: retrying is how a "at most once" command becomes "twice".
                outcome = new CommandOutcome(CommandResultCode.RejectedInvalid);
                disposition = CommandDrainDisposition.Failed;
            }
        }

        window.Complete(command.Key, outcome);
        if (outcome.Code != CommandResultCode.Applied)
            Log.Debug($"[authority] command result player={command.ConnectionPlayerId} connection={command.ConnectionId} key={command.Key} category={command.Packet.Category} disposition={disposition} result={outcome.Code}");
        onResult?.Invoke(command, disposition, outcome);
    }

    /// <summary>
    /// Returns the dedup window of one connection, creating it on first use.
    /// </summary>
    /// <remarks>
    /// The window is fixed to this queue's epoch and the connection's epoch, so a reconnect with a
    /// fresh connection epoch gets a fresh window and cannot replay an old sequence number.
    /// </remarks>
    private CommandWindow GetOrCreateWindow(ConnectionEpoch connection)
    {
        if (!windows.TryGetValue(connection.Value, out var window))
        {
            window = new CommandWindow(epoch, connection, ResultCapacity);
            windows.Add(connection.Value, window);
        }
        return window;
    }

    /// <summary>True when a result for this key is already cached, for diagnostics.</summary>
    public bool TryGetCachedOutcome(CommandKey key, out CommandOutcome outcome)
    {
        lock (gate)
        {
            if (windows.TryGetValue(key.Connection.Value, out var window))
            {
                return window.TryGetOutcome(key, out outcome);
            }
        }
        outcome = default;
        return false;
    }

    /// <summary>
    /// Drops one connection's window, queued budget and any command still waiting for the frame.
    /// </summary>
    /// <remarks>
    /// Purging the pending commands is the point: a disconnected player's commands must not run at
    /// the next drain, because the design releases a departed client's work immediately instead of
    /// waiting for anything from it. Dropping only the dedup window would leave those commands in
    /// the queue and execute them under a connection that no longer exists.
    /// </remarks>
    public bool ForgetConnection(ConnectionEpoch connection)
    {
        lock (gate)
        {
            var survivors = new Queue<QueuedHostCommand>(pending.Count);
            while (pending.Count > 0)
            {
                var command = pending.Dequeue();
                if (!command.Key.Connection.Equals(connection)) survivors.Enqueue(command);
            }
            while (survivors.Count > 0) pending.Enqueue(survivors.Dequeue());

            queuedCountByConnection.Remove(connection.Value);
            queuedBytesByConnection.Remove(connection.Value);
            playerByConnection.Remove(connection.Value);
            return windows.Remove(connection.Value);
        }
    }

    /// <summary>Drops everything. Used when the world or session ends.</summary>
    public void Clear()
    {
        lock (gate)
        {
            pending.Clear();
            windows.Clear();
            queuedCountByConnection.Clear();
            queuedBytesByConnection.Clear();
            playerByConnection.Clear();
            ownerThreadId = 0;
            draining = false;
        }
    }
}
