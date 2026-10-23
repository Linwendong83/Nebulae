using System;
using System.Collections.Generic;

namespace NebulaeModel.Authority;

/// <summary>
/// Identifies one subscribed scope: a pool kind plus its scope id.
/// </summary>
/// <remarks>
/// The pair is needed because the same scope id can appear in two pools that are not related, for
/// example planet 101's ground enemy pool and its entity pool.
/// </remarks>
internal readonly struct ScopeKey : IEquatable<ScopeKey>
{
    public ScopeKey(PoolKind kind, int scope)
    {
        Kind = kind;
        Scope = scope;
    }

    public PoolKind Kind { get; }

    public int Scope { get; }

    public bool IsValid => AuthorityScope.IsValidScope(Kind, Scope);

    public bool Equals(ScopeKey other) => Kind == other.Kind && Scope == other.Scope;

    public override bool Equals(object obj) => obj is ScopeKey other && Equals(other);

    public override int GetHashCode() => ((int)Kind * 397) ^ Scope;

    public static bool operator ==(ScopeKey left, ScopeKey right) => left.Equals(right);

    public static bool operator !=(ScopeKey left, ScopeKey right) => !left.Equals(right);

    public override string ToString() => Kind + ":" + Scope;
}

/// <summary>The subscription state.</summary>
internal enum SubscriptionPhase : byte
{
    Unsubscribed = 0,
    Snapshotting = 1,
    Installing = 2,
    CatchingUp = 3,
    Live = 4,
    Resyncing = 5
}

/// <summary>Outcome of offering one state or lifecycle message to the replica.</summary>
internal enum ReplicaApplyResult : byte
{
    /// <summary>Newer revision for a known live object. Apply it.</summary>
    Applied = 0,

    /// <summary>Already applied at this revision. Idempotent no-op, not an error.</summary>
    Duplicate = 1,

    /// <summary>Older revision than what is already applied. Ignore it.</summary>
    Stale = 2,

    /// <summary>This exact key is dead. A dead object in this generation can never come back.</summary>
    RejectedTombstone = 3,

    /// <summary>No lifecycle message has established this object yet. Do not apply state to it.</summary>
    RejectedUnknownObject = 4,

    /// <summary>The message declares a different baseline than the one installed.</summary>
    RejectedBaseline = 5,

    /// <summary>The key is malformed or belongs to another authority epoch.</summary>
    RejectedInvalid = 6
}

/// <summary>Outcome of offering one stream sequence number to the replica.</summary>
internal enum SequenceResult : byte
{
    /// <summary>Next expected sequence. Apply the message.</summary>
    Accepted = 0,

    /// <summary>Already applied. Ignore the message.</summary>
    Duplicate = 1,

    /// <summary>A sequence was skipped. Stop applying and request a new baseline.</summary>
    Gap = 2,

    /// <summary>Belongs to a superseded subscription of this scope. Ignore it.</summary>
    WrongSubscriptionEpoch = 3,

    /// <summary>Scope was never subscribed.</summary>
    UnknownScope = 4,

    /// <summary>Sequence number is not a valid stream position.</summary>
    Invalid = 5
}

/// <summary>
/// One object's version bookkeeping on the replica side.
/// </summary>
internal readonly struct ReplicaObjectVersion
{
    public ReplicaObjectVersion(long revision, bool dead, long deathSequence)
    {
        Revision = revision;
        Dead = dead;
        DeathSequence = deathSequence;
    }

    /// <summary>Latest revision applied for this key.</summary>
    public long Revision { get; }

    /// <summary>True once the host has published the death of this exact key.</summary>
    public bool Dead { get; }

    /// <summary>Stream sequence the death was published at; the cutoff for reclamation.</summary>
    public long DeathSequence { get; }
}

/// <summary>
/// Version state of one subscribed scope: subscription epoch, baseline, stream position, live members and
/// tombstones.
/// </summary>
/// <remarks>
/// <para>Revisions isolate old state, and tombstones prevent a dead generation from being revived. The model
/// has no game or Unity dependencies.</para>
/// <para>Not thread-safe: callers access it only at the quiescent frame boundary.</para>
/// </remarks>
internal sealed class ScopeVersionState
{
    private readonly Dictionary<ObjectKey, ReplicaObjectVersion> objects = new();
    private readonly List<ObjectKey> tombstones = [];

    public ScopeVersionState(ScopeKey scope, AuthorityEpoch epoch)
    {
        if (!scope.IsValid) throw new ArgumentException("Invalid scope: " + scope, nameof(scope));
        if (!epoch.IsValid) throw new ArgumentException("ScopeVersionState needs a valid epoch.", nameof(epoch));
        Scope = scope;
        Epoch = epoch;
    }

    public ScopeKey Scope { get; }

    /// <summary>Authority epoch whose objects this scope accepts. Keys from another epoch are rejected.</summary>
    public AuthorityEpoch Epoch { get; }

    /// <summary>Increments on every subscribe/resubscribe. Never reused, so A→B→A rejects A's tail.</summary>
    public long SubscriptionEpoch { get; private set; }

    public SubscriptionPhase Phase { get; private set; } = SubscriptionPhase.Unsubscribed;

    /// <summary>Baseline currently installed, or 0 before the first baseline.</summary>
    public long BaselineId { get; private set; }

    /// <summary>Highest stream sequence applied contiguously. Gaps are never skipped over.</summary>
    public long LastAppliedSequence { get; private set; }

    /// <summary>Number of live members of this scope.</summary>
    public int MemberCount
    {
        get
        {
            var count = 0;
            foreach (var entry in objects.Values)
            {
                if (!entry.Dead) count++;
            }
            return count;
        }
    }

    /// <summary>Number of retained tombstones.</summary>
    public int TombstoneCount => tombstones.Count;

    /// <summary>
    /// Starts a new subscription and returns its epoch. Existing members and tombstones are kept:
    /// the previous contents stay displayed until the new baseline replaces them atomically, and a
    /// resubscribe must not revive a tombstone.
    /// </summary>
    public long BeginSubscription(SubscriptionPhase phase = SubscriptionPhase.Snapshotting)
    {
        SubscriptionEpoch++;
        Phase = phase;
        return SubscriptionEpoch;
    }

    /// <summary>
    /// Establishes identity for a new object. State may only be applied after this.
    /// </summary>
    /// <remarks>
    /// A key that is already tombstoned stays dead: the host recycled the slot and published the
    /// death, so a later spawn of the *same* generation is a protocol error, and the next
    /// generation arrives as a different key.
    /// </remarks>
    public ReplicaApplyResult Spawn(ObjectKey key, long revision)
    {
        if (!IsUsableKey(key)) return ReplicaApplyResult.RejectedInvalid;
        if (revision <= 0) return ReplicaApplyResult.RejectedInvalid;
        if (objects.TryGetValue(key, out var existing))
        {
            if (existing.Dead) return ReplicaApplyResult.RejectedTombstone;
            if (revision > existing.Revision)
            {
                objects[key] = new ReplicaObjectVersion(revision, false, 0);
                return ReplicaApplyResult.Applied;
            }
            return revision == existing.Revision ? ReplicaApplyResult.Duplicate : ReplicaApplyResult.Stale;
        }
        objects.Add(key, new ReplicaObjectVersion(revision, false, 0));
        return ReplicaApplyResult.Applied;
    }

    /// <summary>
    /// Applies an absolute state update to a known live object.
    /// </summary>
    /// <remarks>
    /// Only a strictly higher revision is accepted, which is what stops a delayed packet from
    /// overwriting newer state. A key that is dead rejects every revision, including higher ones:
    /// death is terminal for a generation, and the "resurrection" the design forbids is exactly a
    /// late Alive/HP/pose for a tombstoned key.
    /// </remarks>
    public ReplicaApplyResult ApplyState(ObjectKey key, long revision)
    {
        if (!IsUsableKey(key)) return ReplicaApplyResult.RejectedInvalid;
        if (revision <= 0) return ReplicaApplyResult.RejectedInvalid;
        if (!objects.TryGetValue(key, out var existing)) return ReplicaApplyResult.RejectedUnknownObject;
        if (existing.Dead) return ReplicaApplyResult.RejectedTombstone;
        if (revision > existing.Revision)
        {
            objects[key] = new ReplicaObjectVersion(revision, false, 0);
            return ReplicaApplyResult.Applied;
        }
        return revision == existing.Revision ? ReplicaApplyResult.Duplicate : ReplicaApplyResult.Stale;
    }

    /// <summary>
    /// Applies an absolute state update that declares which baseline it is an increment of.
    /// </summary>
    /// <remarks>
    /// An increment computed against a baseline the replica does not have must not be applied, even
    /// when its revision looks newer: the two sides are describing different worlds, and the host
    /// has to send a fresh baseline instead.
    /// </remarks>
    public ReplicaApplyResult ApplyState(ObjectKey key, long revision, long declaredBaselineId)
    {
        if (declaredBaselineId != BaselineId) return ReplicaApplyResult.RejectedBaseline;
        return ApplyState(key, revision);
    }

    /// <summary>
    /// Publishes the death of one exact key and records the stream sequence it was published at.
    /// </summary>
    /// <remarks>
    /// The death sequence is what makes reclamation deterministic: tombstones are released when a
    /// new baseline covers that sequence, never on a timer.
    /// </remarks>
    public ReplicaApplyResult MarkDead(ObjectKey key, long revision, long sequence)
    {
        if (!IsUsableKey(key)) return ReplicaApplyResult.RejectedInvalid;
        if (revision <= 0 || sequence <= 0) return ReplicaApplyResult.RejectedInvalid;

        if (objects.TryGetValue(key, out var existing))
        {
            if (existing.Dead) return ReplicaApplyResult.Duplicate;
            if (revision < existing.Revision) return ReplicaApplyResult.Stale;
        }

        objects[key] = new ReplicaObjectVersion(revision, true, sequence);
        tombstones.Add(key);
        return ReplicaApplyResult.Applied;
    }

    /// <summary>True when this exact key has been published as dead.</summary>
    public bool IsTombstoned(ObjectKey key) =>
        objects.TryGetValue(key, out var entry) && entry.Dead;

    /// <summary>Reads the version record of one key.</summary>
    public bool TryGetVersion(ObjectKey key, out ReplicaObjectVersion version) =>
        objects.TryGetValue(key, out version);

    /// <summary>
    /// Offers a stream sequence number. The caller must not apply a message that is not
    /// <see cref="SequenceResult.Accepted"/>.
    /// </summary>
    public SequenceResult AcceptSequence(long subscriptionEpoch, long sequence)
    {
        if (Phase == SubscriptionPhase.Unsubscribed) return SequenceResult.UnknownScope;
        if (sequence <= 0) return SequenceResult.Invalid;
        if (subscriptionEpoch != SubscriptionEpoch) return SequenceResult.WrongSubscriptionEpoch;
        if (sequence <= LastAppliedSequence) return SequenceResult.Duplicate;
        if (sequence != LastAppliedSequence + 1) return SequenceResult.Gap;
        LastAppliedSequence = sequence;
        return SequenceResult.Accepted;
    }

    /// <summary>
    /// Atomically replaces the scope membership with a new baseline and reclaims the tombstones the
    /// baseline provably covers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reclamation is bound to <paramref name="cutoffSequence"/>, a point the host declared and the
    /// replica confirmed, so a tombstone is never forgotten while a stale packet that would
    /// resurrect the object can still arrive. Tombstones published after the cutoff are kept,
    /// because the baseline does not cover them and their deaths are still in the incremental log.
    /// </para>
    /// <para>
    /// Members keep a revision the replica already applied, so installing a baseline cannot rewind
    /// state. A member whose tombstone the baseline covers is revived by the baseline: the host's
    /// snapshot is a complete statement about the scope at that point, so it outranks an older
    /// death record. That is the only path that clears a tombstone, and it is always explicit.
    /// </para>
    /// </remarks>
    public void InstallBaseline(long baselineId, long cutoffSequence, IEnumerable<ObjectKey> members)
    {
        if (baselineId <= 0) throw new ArgumentOutOfRangeException(nameof(baselineId));
        if (cutoffSequence < 0) throw new ArgumentOutOfRangeException(nameof(cutoffSequence));
        if (members == null) throw new ArgumentNullException(nameof(members));

        var replacement = new Dictionary<ObjectKey, ReplicaObjectVersion>();
        foreach (var key in members)
        {
            if (!IsUsableKey(key)) throw new ArgumentException("Baseline contains a foreign key: " + key);
            if (replacement.ContainsKey(key)) continue;
            // Keep a revision the replica already applied; a baseline must never move it backwards.
            var revision = objects.TryGetValue(key, out var existing) ? Math.Max(existing.Revision, 1) : 1;
            replacement.Add(key, new ReplicaObjectVersion(revision, false, 0));
        }

        // Only tombstones the baseline does not cover survive; the rest are provably superseded.
        var survivors = new List<ObjectKey>();
        foreach (var key in tombstones)
        {
            if (!objects.TryGetValue(key, out var entry)) continue;
            if (entry.DeathSequence > cutoffSequence && !replacement.ContainsKey(key)) survivors.Add(key);
        }

        objects.Clear();
        foreach (var entry in replacement) objects.Add(entry.Key, entry.Value);
        foreach (var key in survivors)
        {
            objects[key] = objects.TryGetValue(key, out var prior)
                ? prior
                : new ReplicaObjectVersion(1, true, 0);
        }
        tombstones.Clear();
        tombstones.AddRange(survivors);

        BaselineId = baselineId;
        // The baseline supersedes everything up to the cutoff; catching up resumes after it.
        if (cutoffSequence > LastAppliedSequence) LastAppliedSequence = cutoffSequence;
    }

    /// <summary>Records the subscription phase.</summary>
    public void SetPhase(SubscriptionPhase phase) => Phase = phase;

    /// <summary>
    /// Adopts the host-minted subscription epoch a baseline was produced for.
    /// </summary>
    /// <remarks>
    /// The host owns the subscription epoch: it mints one per subscription and stamps
    /// every packet of that subscription with it, so a client adopts rather than invents the value.
    /// Monotonicity is enforced — an older Begin must not rebind a live subscription — but the same
    /// epoch may be re-adopted when the host repeats itself.
    /// </remarks>
    public bool AdoptSubscription(long subscriptionEpoch, SubscriptionPhase phase)
    {
        if (subscriptionEpoch <= 0 || subscriptionEpoch < SubscriptionEpoch) return false;
        SubscriptionEpoch = subscriptionEpoch;
        Phase = phase;
        return true;
    }

    /// <summary>
    /// Installs a baseline for a stream that restarts with it: membership is replaced atomically
    /// and the stream position resets, because the new subscription counts from one.
    /// </summary>
    /// <remarks>
    /// Old tombstones survive an install that does not name them alive: after a restart they are
    /// mostly redundant (the superseded subscription's tail is refusable by epoch), but keeping
    /// them is the conservative choice — they can only ever refuse a resurrection, never cause one.
    /// </remarks>
    public void RestartStreamAtBaseline(long baselineId, IEnumerable<ObjectKey> members)
    {
        InstallBaseline(baselineId, 0, members);
        LastAppliedSequence = 0;
    }

    /// <summary>
    /// Adopts the revision a baseline member's state was published at, after the baseline install.
    /// </summary>
    /// <remarks>
    /// The install itself never rewinds a revision it had already applied, but a fresh subscriber
    /// starts every member at 1; the snapshot's own revisions are the truth the shipped state bytes
    /// were published at, so they are raised here. Host revisions only ever grow, which keeps the
    /// first post-baseline increment strictly ahead.
    /// </remarks>
    public bool RaiseRevision(ObjectKey key, long revision)
    {
        if (!objects.TryGetValue(key, out var existing) || existing.Dead) return false;
        if (revision <= existing.Revision) return false;
        objects[key] = new ReplicaObjectVersion(revision, false, 0);
        return true;
    }

    /// <summary>
    /// Marks the scope unsubscribed while keeping its version history.
    /// </summary>
    /// <remarks>
    /// The version state keeps the stream position and membership when a client leaves a scope so a
    /// re-subscribe
    /// can continue the same stream instead of restarting it (which would make a late packet look
    /// like a fresh one). A re-subscribe via <see cref="BeginSubscription"/> is what advances the
    /// subscription epoch; this method deliberately does not.
    /// </remarks>
    public void EndSubscription() => Phase = SubscriptionPhase.Unsubscribed;

    private bool IsUsableKey(ObjectKey key) =>
        key.IsValid && key.Epoch.Equals(Epoch) && key.Kind == Scope.Kind && key.Scope == Scope.Scope;
}

/// <summary>
/// The replica's whole version bookkeeping: one <see cref="ScopeVersionState"/> per subscribed scope.
/// </summary>
internal sealed class ReplicaVersionState
{
    private readonly Dictionary<ScopeKey, ScopeVersionState> scopes = new();

    public ReplicaVersionState(AuthorityEpoch epoch)
    {
        if (!epoch.IsValid) throw new ArgumentException("ReplicaVersionState needs a valid epoch.", nameof(epoch));
        Epoch = epoch;
    }

    /// <summary>Authority epoch of the world this replica mirrors.</summary>
    public AuthorityEpoch Epoch { get; }

    public int ScopeCount => scopes.Count;

    /// <summary>Returns the state of a scope, creating it if this is the first time it is seen.</summary>
    public ScopeVersionState GetOrCreateScope(ScopeKey scope)
    {
        if (!scopes.TryGetValue(scope, out var state))
        {
            state = new ScopeVersionState(scope, Epoch);
            scopes.Add(scope, state);
        }
        return state;
    }

    public bool TryGetScope(ScopeKey scope, out ScopeVersionState state) =>
        scopes.TryGetValue(scope, out state);

    /// <summary>
    /// Drops a scope entirely. Used when the client leaves a planet and the scope's objects are no
    /// longer displayed; the host keeps simulating them regardless.
    /// </summary>
    public bool RemoveScope(ScopeKey scope) => scopes.Remove(scope);
}

/// <summary>
/// Host-side revision minting: the single source of <c>ObjectRevision</c> values.
/// </summary>
/// <remarks>
/// The host increments the revision of a key once per authoritative state change. Because the key
/// contains the generation, a recycled pool slot starts a new counter automatically and a stale
/// revision from the previous generation can never be mistaken for a current one.
/// </remarks>
internal sealed class ObjectRevisionTracker
{
    private readonly Dictionary<ObjectKey, long> revisions = new();

    public int Count => revisions.Count;

    /// <summary>Advances and returns the next revision for a key.</summary>
    public long Next(ObjectKey key)
    {
        if (!key.IsValid) throw new ArgumentException("Invalid key: " + key, nameof(key));
        revisions.TryGetValue(key, out var current);
        var next = current + 1;
        revisions[key] = next;
        return next;
    }

    /// <summary>Current revision, or 0 when the key was never stamped.</summary>
    public long Peek(ObjectKey key) => revisions.TryGetValue(key, out var current) ? current : 0;

    /// <summary>
    /// Raises the counter to an imported value without minting, used when a host takes over state
    /// that was already stamped (a loaded save, or a snapshot the host itself produced).
    /// </summary>
    public bool Observe(ObjectKey key, long revision)
    {
        if (!key.IsValid || revision <= 0) return false;
        revisions.TryGetValue(key, out var current);
        if (revision <= current) return false;
        revisions[key] = revision;
        return true;
    }

    /// <summary>Forgets a key. Only valid after its generation is over, never for a live object.</summary>
    public bool Forget(ObjectKey key) => revisions.Remove(key);
}
