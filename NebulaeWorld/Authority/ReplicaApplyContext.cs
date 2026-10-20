#region

using System;
using System.Threading;
using NebulaeModel.Authority;

#endregion

namespace NebulaeWorld.Authority;

/// <summary>
/// What one replica apply is allowed to do, and to which part of the world.
/// </summary>
/// <remarks>
/// The scope is carried so a guard can tell a legitimate apply from a stray write: an adapter may
/// only touch the scope, transaction and stream position named here. It is a value so it
/// cannot be mutated while an apply is running.
/// </remarks>
internal readonly struct ApplyScope
{
    public ApplyScope(ScopeKey scope, long transactionId = 0, long streamSequence = 0, long hostTick = 0)
    {
        Scope = scope;
        TransactionId = transactionId;
        StreamSequence = streamSequence;
        HostTick = hostTick;
    }

    /// <summary>Scope being applied, or an invalid key when the apply is not scope-bound.</summary>
    public ScopeKey Scope { get; }

    /// <summary>Host transaction this apply belongs to, or 0.</summary>
    public long TransactionId { get; }

    /// <summary>Stream position of the message being applied, or 0.</summary>
    public long StreamSequence { get; }

    /// <summary>Host tick the applied state was produced on, or 0.</summary>
    public long HostTick { get; }

    public override string ToString() =>
        "scope=" + (Scope.IsValid ? Scope.ToString() : "-") + "|tx=" + TransactionId +
        "|seq=" + StreamSequence + "|tick=" + HostTick;
}

/// <summary>
/// The lease returned by <see cref="ReplicaApplyContext.Enter"/>. Disposing it ends the apply.
/// </summary>
/// <remarks>
/// The lease is the only way out of an apply, so an exception inside the guarded region still runs
/// the exit through <c>using</c>. That is what keeps a failed apply from leaving the session stuck
/// in "replica writes allowed".
/// </remarks>
internal readonly struct ReplicaApplyLease : IDisposable
{
    private readonly ReplicaApplyContext owner;

    internal ReplicaApplyLease(ReplicaApplyContext owner)
    {
        this.owner = owner;
    }

    public void Dispose() => owner?.Exit();
}

/// <summary>
/// The only window in which a client replica may write mirrored world state.
/// </summary>
/// <remarks>
/// <para>
/// The point of the type is a single, narrow, auditable answer to "is this write allowed right now".
/// Packet processors and the socket thread are never inside an apply, so a message cannot turn into
/// a world mutation by accident; only the frame-boundary applier opens a scope, and it closes it
/// again even when the adapter throws.
/// </para>
/// <para>
/// The context is single-threaded by construction. It records the thread that opened the outermost
/// apply and refuses to open a nested one from any other thread, which is the executable form of
/// "socket 线程不能写世界": a second thread that tries to write a replica while the apply thread is
/// running is a bug that fails loudly instead of racing.
/// </para>
/// <para>
/// Nesting is counted so an adapter may call another adapter without closing the outer scope early.
/// Each exit restores the scope that was active before its matching enter, so the innermost apply
/// never leaks its scope to the code after it.
/// </para>
/// </remarks>
internal sealed class ReplicaApplyContext
{
    private readonly object gate = new();
    private ApplyScope[] scopeStack = new ApplyScope[4];
    private int depth;
    private int ownerThreadId;
    private long appliedTotal;

    /// <summary>True while a replica apply is in progress on this session.</summary>
    public bool IsActive
    {
        get
        {
            lock (gate)
            {
                return depth > 0;
            }
        }
    }

    /// <summary>True only for the thread that owns the active apply lease.</summary>
    public bool IsActiveOnCurrentThread
    {
        get
        {
            lock (gate)
                return depth > 0 && ownerThreadId == Environment.CurrentManagedThreadId;
        }
    }

    /// <summary>Number of applies entered. Diagnostics only; a growing count is expected.</summary>
    public long AppliedTotal => Interlocked.Read(ref appliedTotal);

    /// <summary>The scope of the innermost active apply, or a default scope when idle.</summary>
    public ApplyScope Current
    {
        get
        {
            lock (gate)
            {
                return depth > 0 ? scopeStack[depth - 1] : default;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="scope"/> is the scope the current apply is allowed to write.
    /// </summary>
    /// <remarks>
    /// A guard calls this before touching mirrored state. An idle context allows nothing, so a write
    /// outside an apply is refused rather than silently accepted because "the session is in authority
    /// mode".
    /// </remarks>
    public bool Allows(in ApplyScope scope)
    {
        lock (gate)
        {
            if (depth == 0 || ownerThreadId != Environment.CurrentManagedThreadId) return false;
            var active = scopeStack[depth - 1];
            if (!active.Scope.IsValid || !scope.Scope.IsValid) return false;
            return active.Scope.Equals(scope.Scope);
        }
    }

    /// <summary>
    /// Opens an apply scope and returns the lease that closes it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a nested apply is opened from a different thread than the outer one. A cross-thread
    /// apply is a programming error, not something to serialize silently.
    /// </exception>
    public ReplicaApplyLease Enter(in ApplyScope scope)
    {
        var threadId = Environment.CurrentManagedThreadId;
        lock (gate)
        {
            if (depth == 0)
            {
                ownerThreadId = threadId;
            }
            else if (ownerThreadId != threadId)
            {
                throw new InvalidOperationException(
                    "A replica apply is already open on thread " + ownerThreadId +
                    "; thread " + threadId + " must not write the replica.");
            }

            if (depth == scopeStack.Length) Array.Resize(ref scopeStack, depth * 2);
            scopeStack[depth] = scope;
            depth++;
        }
        Interlocked.Increment(ref appliedTotal);
        return new ReplicaApplyLease(this);
    }

    /// <summary>
    /// Closes the innermost apply. Safe to call through the lease's <c>Dispose</c> from a
    /// <c>finally</c>, which is how an exception cannot leave the context open.
    /// </summary>
    public void Exit()
    {
        lock (gate)
        {
            if (depth == 0) return;
            depth--;
            if (depth == 0) ownerThreadId = 0;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> inside an apply and always closes it.
    /// </summary>
    /// <remarks>
    /// The convenience form for callers that do not want to write the <c>using</c> themselves. An
    /// exception propagates after the scope is closed, so the failure is visible and the session is
    /// left writable only by the next deliberate apply.
    /// </remarks>
    public void Run(in ApplyScope scope, Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        using (Enter(scope))
        {
            action();
        }
    }

    /// <summary>Forgets the apply history. Used when the session ends.</summary>
    public void Reset()
    {
        lock (gate)
        {
            depth = 0;
            ownerThreadId = 0;
        }
        Interlocked.Exchange(ref appliedTotal, 0);
    }
}
