#region

using System;
using System.Collections.Generic;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Authority;

#endregion

namespace NebulaeModel.Authority;

/// <summary>
/// The replica's mirror of one object, keyed by the host's <see cref="ObjectKey"/>.
/// </summary>
/// <remarks>
/// <para>
/// The three identities a replica deals with are kept apart by construction:
/// <see cref="Key"/> carries the host's pool slot and generation and is the only thing replication
/// speaks; <see cref="LocalComponentId"/> is the client's own component/skill pool slot, rebound by
/// the adapter and never accepted from the wire; <see cref="LocalRendererId"/> is a purely local
/// GPU handle. Canonical state is the host's facts and nothing else writes it.
/// </para>
/// </remarks>
internal sealed class ReplicaObjectMirror
{
    public ReplicaObjectMirror(ScopeKey scope, in ObjectKey key, long revision, byte[] canonicalState)
    {
        Scope = scope;
        Key = key;
        Revision = revision;
        CanonicalState = canonicalState;
    }

    /// <summary>The scope this mirror belongs to. A baseline replaces only its own scope's mirrors.</summary>
    public ScopeKey Scope { get; }

    /// <summary>The host identity this mirror reflects. Never the local slot.</summary>
    public ObjectKey Key { get; }

    /// <summary>Highest revision applied to this mirror.</summary>
    public long Revision { get; internal set; }

    /// <summary>Latest absolute canonical state, or null until the host published one.</summary>
    public byte[] CanonicalState { get; internal set; }

    /// <summary>Client-local component pool slot, or 0 while unbound. Adapter-owned.</summary>
    public int LocalComponentId { get; internal set; }

    /// <summary>Client-local renderer handle, or 0 while unbound. Always local, never synced.</summary>
    public int LocalRendererId { get; internal set; }
}

/// <summary>
/// Receives the replica's mirror mutations so an adapter can write game-visible structures.
/// </summary>
/// <remarks>
/// <para>
/// The replica owns the canonical layer; the adapters own turning it into local component pools.
/// This interface is the only channel between them: every call happens inside the apply call the
/// session made, after the mirror has accepted the change, so an observer
/// never sees state the version gate refused.
/// </para>
/// <para>
/// The three calls mirror the three ways the mirror changes. A spawned identity carries no state
/// yet and notifies nothing — the state record that follows does. A baseline notifies once for the
/// whole membership, which lets the binding reconcile atomically (keys it holds but the baseline
/// dropped are evicted there). A despawn notifies so local bindings die with their key.
/// </para>
/// </remarks>
internal interface IReplicaMirrorObserver
{
    /// <summary>One applied absolute-state record; the mirror already holds these bytes.</summary>
    void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state);

    /// <summary>A baseline was installed; <paramref name="members"/> is the complete new membership.</summary>
    void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members);

    /// <summary>One member left the scope (despawn); its local bindings must be released.</summary>
    void OnMemberRemoved(ScopeKey scope, in ObjectKey key);
}

/// <summary>Game adapters must have their backing pool before a snapshot can be acknowledged.</summary>
internal interface IReplicaBaselineReadiness
{
    bool IsReadyForBaseline(ScopeKey scope);
}

/// <summary>
/// The optional presentation side of a mirror: a member the host published as dead.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IReplicaMirrorObserver.OnMemberRemoved"/> answers "this key left the mirror" for every
/// reason — a published death, a baseline that no longer names the member, a scope the client left.
/// Only the first is a death, and only a death may present one: an unload that exploded every
/// mirrored enemy would be a lie about the world. The replica therefore calls this beside
/// <see cref="IReplicaMirrorObserver.OnMemberRemoved"/>, and before it, while the local shell is
/// still live — the one moment a presentation can read the pose the last state record installed.
/// </para>
/// <para>
/// Implementations are presentation-only: a death effect must never write a pool, a rule or a
/// statistic, and a failure must not stop the removal that follows it. Adapters that own no
/// presentation simply do not implement this.
/// </para>
/// </remarks>
internal interface IReplicaDeathObserver
{
    /// <summary>One member the host published as dead; its shell is still live.</summary>
    void OnMemberDied(ScopeKey scope, in ObjectKey key);
}

/// <summary>
/// Carries the replica's scope-control requests to the host: resync requests raised by the
/// recovery rules, and the subscribe/unsubscribe the session issues when the player moves.
/// </summary>
/// <remarks>
/// Identity is filled by the transport sink, never by the replica: player identity comes from the connection,
/// so the packet only states what is being asked about which scope.
/// </remarks>
internal interface IScopeControlSink
{
    void Send(ScopeControlOp op, ScopeKey scope, ScopeRecoveryReason reason, bool digestOnly,
        long subscriptionEpoch, long lastAppliedSequence);
}

/// <summary>
/// The client's standard replica: version gating plus the canonical mirror store.
/// </summary>
/// <remarks>
/// <para>
/// The replica applies host facts in stream order and refuses everything the version state cannot
/// vouch for. The rules the whole class enforces, in the order they matter:
/// </para>
/// <list type="number">
/// <item>A subscription starts in Snapshotting: the host first sends a baseline, the replica
/// installs it as one atomic membership replacement, and only the baseline's stream-resume marker
/// moves the scope to Live. Live facts offered before that are a protocol violation and flag the
/// scope instead of being applied.</item>
/// <item>Identity before state. A state record for a key no lifecycle message or baseline
/// established is refused and flags the scope for resync — it is evidence of a lost spawn, never
/// noise to ignore.</item>
/// <item>A dead generation stays dead. State or spawn for a tombstoned key is refused whatever its
/// revision; the next generation is a different key. Only a baseline that names the key alive may
/// clear the tombstone, and it does so explicitly.</item>
/// <item>The stream is contiguous and belongs to one subscription epoch. A duplicate position is
/// ignored, a gap stops application and flags the scope, and a packet from a superseded
/// subscription is refused by its epoch before it can be mistaken for a fresh one.</item>
/// <item>A batch that hits a refused record stops there. Applying the rest would build a world the
/// host never described.</item>
/// </list>
/// <para>
/// The mirror store here is the canonical layer; writing it into game-visible structures is the
/// adapters' job and happens only inside the session's apply window. Nothing in this class
/// references Unity or game types, so the whole behaviour surface runs in plain tests.
/// </para>
/// </remarks>
internal sealed class ClientWorldReplica
{
    private readonly List<ObjectKey> runtimeAuditKeys = new();
    private int runtimeAuditIndex;
    private long runtimeAuditTick = -1;
    public long SpaceEnemyRequestId { get; private set; }

    public void BeginSpaceEnemySubscription()
    {
        SpaceEnemyRequestId++;
        var scope = new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector);
        Snapshots.Abandon(scope);
        pendingInstalls.Remove(scope);
    }
    private readonly Dictionary<ScopeKey, FrozenScopeSnapshot> pendingInstalls = [];
    private readonly Dictionary<ObjectKey, ReplicaObjectMirror> mirrors = new();
    private readonly Dictionary<(PoolKind Kind, int Scope, int Slot), ObjectKey> currentSlots = new();
    private readonly HashSet<ScopeKey> needsResync = [];
    private readonly Dictionary<ScopeKey, Queue<PendingScopeDigest>> pendingDigests = new();
    private readonly Dictionary<ScopeKey, ScopeDigestState> digestSummaries = new();
    private readonly List<ScopeDigestMember> digestScratch = [];

    /// <summary>A digest statement waiting for its stream position to be applied.</summary>
    private readonly struct PendingScopeDigest
    {
        public PendingScopeDigest(long subscriptionEpoch, long baselineId, long hostTick,
            long declaredStreamSequence, int memberCount, ulong digest)
        {
            SubscriptionEpoch = subscriptionEpoch;
            BaselineId = baselineId;
            HostTick = hostTick;
            DeclaredStreamSequence = declaredStreamSequence;
            MemberCount = memberCount;
            Digest = digest;
        }

        public long SubscriptionEpoch { get; }
        public long BaselineId { get; }
        public long HostTick { get; }
        public long DeclaredStreamSequence { get; }
        public int MemberCount { get; }
        public ulong Digest { get; }

        public ScopeDigestState Compose(ScopeKey scope) => new(scope, SubscriptionEpoch, BaselineId, HostTick,
            DeclaredStreamSequence, MemberCount, Digest);
    }

    private long appliedTotal;
    private long duplicateTotal;
    private long staleTotal;
    private long tombstoneRefusedTotal;
    private long unknownObjectTotal;
    private long baselineRefusedTotal;
    private long gapTotal;
    private long sequenceDroppedTotal;
    private long invalidTotal;
    private long streamBeforeBaselineTotal;
    private long snapshotsInstalledTotal;
    private long unsentSnapshotAcksTotal;
    private long resyncsRequestedTotal;
    private long unsentScopeControlTotal;
    private long streamSuspendedTotal;
    private long digestsMatchedTotal;
    private long digestsMismatchedTotal;
    private long digestsSupersededTotal;
    private long digestsDroppedTotal;
    private long digestSummariesRecordedTotal;

    public ClientWorldReplica(AuthorityEpoch epoch)
    {
        if (!epoch.IsValid) throw new ArgumentException("ClientWorldReplica needs a valid epoch.", nameof(epoch));
        Epoch = epoch;
        Versions = new ReplicaVersionState(epoch);
    }

    public AuthorityEpoch Epoch { get; }

    /// <summary>Version bookkeeping, one state per subscribed scope.</summary>
    public ReplicaVersionState Versions { get; }

    /// <summary>The canonical mirror, keyed by host object key.</summary>
    public IReadOnlyDictionary<ObjectKey, ReplicaObjectMirror> Mirrors => mirrors;

    public int AuditRuntimeObjects(long tick, IReplicaRuntimeVerifier verifier, int maxObjects = 32)
    {
        if (verifier == null || maxObjects <= 0) return 0;
        if (runtimeAuditTick < 0 || tick < runtimeAuditTick || tick - runtimeAuditTick >= 300 ||
            runtimeAuditKeys.Count == 0 && mirrors.Count > 0)
        {
            runtimeAuditKeys.Clear(); runtimeAuditKeys.AddRange(mirrors.Keys);
            runtimeAuditTick = tick; runtimeAuditIndex = 0;
        }
        var checkedObjects = 0;
        for (var visited = 0; visited < Math.Min(maxObjects, runtimeAuditKeys.Count); visited++)
        {
            if (runtimeAuditIndex >= runtimeAuditKeys.Count) runtimeAuditIndex = 0;
            var key = runtimeAuditKeys[runtimeAuditIndex];
            // A key whose object has left the mirror set is gone for good, so it is dropped from the
            // rotation rather than left to be looked up again. The drop costs one visit once; leaving
            // it in place costs a visit on every pass until the list is rebuilt, so a sector that
            // lost half its enemies would audit half as many survivors for the rest of the interval
            // and drift would take proportionally longer to notice. Keys are removed, never added,
            // so the rotation converges on the live set instead of decaying with it.
            if (!mirrors.TryGetValue(key, out var mirror))
            {
                var last = runtimeAuditKeys.Count - 1;
                runtimeAuditKeys[runtimeAuditIndex] = runtimeAuditKeys[last];
                runtimeAuditKeys.RemoveAt(last);
                continue;
            }
            runtimeAuditIndex++;
            if (mirror.CanonicalState == null ||
                !Versions.TryGetScope(mirror.Scope, out var version) || version.Phase != SubscriptionPhase.Live) continue;
            var result = verifier.VerifyObject(key, mirror.CanonicalState);
            if (result == RuntimeObjectIntegrity.Unavailable) continue;
            checkedObjects++;
            if (result == RuntimeObjectIntegrity.Drift) RequestResync(mirror.Scope, ScopeRecoveryReason.RuntimeDrift);
        }
        return checkedObjects;
    }

    public bool TryFindKey(PoolKind kind, int scope, int nativeId, out ObjectKey key)
    {
        key = default;
        return Versions.TryGetScope(new ScopeKey(kind, scope), out var state) &&
            (state.Phase == SubscriptionPhase.Live || state.Phase == SubscriptionPhase.CatchingUp) &&
            !needsResync.Contains(new ScopeKey(kind, scope)) && currentSlots.TryGetValue((kind, scope, nativeId), out key) &&
            key.IsValid && !state.IsTombstoned(key);
    }

    /// <summary>Scopes whose stream broke or lost a dependency. They receive no further trust.</summary>
    public IReadOnlyCollection<ScopeKey> ScopesNeedingResync => needsResync;

    /// <summary>Staging area of incoming baselines. Nothing here touches the active world.</summary>
    public ClientSnapshotStaging Snapshots { get; } = new();

    /// <summary>
    /// Where the replica returns its snapshot acknowledgements. Unset in pure-model tests that do
    /// not exercise the host conversation; the omission is counted, never silent.
    /// </summary>
    public ISnapshotAckSink SnapshotAckSink { get; set; }

    /// <summary>
    /// Adapter notified of mirror mutations. Unset means no adapter writes game structures —
    /// the mirror stays canonical-only, which is the fail-closed default.
    /// </summary>
    public IReplicaMirrorObserver MirrorObserver { get; set; }

    /// <summary>
    /// Where the replica sends scope-control requests. Unset in pure-model tests that do not
    /// exercise the host conversation; the omission is counted, never silent.
    /// </summary>
    public IScopeControlSink ScopeControlSink { get; set; }

    public long AppliedTotal => appliedTotal;
    public long DuplicateTotal => duplicateTotal;
    public long StaleTotal => staleTotal;
    public long TombstoneRefusedTotal => tombstoneRefusedTotal;
    public long UnknownObjectTotal => unknownObjectTotal;
    public long BaselineRefusedTotal => baselineRefusedTotal;
    public long GapTotal => gapTotal;
    public long SequenceDroppedTotal => sequenceDroppedTotal;
    public long InvalidTotal => invalidTotal;

    /// <summary>Live stream packets offered before their scope's baseline was installed.</summary>
    public long StreamBeforeBaselineTotal => streamBeforeBaselineTotal;

    /// <summary>Baselines installed as one atomic membership replacement.</summary>
    public long SnapshotsInstalledTotal => snapshotsInstalledTotal;

    /// <summary>Baselines installed while no ack sink was wired; the host stays uninformed.</summary>
    public long UnsentSnapshotAcksTotal => unsentSnapshotAcksTotal;

    /// <summary>Recovery requests the replica raised (deduplicated per scope).</summary>
    public long ResyncsRequestedTotal => resyncsRequestedTotal;

    /// <summary>Scope-control requests dropped for lack of a sink; the host stays uninformed.</summary>
    public long UnsentScopeControlTotal => unsentScopeControlTotal;

    /// <summary>Stream packets refused because their scope is suspended for a rebuild (Resyncing).</summary>
    public long StreamSuspendedTotal => streamSuspendedTotal;

    /// <summary>Digests verified equal at the stream position both sides held.</summary>
    public long DigestsMatchedTotal => digestsMatchedTotal;

    /// <summary>Digests that did not match; each raised a resync instead of being ignored.</summary>
    public long DigestsMismatchedTotal => digestsMismatchedTotal;

    /// <summary>Digests for stream positions already applied past; old news, not a divergence.</summary>
    public long DigestsSupersededTotal => digestsSupersededTotal;

    /// <summary>Digests refused outright (wrong phase, superseded subscription, full buffer).</summary>
    public long DigestsDroppedTotal => digestsDroppedTotal;

    /// <summary>Digests recorded as observation summaries for digest-only scopes.</summary>
    public long DigestSummariesRecordedTotal => digestSummariesRecordedTotal;

    /// <summary>Last digest recorded per digest-only scope: the observation summary a UI reads.</summary>
    public IReadOnlyDictionary<ScopeKey, ScopeDigestState> DigestSummaries => digestSummaries;

    public bool IsSubscribed(ScopeKey scope) =>
        Versions.TryGetScope(scope, out var state) && state.Phase != SubscriptionPhase.Unsubscribed;

    /// <summary>
    /// Asks to subscribe to a scope. The subscription only completes when the host's baseline
    /// arrives: the scope sits in Snapshotting until install, and Live follows the host's
    /// stream-resume marker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Calling Subscribe on a scope that is already live re-enters Snapshotting: it is a request
    /// for a fresh baseline, and while it is pending the scope stops applying live facts, exactly
    /// like the first subscription. A Resyncing scope re-enters the same way — that is the path a
    /// resync's new baseline arrives through. Only a baseline conversation already in flight is
    /// left alone.
    /// </para>
    /// <para>
    /// A digest-only subscription is an observation, not a baseline conversation: it is Live
    /// immediately with no baseline, receives no stream, and only the host's digests. Upgrading to
    /// a full subscription re-enters Snapshotting from there; downgrading replaces any prior
    /// subscription state, because an observation holds nothing the stream left behind.
    /// </para>
    /// </remarks>
    public void Subscribe(ScopeKey scope, bool digestOnly = false)
    {
        if (digestOnly)
        {
            // A fresh observation cannot share version history with a subscription it replaces.
            Versions.RemoveScope(scope);
            pendingDigests.Remove(scope);
            Versions.GetOrCreateScope(scope).SetPhase(SubscriptionPhase.Live);
            return;
        }
        var state = Versions.GetOrCreateScope(scope);
        if (state.Phase == SubscriptionPhase.Snapshotting || state.Phase == SubscriptionPhase.Installing)
        {
            return;
        }
        state.SetPhase(SubscriptionPhase.Snapshotting);
    }

    /// <summary>
    /// Leaves a scope. Membership and version history are kept so a re-subscribe's baseline can
    /// replace them atomically; any half-staged baseline is abandoned.
    /// </summary>
    public void Unsubscribe(ScopeKey scope)
    {
        pendingInstalls.Remove(scope);
        foreach (var mirror in mirrors.Values)
            if (mirror.Scope.Equals(scope)) MirrorObserver?.OnMemberRemoved(scope, mirror.Key);
        Snapshots.Abandon(scope);
        pendingDigests.Remove(scope);
        needsResync.Remove(scope);
        if (Versions.TryGetScope(scope, out var state)) state.EndSubscription();
    }

    /// <summary>
    /// Runs the recovery plan for one observed break: suspends the scope's input, requests
    /// the baselines the plan names, and enters Resyncing where a live scope existed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plan is deterministic per reason and scope: a stream gap rebuilds its own
    /// scope, an unknown core topology asks for the dependency scope's baseline first. Requesting
    /// is deduplicated by the resync set, so repeated observations of the same break send one
    /// request, and the set clears only when a baseline actually installs.
    /// </para>
    /// <para>
    /// While suspended (Resyncing) the scope applies nothing: a half-broken stream must not keep
    /// writing a world the host no longer describes. Recovery completes only through
    /// <see cref="CommitSnapshot"/>'s baseline install.
    /// </para>
    /// </remarks>
    public bool RequestResync(ScopeKey scope, ScopeRecoveryReason reason)
    {
        if (reason == ScopeRecoveryReason.None) return false;
        var plan = ScopeRecoveryPlanner.Plan(reason, scope);
        var requested = false;
        foreach (var action in plan.Actions)
        {
            if (action.Kind == ScopeRecoveryActionKind.SuspendInput) continue;
            requested |= FlagAndRequestResync(action.Scope, action.Reason);
        }
        return requested;
    }

    private bool FlagAndRequestResync(ScopeKey scope, ScopeRecoveryReason reason)
    {
        if (!Versions.TryGetScope(scope, out var state) || state.Phase == SubscriptionPhase.Unsubscribed)
        {
            return false;
        }
        if (!needsResync.Add(scope))
        {
            // One request per break is already outstanding; the host re-baselines from it.
            return true;
        }
        if (scope.Kind == PoolKind.SpaceEnemy && SpaceEnemyRequestId != 0) BeginSpaceEnemySubscription();
        if (state.Phase is SubscriptionPhase.Live or SubscriptionPhase.CatchingUp or SubscriptionPhase.Installing)
        {
            state.SetPhase(SubscriptionPhase.Resyncing);
        }
        System.Threading.Interlocked.Increment(ref resyncsRequestedTotal);
        var sink = ScopeControlSink;
        if (sink == null)
        {
            System.Threading.Interlocked.Increment(ref unsentScopeControlTotal);
            return true;
        }
        sink.Send(ScopeControlOp.Resync, scope, reason, digestOnly: false, state.SubscriptionEpoch,
            state.LastAppliedSequence);
        return true;
    }

    /// <summary>
    /// Receives the host's canonical digest of a scope and verifies it when the stream position it
    /// describes has been applied.
    /// </summary>
    /// <remarks>
    /// A digest is a statement about one exact stream position. Ahead of it, it is deferred in a
    /// bounded per-scope buffer; behind it, it is superseded old news — neither is a divergence. At
    /// the position itself, member count and content must match the mirrors exactly, or the scope
    /// is suspended and re-baselined. A digest-only observation has no mirrors to compare against:
    /// the digest is recorded as the summary the observer reads.
    /// </remarks>
    public bool ReceiveScopeDigest(ScopeKey scope, long subscriptionEpoch, long hostTick,
        long declaredStreamSequence, long baselineId, int memberCount, ulong digest)
    {
        if (!Versions.TryGetScope(scope, out var state) || state.Phase == SubscriptionPhase.Unsubscribed)
        {
            System.Threading.Interlocked.Increment(ref digestsDroppedTotal);
            return false;
        }
        if (subscriptionEpoch != state.SubscriptionEpoch)
        {
            // A digest of a superseded subscription says nothing about this one.
            System.Threading.Interlocked.Increment(ref digestsDroppedTotal);
            return false;
        }
        if (state.Phase != SubscriptionPhase.Live && state.Phase != SubscriptionPhase.CatchingUp)
        {
            // Snapshotting/Installing: nothing comparable yet. Resyncing: the digest describes the
            // world being replaced. Both drop instead of verifying against a moving target.
            System.Threading.Interlocked.Increment(ref digestsDroppedTotal);
            return false;
        }
        if (state.Phase == SubscriptionPhase.Live && state.BaselineId == 0)
        {
            digestSummaries[scope] = new PendingScopeDigest(subscriptionEpoch, baselineId, hostTick,
                declaredStreamSequence, memberCount, digest).Compose(scope);
            System.Threading.Interlocked.Increment(ref digestSummariesRecordedTotal);
            return true;
        }

        var pending = new PendingScopeDigest(subscriptionEpoch, baselineId, hostTick,
            declaredStreamSequence, memberCount, digest);
        if (declaredStreamSequence > state.LastAppliedSequence)
        {
            if (!pendingDigests.TryGetValue(scope, out var queue))
            {
                queue = new Queue<PendingScopeDigest>();
                pendingDigests[scope] = queue;
            }
            if (queue.Count >= AuthorityLimits.PendingDigestsPerScopeMax)
            {
                // An observer that cannot keep up loses the oldest check, not a bounded guarantee:
                // the next digest re-checks the scope anyway.
                queue.Dequeue();
                System.Threading.Interlocked.Increment(ref digestsDroppedTotal);
            }
            queue.Enqueue(pending);
            return true;
        }
        if (declaredStreamSequence < state.LastAppliedSequence)
        {
            System.Threading.Interlocked.Increment(ref digestsSupersededTotal);
            return false;
        }
        VerifyScopeDigest(scope, state, pending);
        return true;
    }

    /// <summary>Verifies the digests whose stream position the replica has now reached.</summary>
    private void ProcessPendingDigests(ScopeKey scope, ScopeVersionState state)
    {
        if (!pendingDigests.TryGetValue(scope, out var queue)) return;
        while (queue.Count > 0)
        {
            var pending = queue.Peek();
            if (pending.DeclaredStreamSequence > state.LastAppliedSequence) return;
            queue.Dequeue();
            if (pending.DeclaredStreamSequence < state.LastAppliedSequence)
            {
                System.Threading.Interlocked.Increment(ref digestsSupersededTotal);
                continue;
            }
            VerifyScopeDigest(scope, state, pending);
            return;
        }
    }

    private void VerifyScopeDigest(ScopeKey scope, ScopeVersionState state, in PendingScopeDigest pending)
    {
        if (pending.BaselineId != state.BaselineId)
        {
            // The subscription moved to a different baseline between the digest and its check; the
            // new baseline's own digests are the ones that count.
            System.Threading.Interlocked.Increment(ref digestsDroppedTotal);
            return;
        }
        if (state.MemberCount != pending.MemberCount)
        {
            System.Threading.Interlocked.Increment(ref digestsMismatchedTotal);
            RequestResync(scope, ScopeRecoveryReason.DigestMismatch);
            return;
        }
        digestScratch.Clear();
        foreach (var pair in mirrors)
        {
            if (pair.Value.Scope.Equals(scope))
            {
                digestScratch.Add(new ScopeDigestMember(pair.Key, pair.Value.Revision, pair.Value.CanonicalState));
            }
        }
        var localDigest = ScopeDigestComputer.Compute(digestScratch);
        if (localDigest != pending.Digest)
        {
            System.Threading.Interlocked.Increment(ref digestsMismatchedTotal);
            RequestResync(scope, ScopeRecoveryReason.DigestMismatch);
            return;
        }
        System.Threading.Interlocked.Increment(ref digestsMatchedTotal);
    }

    /// <summary>
    /// Opens a staging buffer from the host's Begin. The scope must have asked to subscribe.
    /// </summary>
    public bool BeginSnapshot(ScopeKey scope, long subscriptionEpoch, long baselineId, int chunkCount,
        long totalBytes, ulong hash, long requestId = 0)
    {
        if (scope.Kind == PoolKind.SpaceEnemy && SpaceEnemyRequestId != 0 && requestId != SpaceEnemyRequestId)
        {
            Snapshots.CountRefused();
            return false;
        }
        if (!Versions.TryGetScope(scope, out var state))
        {
            Snapshots.CountRefused();
            LogBaselineReject("begin:no-scope-state", scope, subscriptionEpoch, baselineId, state: null);
            return false;
        }
        if (state.Phase != SubscriptionPhase.Snapshotting && state.Phase != SubscriptionPhase.Resyncing)
        {
            Snapshots.CountRefused();
            LogBaselineReject("begin:wrong-phase", scope, subscriptionEpoch, baselineId, state);
            return false;
        }
        if (!Snapshots.Begin(scope, subscriptionEpoch, baselineId, chunkCount, totalBytes, hash))
        {
            LogBaselineReject("begin:staging-refused", scope, subscriptionEpoch, baselineId, state);
            return false;
        }
        // The host's epoch is what this subscription speaks from now on; the packet stream will be
        // refused against any other value.
        return state.AdoptSubscription(subscriptionEpoch, SubscriptionPhase.Snapshotting);
    }

    private long baselineRejectsLogged;

    /// <summary>
    /// Reports the guard that refused a baseline, with a bounded number of diagnostic messages per process.
    /// </summary>
    private void LogBaselineReject(string reason, in ScopeKey scope, long subscriptionEpoch, long baselineId,
        ScopeVersionState state)
    {
        if (System.Threading.Interlocked.Increment(ref baselineRejectsLogged) > 20) return;
        Log.Warn($"[authority] baseline refused ({reason}) scope={scope} subEpoch={subscriptionEpoch} " +
                 $"baseline={baselineId} phase={(state == null ? "none" : state.Phase.ToString())} " +
                 $"stateSubEpoch={(state?.SubscriptionEpoch ?? -1)}");
    }

    /// <summary>Places one chunk of the baseline into staging.</summary>
    public bool ReceiveSnapshotChunk(ScopeKey scope, long subscriptionEpoch, long baselineId, int chunkIndex,
        byte[] data) =>
        Snapshots.Chunk(scope, subscriptionEpoch, baselineId, chunkIndex, data);

    /// <summary>
    /// Completes a baseline: verifies the reassembly, decodes it, and installs it as one atomic
    /// membership replacement. The scope moves to CatchingUp; Live follows the host's resume marker.
    /// </summary>
    public bool CommitSnapshot(ScopeKey scope, long subscriptionEpoch, long baselineId, int chunkCount, ulong hash)
    {
        if (scope.Kind == PoolKind.SpaceEnemy && SpaceEnemyRequestId != 0 &&
            !Snapshots.IsStaging(scope, subscriptionEpoch, baselineId))
        {
            Snapshots.CountRefused();
            return false;
        }
        if (!Versions.TryGetScope(scope, out var state) || state.Phase != SubscriptionPhase.Snapshotting)
        {
            Snapshots.CountRefused();
            LogBaselineReject("commit:wrong-phase", scope, subscriptionEpoch, baselineId, state);
            return false;
        }
        if (!Snapshots.Commit(scope, subscriptionEpoch, baselineId, chunkCount, hash, out var assembled))
        {
            LogBaselineReject("commit:staging-refused", scope, subscriptionEpoch, baselineId, state);
            RequestResync(scope, ScopeRecoveryReason.BaselineRefused);
            return false;
        }
        if (!AuthoritySnapshotCodec.TryDecode(assembled, Epoch, scope, out var snapshot, out _))
        {
            // The bookkeeping agreed but the image did not decode: the buffer is already discarded,
            // and the scope is flagged rather than half-trusted.
            RequestResync(scope, ScopeRecoveryReason.BaselineRefused);
            return false;
        }
        if (snapshot.BaselineId != baselineId || snapshot.SubscriptionEpoch != subscriptionEpoch)
        {
            // The envelope bookkeeping contradicts the image it described; neither can be trusted.
            RequestResync(scope, ScopeRecoveryReason.BaselineRefused);
            return false;
        }
        if (MirrorObserver is IReplicaBaselineReadiness ready && !ready.IsReadyForBaseline(scope))
        {
            pendingInstalls[scope] = snapshot;
            state.SetPhase(SubscriptionPhase.Installing);
            return true;
        }
        InstallBaseline(scope, state, snapshot);
        return true;
    }

    public void RetryPendingBaselines(Action<ScopeKey, Action> apply)
    {
        foreach (var scope in new List<ScopeKey>(pendingInstalls.Keys))
        {
            var snapshot = pendingInstalls[scope];
            if (!Versions.TryGetScope(scope, out var state) ||
                state.Phase != SubscriptionPhase.Installing || state.SubscriptionEpoch != snapshot.SubscriptionEpoch)
            {
                pendingInstalls.Remove(scope);
                continue;
            }
            if (MirrorObserver is IReplicaBaselineReadiness ready && !ready.IsReadyForBaseline(scope)) continue;
            apply(scope, () => InstallBaseline(scope, state, snapshot));
            pendingInstalls.Remove(scope);
        }
    }

    public void ReapplyScope(ScopeKey scope)
    {
        if (!Versions.TryGetScope(scope, out var state) ||
            (state.Phase != SubscriptionPhase.Live && state.Phase != SubscriptionPhase.CatchingUp)) return;
        var members = new List<SnapshotMemberRecord>();
        foreach (var mirror in mirrors.Values)
            if (mirror.Scope.Equals(scope)) members.Add(new SnapshotMemberRecord(mirror.Key, mirror.Revision, mirror.CanonicalState));
        MirrorObserver?.OnBaselineInstalled(scope, members);
    }

    /// <summary>
    /// Atomically replaces the scope's membership and mirror with a baseline.
    /// </summary>
    /// <remarks>
    /// The install is one replacement, not a patch: members the baseline does not name leave the
    /// mirror, members it names get their identity and shipped state, and a tombstone the baseline
    /// names alive is the one explicit path that clears it. Local component and renderer bindings
    /// survive for keys the baseline keeps; re-binding the rest is the adapter's job.
    /// </remarks>
    private void InstallBaseline(ScopeKey scope, ScopeVersionState state, FrozenScopeSnapshot snapshot)
    {
        var keys = new List<ObjectKey>(snapshot.Members.Count);
        foreach (var member in snapshot.Members)
        {
            keys.Add(member.Key);
        }

        state.RestartStreamAtBaseline(snapshot.BaselineId, keys);
        foreach (var member in snapshot.Members)
        {
            if (member.State != null)
            {
                state.RaiseRevision(member.Key, member.Revision);
            }
        }

        // The replacement is scoped: mirrors of other scopes are nobody else's baseline.
        var keep = new HashSet<ObjectKey>(keys);
        List<ObjectKey> doomed = null;
        foreach (var pair in mirrors)
        {
            if (pair.Value.Scope.Equals(scope) && !keep.Contains(pair.Key))
            {
                doomed ??= new List<ObjectKey>();
                doomed.Add(pair.Key);
            }
        }
        if (doomed != null)
        {
            foreach (var key in doomed)
            {
                mirrors.Remove(key);
                var slot = (key.Kind, key.Scope, key.NativeId);
                if (currentSlots.TryGetValue(slot, out var owner) && owner.Equals(key)) currentSlots.Remove(slot);
            }
        }

        foreach (var member in snapshot.Members)
        {
            mirrors.TryGetValue(member.Key, out var existing);
            currentSlots[(member.Key.Kind, member.Key.Scope, member.Key.NativeId)] = member.Key;
            var revision = state.TryGetVersion(member.Key, out var version) ? version.Revision : member.Revision;
            var mirror = new ReplicaObjectMirror(scope, member.Key, revision,
                member.State ?? existing?.CanonicalState);
            if (existing != null)
            {
                mirror.LocalComponentId = existing.LocalComponentId;
                mirror.LocalRendererId = existing.LocalRendererId;
            }
            mirrors[member.Key] = mirror;
        }

        state.SetPhase(SubscriptionPhase.CatchingUp);
        needsResync.Remove(scope);
        pendingDigests.Remove(scope);
        System.Threading.Interlocked.Increment(ref snapshotsInstalledTotal);

        // The adapter reconciles the whole scope against the new membership in one call — the
        // atomic replacement it mirrors, including the keys the baseline dropped — before the ack
        // leaves, so the host never learns "installed" from a half-reconciled world.
        try { MirrorObserver?.OnBaselineInstalled(scope, snapshot.Members); }
        catch
        {
            state.SetPhase(SubscriptionPhase.Installing);
            pendingInstalls[scope] = snapshot;
            throw;
        }

        var ack = AuthoritySnapshotAckPacket.Create(
            new AuthorityEnvelopeHeader(AuthorityFamily.SnapshotAck, Epoch,
                connection: default, sequence: 0, hostTick: snapshot.HostTick, claimedPlayerId: 0,
                payloadLength: 0),
            snapshot.BaselineId, state.LastAppliedSequence, accepted: true);
        if (SnapshotAckSink == null)
        {
            System.Threading.Interlocked.Increment(ref unsentSnapshotAcksTotal);
            return;
        }
        SnapshotAckSink.Send(ack);
    }

    /// <summary>
    /// Applies one lifecycle batch: spawns establish identity, despawns end a membership, and a death
    /// record is the despawn that also announces the death it came from.
    /// </summary>
    /// <returns>True when the whole batch was applied; false when it was refused at the gate or at a record.</returns>
    public bool ApplyLifecycle(ScopeKey scope, long subscriptionEpoch, long streamSequence,
        IReadOnlyList<AuthorityLifecycleRecord> records)
    {
        if (records == null) throw new ArgumentNullException(nameof(records));
        if (!AcceptStream(scope, subscriptionEpoch, streamSequence, out var state)) return false;

        foreach (var record in records)
        {
            ReplicaApplyResult result;
            switch (record.Op)
            {
                case AuthorityLifecycleOp.Spawn:
                    result = state.Spawn(record.Key, record.Revision);
                    break;
                case AuthorityLifecycleOp.Despawn:
                case AuthorityLifecycleOp.Death:
                    result = state.MarkDead(record.Key, record.Revision, streamSequence);
                    break;
                default:
                    result = ReplicaApplyResult.RejectedInvalid;
                    break;
            }

            if (record.Op == AuthorityLifecycleOp.Spawn && result == ReplicaApplyResult.RejectedTombstone)
            {
                // Spawning the exact key a tombstone holds is a host protocol violation, not a late
                // packet: the batch stops and the scope is flagged instead of half-trusting it.
                System.Threading.Interlocked.Increment(ref tombstoneRefusedTotal);
                RequestResync(scope, ScopeRecoveryReason.ProtocolViolation);
                return false;
            }

            if (!RecordAndContinue(scope, result)) return false;

            if (record.Op == AuthorityLifecycleOp.Spawn && result == ReplicaApplyResult.Applied)
            {
                var previousState = mirrors.TryGetValue(record.Key, out var existing) ? existing.CanonicalState : null;
                mirrors[record.Key] = new ReplicaObjectMirror(scope, record.Key, record.Revision, previousState);
                currentSlots[(record.Key.Kind, record.Key.Scope, record.Key.NativeId)] = record.Key;
            }
            else if (result == ReplicaApplyResult.Applied &&
                     (record.Op == AuthorityLifecycleOp.Despawn || record.Op == AuthorityLifecycleOp.Death))
            {
                mirrors.Remove(record.Key);
                var slot = (record.Key.Kind, record.Key.Scope, record.Key.NativeId);
                if (currentSlots.TryGetValue(slot, out var owner) && owner.Equals(record.Key)) currentSlots.Remove(slot);
                // A published death is presentation, not just bookkeeping: the mirror is gone from
                // the store but the local shell is still live, which is the one moment the adapter
                // can read the pose it should explode at. A plain despawn notifies nothing here.
                if (record.Op == AuthorityLifecycleOp.Death)
                    (MirrorObserver as IReplicaDeathObserver)?.OnMemberDied(scope, record.Key);
                MirrorObserver?.OnMemberRemoved(scope, record.Key);
            }
        }

        System.Threading.Interlocked.Increment(ref appliedTotal);
        ProcessPendingDigests(scope, state);
        return true;
    }

    /// <summary>
    /// Applies one absolute state batch. Every record may only touch an identity that exists.
    /// </summary>
    /// <remarks>
    /// HP arrives absolute; there is no delta for the replica to accumulate and no "full heal" a
    /// client can claim. A record for an unknown key aborts the batch and flags the scope: the
    /// spawn it depends on was lost, and guessing the object into existence is the exact failure
    /// mode the design forbids.
    /// </remarks>
    public bool ApplyWorldState(ScopeKey scope, long subscriptionEpoch, long streamSequence,
        long declaredBaselineId, IReadOnlyList<AuthorityWorldStateRecord> records)
    {
        if (records == null) throw new ArgumentNullException(nameof(records));
        if (!AcceptStream(scope, subscriptionEpoch, streamSequence, out var state)) return false;

        var hasDelta = false;
        for (var i = 0; i < records.Count; i++) if (records[i].IsDelta) { hasDelta = true; break; }
        if (hasDelta)
        {
            // Reconstruct the whole batch before advancing an object revision or notifying a binding.
            var expanded = new List<AuthorityWorldStateRecord>(records.Count);
            var staged = new Dictionary<ObjectKey, AuthorityWorldStateRecord>(records.Count);
            foreach (var record in records)
            {
                var canonical = record.State;
                byte[] previous = null;
                long previousRevision = 0;
                if (mirrors.TryGetValue(record.Key, out var mirror))
                { previous = mirror.CanonicalState; previousRevision = mirror.Revision; }
                if (staged.TryGetValue(record.Key, out var pending) && pending.Revision > previousRevision)
                { previous = pending.State; previousRevision = pending.Revision; }
                var advances = record.Revision > previousRevision && !state.IsTombstoned(record.Key);
                if (record.IsDelta && advances)
                {
                    if (declaredBaselineId != state.BaselineId || previousRevision != record.BaseRevision || previous == null)
                    { RequestResync(scope, ScopeRecoveryReason.BaselineRefused); return false; }
                    try
                    {
                        canonical = NebulaeModel.Networking.FieldDeltaCodec.Apply(previous,
                            AuthorityStateFields.Describe(record.Key.Kind, previous), record.State, AuthorityLimits.StateRecordMaxBytes);
                        if (AuthorityStateFields.Describe(record.Key.Kind, canonical) == null)
                            throw new System.IO.InvalidDataException("Invalid reconstructed state.");
                    }
                    catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException)
                    { RequestResync(scope, ScopeRecoveryReason.BaselineRefused); return false; }
                }
                var full = new AuthorityWorldStateRecord(record.Key, record.Revision, canonical);
                expanded.Add(full);
                if (advances) staged[record.Key] = full;
            }
            records = expanded;
        }

        foreach (var record in records)
        {
            var result = declaredBaselineId == state.BaselineId
                ? state.ApplyState(record.Key, record.Revision)
                : state.ApplyState(record.Key, record.Revision, declaredBaselineId);

            if (!RecordAndContinue(scope, result)) return false;

            if (result != ReplicaApplyResult.Applied) continue;
            if (mirrors.TryGetValue(record.Key, out var mirror))
            {
                mirror.Revision = record.Revision;
                mirror.CanonicalState = record.State;
            }
            else
            {
                // The version state accepted the revision but no lifecycle message established the
                // mirror (e.g. it was installed by a baseline). Create it rather than drop facts.
                mirrors[record.Key] = new ReplicaObjectMirror(scope, record.Key, record.Revision, record.State);
            }
            // The mirror holds the record, so the adapter may now mirror it into game structures —
            // still inside the apply call the session opened for this message.
            MirrorObserver?.OnStateApplied(scope, record.Key, record.Revision, record.State);
        }

        // The host's stream-resume marker is an empty batch: applying it means every backlog
        // increment before it has been applied, which is what moves the scope to Live.
        if (records.Count == 0 && state.Phase == SubscriptionPhase.CatchingUp)
        {
            state.SetPhase(SubscriptionPhase.Live);
        }

        System.Threading.Interlocked.Increment(ref appliedTotal);
        ProcessPendingDigests(scope, state);
        return true;
    }

    /// <summary>Records a client-local component binding for one mirrored object.</summary>
    /// <remarks>Adapters call this after rebinding their own pools; the wire never supplies it.</remarks>
    public bool SetLocalComponentBinding(ObjectKey key, int componentId)
    {
        if (!mirrors.TryGetValue(key, out var mirror)) return false;
        mirror.LocalComponentId = componentId;
        return true;
    }

    /// <summary>Records a client-local renderer handle for one mirrored object.</summary>
    public bool SetLocalRendererBinding(ObjectKey key, int rendererId)
    {
        if (!mirrors.TryGetValue(key, out var mirror)) return false;
        mirror.LocalRendererId = rendererId;
        return true;
    }

    private bool AcceptStream(ScopeKey scope, long subscriptionEpoch, long streamSequence,
        out ScopeVersionState state)
    {
        if (!Versions.TryGetScope(scope, out state) ||
            state.Phase == SubscriptionPhase.Unsubscribed)
        {
            System.Threading.Interlocked.Increment(ref sequenceDroppedTotal);
            return false;
        }

        if (state.Phase == SubscriptionPhase.Snapshotting || state.Phase == SubscriptionPhase.Installing)
        {
            // The host must not stream live facts before its baseline was acked. Receiving one is a
            // protocol violation: refuse and flag rather than apply half a world.
            System.Threading.Interlocked.Increment(ref streamBeforeBaselineTotal);
            RequestResync(scope, ScopeRecoveryReason.ProtocolViolation);
            return false;
        }

        if (state.Phase == SubscriptionPhase.Resyncing)
        {
            // The scope is suspended for a rebuild: until the new baseline installs, nothing
            // of this stream may write the mirror. The pending resync request already covers it.
            System.Threading.Interlocked.Increment(ref streamSuspendedTotal);
            return false;
        }

        if (state.Phase == SubscriptionPhase.Live && state.BaselineId == 0)
        {
            // A digest-only observation: no baseline exists, so there is no stream to apply
            // to. The host never sends one to an observer; receiving one is a protocol violation.
            System.Threading.Interlocked.Increment(ref streamBeforeBaselineTotal);
            return false;
        }

        // The subscription epoch rides on the packet, so the gate is the single ordering authority
        // and the tail of a superseded subscription is refusable by its own value.
        var result = state.AcceptSequence(subscriptionEpoch, streamSequence);
        switch (result)
        {
            case SequenceResult.Accepted:
                return true;
            case SequenceResult.Duplicate:
                System.Threading.Interlocked.Increment(ref duplicateTotal);
                return false;
            case SequenceResult.Gap:
                System.Threading.Interlocked.Increment(ref gapTotal);
                RequestResync(scope, ScopeRecoveryReason.StreamGap);
                return false;
            case SequenceResult.WrongSubscriptionEpoch:
            case SequenceResult.UnknownScope:
                System.Threading.Interlocked.Increment(ref sequenceDroppedTotal);
                return false;
            default:
                System.Threading.Interlocked.Increment(ref invalidTotal);
                RequestResync(scope, ScopeRecoveryReason.ProtocolViolation);
                return false;
        }
    }

    /// <summary>
    /// Records one record's outcome and decides whether the batch may continue.
    /// </summary>
    /// <remarks>
    /// Duplicates, stale revisions and tombstoned keys are per-record facts, not stream failures:
    /// they are counted and the batch continues. Unknown objects and baseline mismatches mean the
    /// batch's world assumptions are broken — the batch stops and the scope is flagged for resync.
    /// </remarks>
    private bool RecordAndContinue(ScopeKey scope, ReplicaApplyResult result)
    {
        switch (result)
        {
            case ReplicaApplyResult.Applied:
            case ReplicaApplyResult.Duplicate:
            case ReplicaApplyResult.Stale:
            case ReplicaApplyResult.RejectedTombstone:
                if (result == ReplicaApplyResult.Duplicate) System.Threading.Interlocked.Increment(ref duplicateTotal);
                if (result == ReplicaApplyResult.Stale) System.Threading.Interlocked.Increment(ref staleTotal);
                if (result == ReplicaApplyResult.RejectedTombstone)
                {
                    System.Threading.Interlocked.Increment(ref tombstoneRefusedTotal);
                }
                return true;
            case ReplicaApplyResult.RejectedUnknownObject:
                System.Threading.Interlocked.Increment(ref unknownObjectTotal);
                RequestResync(scope, ScopeRecoveryReason.UnknownObject);
                return false;
            case ReplicaApplyResult.RejectedBaseline:
                System.Threading.Interlocked.Increment(ref baselineRefusedTotal);
                RequestResync(scope, ScopeRecoveryReason.BaselineRefused);
                return false;
            default:
                System.Threading.Interlocked.Increment(ref invalidTotal);
                RequestResync(scope, ScopeRecoveryReason.ProtocolViolation);
                return false;
        }
    }

}
