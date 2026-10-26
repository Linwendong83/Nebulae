#region

using System;
using System.Collections.Generic;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Logger;
using NebulaeModel.Networking;

#endregion

namespace NebulaeModel.Authority;

/// <summary>
/// The host's read-only view of the canonical world, as the replicator sees it.
/// </summary>
/// <remarks>
/// <para>
/// Domain adapters read vanilla pools through this interface. The replicator has no direct game-type
/// dependencies, so the scan-then-publish loop runs in plain tests.
/// </para>
/// <para>
/// The two reads have deliberately different failure meanings. <see cref="TryReadMembers"/>
/// returning false means "this scope cannot be read right now" and the frame skips it: treating it
/// as an empty scope would publish a wave of fake deaths. Returning true with an empty list is how
/// a scope that really lost all its members is expressed. <see cref="TryReadState"/> returning
/// false means this member has no canonical state to publish (yet), not that it is gone.
/// </para>
/// <para>
/// State blobs must not exceed <see cref="AuthorityLimits.StateRecordMaxBytes"/>; the adapter owns
/// the canonical field selection that keeps them bounded.
/// </para>
/// </remarks>
internal interface IHostWorldView
{
    /// <summary>Reads the current member keys of a scope into <paramref name="members"/>.</summary>
    bool TryReadMembers(ScopeKey scope, List<ObjectKey> members);

    /// <summary>Reads the canonical state of one member, or false when it has none.</summary>
    bool TryReadState(ObjectKey key, out byte[] state);
}

/// <summary>Native event candidates plus exact retired identities; false requests a full audit.</summary>
internal interface IHostIncrementalWorldView
{
    bool TryReadChanges(ScopeKey scope, long hostTick, List<ObjectKey> candidates, List<ObjectKey> removed);
    void StateSampled(in ObjectKey key);
}

internal interface IHostSnapshotWorldView
{
    bool TryReadSnapshotState(ObjectKey key, out byte[] state);
}

/// <summary>Scratch native references live for one parked-worker capture, never for an entire tick.</summary>
internal interface IHostCaptureBatchWorldView
{
    void BeginCapture(ScopeKey scope);
    void EndCapture();
}

/// <summary>Receives the packets the replicator produces for one subscriber.</summary>
/// <remarks>The network sink forwards packets to the subscriber's connection; tests can record them without a
/// socket.</remarks>
internal interface IReplicationSink
{
    void Send(ushort subscriberId, AuthorityEnvelopePacket packet);
}

internal interface IDeferredReplicationSink : IReplicationSink
{
    void SendWorldStates(ushort subscriberId, Func<AuthorityWorldStatePacket> factory);
}

/// <summary>
/// Publishes native identity changes and canonical decisions at the completed frame boundary.
/// </summary>
/// <remarks>
/// <para>
/// Incremental sources supply candidates from native commits and periodically audit the world.
/// Candidates are read and compared with the last published bytes; a hint is never itself a fact.
/// Full-scan sources use the same identity and publication rules.
/// </para>
/// <para>
/// Ordering rules the method maintains: within one frame, all spawns are published before any
/// state, and states before despawns, so a subscriber never sees HP for an identity it has not
/// been given. An object that both spawns and dies inside one frame is published as spawn then
/// despawn — the subscriber's tombstone lands on the key that actually died. A recycled pool slot
/// is a different key (new generation) and is never merged with the old object's history.
/// </para>
/// <para>
/// Everything here runs at the quiescent frame boundary — capture, subscription and
/// delivery alike — so the state needs no locking and the log order is the delivery order.
/// </para>
/// </remarks>
internal sealed class HostWorldReplicator
{
    private readonly AuthorityEpoch epoch;
    private readonly IHostWorldView worldView;
    private readonly IHostDeathView deathView;
    private readonly IReplicationSink sink;
    private readonly int logCapacity;
    private readonly Dictionary<ScopeKey, ScopeReplicationState> scopes = [];
    private readonly List<ObjectKey> memberScratch = [];
    private readonly HashSet<ObjectKey> memberSetScratch = [];
    private readonly List<ScopeEvent> pendingScratch = [];
    private readonly List<ObjectKey> despawnScratch = [];
    private readonly List<ScopeDigestMember> digestScratch = [];
    private readonly HashSet<int> interestedStars = [];
    private readonly List<(ushort Player, long Request)> recoveryScratch = [];
    private long lastCaptureTick;
    private long eventsAppended;
    private long scopesSkipped;
    private long oversizedStatesRefused;
    private long coldScopeFrames;
    private long baselineCounter;
    private long subscriptionEpochCounter;
    private long snapshotsSent;
    private long snapshotRefusedTotal;
    private long acksRejected;
    private long acksUnknown;
    private long eventsTrimmed;
    private long lastDigestTick;

    /// <param name="epoch">World epoch every published key and envelope carries.</param>
    /// <param name="worldView">The canonical world source.</param>
    /// <param name="sink">Where produced packets are handed for delivery.</param>
    /// <param name="deathView">
    /// Which vanished members died. Without it every removal is published as a plain despawn, which
    /// is the fail-closed direction: a client that never hears about a death shows no wreckage,
    /// while one that invents deaths would explode units that merely went home.
    /// </param>
    /// <param name="logCapacity">Retained events per scope before a lagging subscriber needs a
    /// baseline.</param>
    public HostWorldReplicator(AuthorityEpoch epoch, IHostWorldView worldView, IReplicationSink sink,
        int logCapacity = 4096, IHostDeathView deathView = null)
    {
        if (!epoch.IsValid) throw new ArgumentException("HostWorldReplicator needs a valid epoch.", nameof(epoch));
        this.epoch = epoch;
        this.worldView = worldView ?? throw new ArgumentNullException(nameof(worldView));
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.logCapacity = logCapacity;
        this.deathView = deathView;
        Revisions = new ObjectRevisionTracker();
    }

    /// <summary>Revision minting for this world. One source, per key with its generation.</summary>
    public ObjectRevisionTracker Revisions { get; }

    /// <summary>Only absolute combat-state samples are paced. Membership and death stay per-frame.</summary>
    public int StateIntervalTicks { get; set; } = 1;

    /// <summary>
    /// Per-key sampling cadence, consulted only for a scope with an engaged subscriber.
    /// </summary>
    /// <remarks>
    /// The override refines the cadence in one direction: it may slow a member down, never speed it
    /// up. An event-driven member already samples every tick, so the smallest interval the override
    /// can produce for one is the one it already had; a non-event-driven member uses the override in
    /// place of <see cref="StateIntervalTicks"/>. A wiring that wants a kind left alone must answer
    /// with the cadence that kind already has, so changing one kind cannot pace unrelated pools.
    /// </remarks>
    public Func<ObjectKey, int> StateIntervalFor { get; set; }

    /// <summary>Adapters publishing decisions suppress unchanged clocks themselves; do not delay their events.</summary>
    public Func<ObjectKey, bool> EventDrivenStateFor { get; set; }

    /// <summary>
    /// Sampling interval used for a scope none of whose subscribers is fighting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tier is scope-level. Space-enemy interest filters select membership separately and keep
    /// their own delivered-member digests; pacing never changes a subscriber's chosen membership.
    /// </para>
    /// <para>
    /// The host is the side that decides, because it runs the combat simulation and therefore knows
    /// who is fighting; the decision needs no round trip, and a player who starts shooting moves
    /// their scopes back to the hot cadence in the same frame.
    /// </para>
    /// </remarks>
    public int ColdStateIntervalTicks { get; set; } = 15;

    /// <summary>
    /// Whether one subscriber is currently in a fight, from the host's combat simulation. Without a
    /// provider every scope is treated as hot, which is the behaviour this tier refines.
    /// </summary>
    public Func<ushort, bool> EngagementProvider { get; set; }

    /// <summary>Host-accepted player location, latched for each space-enemy subscription.</summary>
    public Func<ushort, int> SubscriberStarProvider { get; set; }
    public Func<ObjectKey, ReplicationInterest> MemberInterestFor { get; set; }

    private bool Filters(ScopeKey scope) => scope.Kind == PoolKind.SpaceEnemy &&
        SubscriberStarProvider != null && MemberInterestFor != null;

    private bool CurrentInterest(ushort subscriber, SubscriberCursor cursor) =>
        cursor.VisibleMembers == null || cursor.InterestStarId == SubscriberStarProvider(subscriber);

    public int ScopeCount => scopes.Count;

    public long EventsAppended => eventsAppended;

    /// <summary>Frames a scope could not be read. Persistent growth means the adapter is broken.</summary>
    public long ScopesSkipped => scopesSkipped;

    /// <summary>Frames refused because a member's canonical state exceeded the record ceiling.</summary>
    public long OversizedStatesRefused => oversizedStatesRefused;

    /// <summary>
    /// Scope-frames captured at the cold cadence because no subscriber was fighting.
    /// </summary>
    public long ColdScopeFrames => coldScopeFrames;

    /// <summary>Baselines frozen and sent to a subscriber.</summary>
    public long SnapshotsSent => snapshotsSent;

    /// <summary>Subscriptions refused a baseline (unreadable scope, oversized snapshot).</summary>
    public long SnapshotRefusedTotal => snapshotRefusedTotal;
    public long AcksRejected => acksRejected;

    /// <summary>Acks for no pending baseline. Either a replay or a bookkeeping bug.</summary>
    public long AcksUnknown => acksUnknown;

    /// <summary>Log events reclaimed because every subscriber was covered by a baseline or delivery.</summary>
    public long EventsTrimmed => eventsTrimmed;

    /// <summary>
    /// Starts one subscriber's subscription of a scope with a frozen baseline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A new or broken subscription never receives a join burst: the host freezes the scope's
    /// membership and canonical state at the current log position, sends it as
    /// Begin/Chunk/Commit, and holds every stream packet until the subscriber's ack reports the
    /// baseline installed. The backlog of events that happened while the client was installing is
    /// delivered after the ack, in log order, so no event is either lost or applied early.
    /// </para>
    /// <para>
    /// Re-subscribing an active, healthy subscriber resumes its stream instead: no new baseline,
    /// no sequence reset. A subscriber whose stream broke recovers only through a fresh baseline —
    /// the next call replaces its cursor with a new subscription epoch, which is what makes the
    /// old stream's tail refusable.
    /// </para>
    /// </remarks>
    public void Subscribe(ushort subscriberId, ScopeKey scope, bool digestOnly = false, long requestId = 0)
    {
        var state = GetOrCreateScope(scope);

        // A digest-only observation holds no baseline and no stream: its cursor exists only so the
        // periodic digest knows where to send the summary. Re-issuing it is a no-op; upgrading to a
        // full subscription replaces the cursor through the normal path below.
        if (digestOnly)
        {
            if (!state.TryGetSubscriber(subscriberId, out var existing) || !existing.DigestOnly)
            {
                state.AddSubscriber(subscriberId, 0, digestOnly: true);
                if (state.TryGetSubscriber(subscriberId, out var observer))
                    observer.InterestStarId = SubscriberStarProvider?.Invoke(subscriberId) ?? 0;
            }
            return;
        }

        if (CountScopesFor(subscriberId) >= AuthorityLimits.ScopesPerSubscriberMax &&
            !(state.TryGetSubscriber(subscriberId, out var held) && held.Active))
        {
            // A subscriber cannot grow its delivery surface without bound; the request is refused
            // visibly instead of silently inflating every later capture.
            snapshotRefusedTotal++;
            Diagnostics.Warn("host:subscription-limit", () => $"[authority] subscription limit refused player={subscriberId} scope={scope} refused={snapshotRefusedTotal}");
            return;
        }

        if (Filters(scope) && state.TryGetSubscriber(subscriberId, out var previous) &&
            (previous.InterestStarId != SubscriberStarProvider(subscriberId) || previous.RequestId != requestId))
            Unsubscribe(subscriberId, scope);

        // Coalesce ordinary subscription retries while a baseline is in flight. A client that
        // stops making progress explicitly requests resync, which replaces this conversation.
        if (state.TryGetSubscriber(subscriberId, out var inFlight) && inFlight.PendingBaselineId != 0)
        {
            return;
        }

        if (state.TryGetSubscriber(subscriberId, out var cursor) && cursor.Active &&
            !cursor.NeedsResync && cursor.PendingBaselineId == 0)
        {
            DeliverScope(state);
            TrimCoveredLog(state);
            return;
        }

        if (!ReadMembers(state, out var current))
        {
            // The scope cannot be read; refuse to serve it rather than present an empty world.
            state.AddSubscriber(subscriberId, 0);
            state.TryGetSubscriber(subscriberId, out cursor);
            cursor.NeedsResync = true;
            cursor.DeltaStates.Clear();
            return;
        }

        StartSnapshotSubscription(state, subscriberId, current, requestId);
    }

    /// <summary>Counts the scopes a subscriber holds an active cursor for.</summary>
    private int CountScopesFor(ushort subscriberId)
    {
        var count = 0;
        foreach (var state in scopes.Values)
        {
            if (state.TryGetSubscriber(subscriberId, out var cursor) && cursor.Active) count++;
        }
        return count;
    }

    /// <summary>
    /// Answers a replica's resync request: the subscription is marked broken and immediately
    /// re-baselined with a new subscription epoch, so the old stream's tail stays refusable.
    /// </summary>
    /// <remarks>
    /// A baseline still in flight is discarded first, not awaited. The client only acks a baseline it
    /// installed, so a broken conversation leaves <c>PendingBaselineId</c> set forever; a resync is
    /// exactly the signal that the conversation is dead, and without clearing the marker the host
    /// would refuse every later recovery.
    /// </remarks>
    public bool RequestResync(ushort subscriberId, ScopeKey scope, long requestId = 0)
    {
        if (!scopes.TryGetValue(scope, out var state) ||
            !state.TryGetSubscriber(subscriberId, out var cursor) ||
            !cursor.Active)
        {
            return false;
        }
        cursor.NeedsResync = true;
        cursor.DeltaStates.Clear();
        cursor.PendingBaselineId = 0;
        Subscribe(subscriberId, scope, requestId: requestId);
        return true;
    }

    /// <summary>
    /// Publishes every scope's canonical digest to its subscribers when the interval is due.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unfiltered scopes share one digest; filtered scopes digest each subscriber's delivered member
    /// table. Both are stamped with the subscriber's stream position and baseline. A subscriber still
    /// installing its baseline receives none: the replica drops digests outside Live anyway, so
    /// sending one would only be dropped twice.
    /// </para>
    /// <para>
    /// The interval is a duty cycle, not a deadline: the check runs at the same post-frame safe
    /// point as capture, and a quiet period simply skips. Scopes with no active subscriber are not
    /// digested at all — a digest nobody reads is pure cost.
    /// </para>
    /// </remarks>
    public void PublishDigests(long hostTick, long intervalTicks)
    {
        if (intervalTicks <= 0) return;
        if (lastDigestTick != 0 && hostTick - lastDigestTick < intervalTicks) return;
        lastDigestTick = hostTick;

        foreach (var state in scopes.Values)
        {
            var hasListener = false;
            foreach (var pair in state.Subscribers)
            {
                if (pair.Value.Active && pair.Value.PendingBaselineId == 0 && !pair.Value.NeedsResync)
                {
                    hasListener = true;
                    break;
                }
            }
            if (!hasListener) continue;

            var memberCount = 0;
            var digest = 0UL;
            if (!Filters(state.Scope)) BuildDigest(state.Members, null, out memberCount, out digest);

            foreach (var pair in state.Subscribers)
            {
                var cursor = pair.Value;
                if (!cursor.Active || cursor.PendingBaselineId != 0 || cursor.NeedsResync) continue;
                if (!CurrentInterest(pair.Key, cursor)) continue;
                if (Filters(state.Scope))
                    BuildDigest(cursor.VisibleMembers ?? state.Members,
                        cursor.DigestOnly ? cursor.InterestStarId : (int?)null, out memberCount, out digest);
                Send(pair.Key, AuthorityScopeDigestPacket.Create(
                    ControlHeader(AuthorityFamily.ScopeDigest, hostTick), state.Scope,
                    cursor.SubscriptionEpoch, cursor.BaselineId, cursor.NextStreamSequence - 1,
                    memberCount, digest));
            }
        }
    }

    private void BuildDigest(Dictionary<ObjectKey, HostMemberRecord> members, int? starId,
        out int count, out ulong digest)
    {
        digestScratch.Clear();
        foreach (var pair in members)
            if (!starId.HasValue || pair.Value.Interest.Includes(starId.Value))
                digestScratch.Add(new ScopeDigestMember(pair.Key, pair.Value.Revision, pair.Value.State));
        count = digestScratch.Count;
        digest = ScopeDigestComputer.Compute(digestScratch);
    }

    /// <summary>Stops one subscriber's stream of a scope, keeping the cursor for continuity.</summary>
    public void Unsubscribe(ushort subscriberId, ScopeKey scope)
    {
        if (scopes.TryGetValue(scope, out var state) &&
            state.TryGetSubscriber(subscriberId, out var cursor))
        {
            cursor.Active = false;
            cursor.DeltaStates.Clear();
            cursor.VisibleMembers?.Clear();
            // The old baseline may never have been ACKed. Leaving the scope abandons that
            // conversation; a later subscription must snapshot the world as it is then.
            cursor.PendingBaselineId = 0;
            cursor.NeedsResync = true;
        }
    }

    /// <summary>Removes a subscriber from every scope. Used when its connection is gone.</summary>
    public void RemoveSubscriber(ushort subscriberId)
    {
        foreach (var state in scopes.Values)
        {
            state.RemoveSubscriber(subscriberId);
        }
    }

    /// <summary>True when the subscriber's stream of this scope is waiting for a baseline.</summary>
    public bool NeedsResync(ushort subscriberId, ScopeKey scope) =>
        scopes.TryGetValue(scope, out var state) &&
        state.TryGetSubscriber(subscriberId, out var cursor) &&
        cursor.NeedsResync;

    public bool HasLiveSubscription(ushort subscriberId, ScopeKey scope) =>
        scopes.TryGetValue(scope, out var state) && state.TryGetSubscriber(subscriberId, out var cursor) &&
        cursor.Active && cursor.PendingBaselineId == 0 && !cursor.NeedsResync && !cursor.DigestOnly;

    /// <summary>
    /// One frame-end canonical scan: diff every subscribed scope against the world, then deliver.
    /// </summary>
    /// <remarks>
    /// Everything a frame produced is delivered in that same frame, in log order, so the visible
    /// transaction stays atomic: a lifecycle batch and the states after it are never reordered, and
    /// nothing waits for a later frame. Timing wraps the whole pass so the reported capture cost is
    /// the whole pass, not a subset of it.
    /// </remarks>
    public void CaptureFrame(long hostTick)
    {
        lastCaptureTick = hostTick;
        foreach (var pair in scopes)
        {
            if (!HasActiveSubscriber(pair.Value)) continue;
            CaptureScope(pair.Value, hostTick);
            DeliverScope(pair.Value);
            TrimCoveredLog(pair.Value);
        }
    }

    /// <summary>Sends one chunk of a baseline image, addressed by its index in that image.</summary>
    private void SendChunk(ushort subscriberId, ScopeKey scope, byte[] image, long baselineId,
        long subscriptionEpoch, int index)
    {
        var offset = (long)index * AuthorityLimits.ChunkMaxBytes;
        var length = (int)Math.Min(AuthorityLimits.ChunkMaxBytes, image.Length - offset);
        var chunk = new byte[length];
        Buffer.BlockCopy(image, (int)offset, chunk, 0, length);
        Send(subscriberId, AuthoritySnapshotChunkPacket.Create(
            ControlHeader(AuthorityFamily.SnapshotChunk, lastCaptureTick), scope,
            baselineId, subscriptionEpoch, index, chunk));
    }

    /// <summary>
    /// Applies a subscriber's snapshot acknowledgement: the baseline was installed (or refused) and
    /// the stream may resume with the backlog the subscriber has not seen.
    /// </summary>
    /// <remarks>
    /// An accepted ack delivers the backlog first, then a zero-record state batch that tells the
    /// replica "everything before this position is applied" — the marker the replica leaves
    /// CatchingUp on. A rejected ack stops the subscription (fail-closed): the recovery is a new
    /// subscription with a new baseline, not a retry over the same bytes.
    /// </remarks>
    public void OnSnapshotAck(ushort subscriberId, long baselineId, long lastAppliedSequence, bool accepted)
    {
        foreach (var state in scopes.Values)
        {
            if (!state.TryGetSubscriber(subscriberId, out var cursor) ||
                cursor.PendingBaselineId != baselineId)
            {
                continue;
            }

            cursor.PendingBaselineId = 0;
            if (!accepted)
            {
                Diagnostics.Warn("host:ack-refused:" + subscriberId + ":" + state.Scope, () => $"[authority] snapshot ACK rejected player={subscriberId} scope={state.Scope} baseline={baselineId} sequence={lastAppliedSequence}");
                cursor.NeedsResync = true;
                cursor.DeltaStates.Clear();
                acksRejected++;
                return;
            }
            if (!CurrentInterest(subscriberId, cursor)) return;
            DeliverScope(state);
            // Delivery may discover that the baseline's tail has already been evicted and
            // replace this cursor with a fresh snapshot. Never acknowledge an incomplete tail.
            if (!cursor.NeedsResync && state.TryGetSubscriber(subscriberId, out var current) &&
                ReferenceEquals(current, cursor) && cursor.PendingBaselineId == 0)
                SendResumeMarker(state, subscriberId, cursor);
            TrimCoveredLog(state);
            return;
        }
        acksUnknown++;
        Log.Debug($"[authority] unmatched snapshot ACK player={subscriberId} baseline={baselineId} sequence={lastAppliedSequence} accepted={accepted}");
    }

    /// <summary>
    /// Freezes the scope at the current log position and sends it as Begin/Chunk/Commit.
    /// </summary>
    private void StartSnapshotSubscription(ScopeReplicationState scope, ushort subscriberId,
        List<ObjectKey> current, long requestId)
    {
        var hasRecipients = false;
        foreach (var subscriber in scope.Subscribers)
            if (!subscriber.Value.DigestOnly && (subscriber.Value.Active || subscriber.Value.PendingBaselineId != 0)) { hasRecipients = true; break; }
        // Publish any newer canonical facts before taking the cut. Updating only the frozen image
        // would silently replace the host cache and leave existing peers on the older value forever.
        var starId = Filters(scope.Scope) ? SubscriberStarProvider(subscriberId) : 0;
        if (!CaptureScope(scope, lastCaptureTick, current, seedOnly: !hasRecipients,
                snapshotStarId: Filters(scope.Scope) ? starId : (int?)null))
        {
            snapshotRefusedTotal++;
            Diagnostics.Warn("host:snapshot-read:" + scope.Scope, () => $"[authority] snapshot capture unavailable player={subscriberId} scope={scope.Scope} tick={lastCaptureTick}");
            return;
        }
        var startLogSequence = scope.NextLogSequence - 1;
        scope.AddSubscriber(subscriberId, startLogSequence);
        if (!scope.TryGetSubscriber(subscriberId, out var cursor))
        {
            // AddSubscriber just created this cursor; reaching here would be a defect, and
            // proceeding without a cursor would strand the subscription unacked.
            snapshotRefusedTotal++;
            Diagnostics.Error("host:missing-cursor", () => $"[authority] snapshot cursor missing player={subscriberId} scope={scope.Scope}");
            return;
        }

        var baselineId = ++baselineCounter;
        cursor.SubscriptionEpoch = ++subscriptionEpochCounter;
        cursor.StartLogSequence = startLogSequence;
        cursor.LastLogSequence = startLogSequence;
        cursor.BaselineId = baselineId;
        cursor.PendingBaselineId = baselineId;
        cursor.InterestStarId = starId;
        cursor.RequestId = requestId;
        if (Filters(scope.Scope)) cursor.VisibleMembers = new Dictionary<ObjectKey, HostMemberRecord>();

        // Freeze: the member table is completed from the world view at this safe point, so the
        // baseline is a full statement — identity and current state for every current member. The
        // frozen state becomes the member's last published record, which keeps the post-install
        // scan quiet until something really changes.
        var members = new List<SnapshotMemberRecord>(cursor.VisibleMembers == null ? current.Count : 0);
        foreach (var key in current)
        {
            if (!scope.Members.TryGetValue(key, out var record))
            {
                record = new HostMemberRecord(Revisions.Next(key), null);
                scope.Members[key] = record;
            }
            if (cursor.VisibleMembers != null && !record.Interest.Includes(starId)) continue;
            members.Add(new SnapshotMemberRecord(key, record.Revision, record.State));
            cursor.DeltaStates[key] = new HostMemberRecord(record.Revision, record.State);
            if (cursor.VisibleMembers != null)
                cursor.VisibleMembers[key] = new HostMemberRecord(record.Revision, record.State, record.Interest);
        }

        var frozen = new FrozenScopeSnapshot(scope.Scope, baselineId, cursor.SubscriptionEpoch,
            startLogSequence, lastCaptureTick, members);
        if (!AuthoritySnapshotCodec.TryEncode(frozen, out var image))
        {
            // The adapter handed out state that cannot be shipped as one baseline. The
            // subscription stops here rather than sending a partial world (fail-closed).
            scope.RemoveSubscriber(subscriberId);
            snapshotRefusedTotal++;
            Diagnostics.Error("host:snapshot-encode:" + scope.Scope, () => $"[authority] snapshot encoding refused player={subscriberId} scope={scope.Scope} baseline={baselineId} members={members.Count} maxBytes={AuthorityLimits.ScopeSnapshotMaxBytes}");
            return;
        }

        var hash = AuthoritySnapshotCodec.Hash(image);
        var chunkCount = AuthoritySnapshotCodec.ChunkCountFor(image.Length);

        var begin = AuthoritySnapshotBeginPacket.Create(
            ControlHeader(AuthorityFamily.SnapshotBegin, lastCaptureTick), scope.Scope, baselineId,
            cursor.SubscriptionEpoch, startLogSequence, image.Length, chunkCount, hash);
        begin.RequestId = requestId;
        Send(subscriberId, begin);

        // The whole baseline ships in this frame: Begin, every chunk, then Commit. Nothing is held
        // back for a later frame, so the conversation is ordered end to end and a large baseline
        // costs one frame's send queue rather than a queue that outlives it.
        for (var index = 0; index < chunkCount; index++)
        {
            SendChunk(subscriberId, scope.Scope, image, baselineId, cursor.SubscriptionEpoch, index);
        }
        Send(subscriberId, AuthoritySnapshotCommitPacket.Create(
            ControlHeader(AuthorityFamily.SnapshotCommit, lastCaptureTick), scope.Scope,
            baselineId, cursor.SubscriptionEpoch, chunkCount, hash));
        snapshotsSent++;
        Log.Debug($"[authority] snapshot sent player={subscriberId} scope={scope.Scope} baseline={baselineId} subscription={cursor.SubscriptionEpoch} members={members.Count} bytes={image.Length} chunks={chunkCount}");
    }

    /// <summary>
    /// A zero-record state batch that ends the backlog: applying it is how the replica's scope
    /// leaves CatchingUp for Live.
    /// </summary>
    private void SendResumeMarker(ScopeReplicationState scope, ushort subscriberId, SubscriberCursor cursor)
    {
        if (!AuthorityWorldStateCodec.TryEncode([], out var data))
        {
            throw new InvalidOperationException("An empty state batch cannot fail its own codec.");
        }
        Send(subscriberId, Stamped(AuthorityWorldStatePacket.Create(
            Header(AuthorityFamily.WorldState, cursor, lastCaptureTick, data.Length),
            scope.Scope, cursor.BaselineId, recordCount: 0, data), cursor));
    }

    /// <summary>Stamps the subscription epoch onto a stream packet before delivery.</summary>
    private static AuthorityEnvelopePacket Stamped<TPacket>(TPacket packet, SubscriberCursor cursor)
        where TPacket : AuthorityEnvelopePacket
    {
        packet.SubscriptionEpoch = cursor.SubscriptionEpoch;
        return packet;
    }

    /// <summary>
    /// Header for the snapshot control families. They are not part of the per-scope stream
    /// sequence space: the conversation is Begin → chunks → Commit → Ack, and each message is
    /// complete on its own, so no stream position is consumed.
    /// </summary>
    private AuthorityEnvelopeHeader ControlHeader(AuthorityFamily family, long hostTick) =>
        new(family, epoch, connection: default, sequence: 0, hostTick,
            claimedPlayerId: 0, payloadLength: 0);

    /// <summary>
    /// Reclaims log events every subscriber is done with: delivered, or covered by the baseline
    /// they are currently installing. This is the deterministic form of tombstone/log reclamation —
    /// bound to the baseline cutoff and to delivery, never to a timer.
    /// </summary>
    private void TrimCoveredLog(ScopeReplicationState scope)
    {
        // With nobody listening the log is dead weight; a later subscriber always arrives through
        // a fresh baseline, never through the log's history.
        var firstRetained = scope.NextLogSequence;
        foreach (var pair in scope.Subscribers)
        {
            var cursor = pair.Value;
            // A digest-only subscriber holds no log position, so it must never pin reclamation.
            if (!cursor.Active || cursor.NeedsResync || cursor.DigestOnly) continue;
            var coveredThrough = cursor.PendingBaselineId != 0 ? cursor.StartLogSequence : cursor.LastLogSequence;
            firstRetained = Math.Min(firstRetained, coveredThrough + 1);
        }
        eventsTrimmed += scope.TrimEventsBefore(firstRetained);
    }

    private bool CaptureScope(ScopeReplicationState scope, long hostTick, List<ObjectKey> fullMembers = null,
        bool seedOnly = false, int? snapshotStarId = null)
    {
        var batch = worldView as IHostCaptureBatchWorldView;
        try
        {
            batch?.BeginCapture(scope.Scope);
            return CaptureScopeCore(scope, hostTick, fullMembers, seedOnly, snapshotStarId);
        }
        finally { batch?.EndCapture(); }
    }

    private bool CaptureScopeCore(ScopeReplicationState scope, long hostTick, List<ObjectKey> fullMembers,
        bool seedOnly, int? snapshotStarId)
    {
        var filtered = Filters(scope.Scope);
        if (filtered)
        {
            interestedStars.Clear();
            if (snapshotStarId.HasValue) interestedStars.Add(snapshotStarId.Value);
            foreach (var pair in scope.Subscribers)
            {
                var cursor = pair.Value;
                if (cursor.Active && !cursor.DigestOnly && !cursor.NeedsResync && CurrentInterest(pair.Key, cursor))
                    interestedStars.Add(cursor.InterestStarId);
            }
        }
        var current = fullMembers;
        despawnScratch.Clear();
        var incremental = false;
        if (current == null && worldView is IHostIncrementalWorldView changes)
        {
            memberScratch.Clear();
            incremental = changes.TryReadChanges(scope.Scope, hostTick, memberScratch, despawnScratch);
            if (incremental) current = memberScratch;
        }
        if (current == null && !ReadMembers(scope, out current)) return false;
        if (!incremental)
        {
            despawnScratch.Clear();
            memberSetScratch.Clear();
            foreach (var key in current) memberSetScratch.Add(key);
            foreach (var key in scope.Members.Keys)
                if (!memberSetScratch.Contains(key)) despawnScratch.Add(key);
        }

        // 1. Spawns first: identity precedes every HP this frame will publish.
        foreach (var key in current)
        {
            if (scope.Members.ContainsKey(key)) continue;
            var revision = Revisions.Next(key);
            var interest = MemberInterestFor?.Invoke(key) ?? default;
            scope.Members[key] = new HostMemberRecord(revision, null, interest);
            if (!seedOnly) Append(scope, new ScopeEvent(scope.NextLogSequence, ScopeEventKind.Spawn, key, revision, hostTick,
                null, interest));
        }

        // 2. Absolute state for members whose canonical bytes changed. The comparison is against
        //    the last published bytes, not against a hook's claim, so nothing escapes by being
        //    unwired.
        // a scope nobody is fighting is sampled at the cold cadence. The tier is
        //    scope-level, so delivery stays complete for every subscriber and the digest remains
        //    comparable at the position it declares.
        var hot = HasEngagedSubscriber(scope);
        if (!hot && fullMembers == null) coldScopeFrames++;
        foreach (var key in current)
        {
            if (!scope.Members.TryGetValue(key, out var record)) continue;
            if (filtered && !(record.Interest.StarId == 0 ? interestedStars.Count > 0 : interestedStars.Contains(record.Interest.StarId)))
            {
                (worldView as IHostIncrementalWorldView)?.StateSampled(key);
                continue;
            }
            // New identities always get their first state immediately, including pool reuse.
            //
            // An event-driven adapter suppresses unchanged records itself, so its cadence starts
            // at every tick. A per-key override may only slow that down, never speed it up: it
            // exists to throttle members nobody is watching, and the adapter still publishes on the
            // tick a watched member's committed fact changes. Answering the event-driven branch
            // first would make <see cref="StateIntervalFor"/> unreachable for exactly the kinds
            // that most need it, because those kinds are the event-driven ones.
            var interval = 1;
            if (EventDrivenStateFor?.Invoke(key) != true)
            {
                interval = hot
                    ? Math.Max(1, StateIntervalFor?.Invoke(key) ?? StateIntervalTicks)
                    : ColdStateIntervalTicks;
            }
            else if (StateIntervalFor != null)
            {
                var requested = StateIntervalFor(key);
                if (requested > interval) interval = requested;
            }
            // Spread slow refreshes across the interval instead of creating a periodic hitch: at a
            // 20 Hz cadence every member lands on the same tick and produces one large batch.
            var phase = interval >= 8 ? key.NativeId % interval : 0;
            if (fullMembers == null && hostTick % interval != phase && record.State != null) continue;
            byte[] canonical;
            var read = fullMembers != null && worldView is IHostSnapshotWorldView snapshot
                ? snapshot.TryReadSnapshotState(key, out canonical)
                : worldView.TryReadState(key, out canonical);
            // Counted before the read's outcome is known: a member the adapter cannot read yet still
            // costs the pass a lookup, and a skipped read is the saving a cadence change produces.
            if (!read) continue;
            if (canonical.Length > AuthorityLimits.StateRecordMaxBytes)
            {
                // Refuse the whole frame for this scope: publishing the other members would let a
                // silently unpublishable field diverge.
                oversizedStatesRefused++;
                Diagnostics.Error("host:oversized-state:" + scope.Scope, () => $"[authority] state publication refused key={key} bytes={canonical.Length} maxBytes={AuthorityLimits.StateRecordMaxBytes} tick={hostTick}");
                return false;
            }
            if (FieldDeltaCodec.Equal(record.State, canonical))
            {
                (worldView as IHostIncrementalWorldView)?.StateSampled(key);
                continue;
            }
            var revision = seedOnly && record.State == null ? record.Revision : Revisions.Next(key);
            record.Revision = revision;
            record.State = canonical;
            if (!seedOnly)
            {
                Append(scope, new ScopeEvent(scope.NextLogSequence, ScopeEventKind.State, key, revision, hostTick,
                    canonical, record.Interest));
            }
            (worldView as IHostIncrementalWorldView)?.StateSampled(key);
        }

        // 3. Despawns last, with a final revision, so a death can never be overwritten by an older
        //    state still in flight.
        foreach (var key in despawnScratch)
        {
            if (!scope.Members.TryGetValue(key, out var removed)) continue;
            var revision = Revisions.Next(key);
            scope.Members.Remove(key);
            // A member that left the pool is only a death when the frame's rules settled one for it
            // through the Kill path; everything else (a unit going home, a relay that landed) left
            // through the silent removal path and must stay silent on the client too.
            var kind = deathView != null && deathView.IsDead(key) ? ScopeEventKind.Death : ScopeEventKind.Despawn;
            if (!seedOnly) Append(scope, new ScopeEvent(scope.NextLogSequence, kind, key, revision, hostTick,
                null, removed.Interest));
            // The generation is over on the host; the client's tombstone is what refuses any late
            // packet for this key from here on.
            Revisions.Forget(key);
        }
        return true;
    }

    private static bool HasActiveSubscriber(ScopeReplicationState scope)
    {
        foreach (var subscriber in scope.Subscribers)
            if (subscriber.Value.Active) return true;
        return false;
    }

    /// <summary>
    /// Whether any subscriber that receives this scope's stream is currently in a fight.
    /// </summary>
    /// <remarks>
    /// Digest-only observers are ignored: they receive no state events, so their engagement cannot
    /// change what the scope publishes. With no provider the scope is hot, which is the behaviour
    /// this tier refines rather than replaces.
    /// </remarks>
    private bool HasEngagedSubscriber(ScopeReplicationState scope)
    {
        var engagement = EngagementProvider;
        if (engagement == null) return true;
        foreach (var pair in scope.Subscribers)
        {
            if (!pair.Value.Active || pair.Value.DigestOnly) continue;
            if (engagement(pair.Key)) return true;
        }
        return false;
    }

    private void DeliverScope(ScopeReplicationState scope)
    {
        if (scope.LogCount == 0) return;
        recoveryScratch.Clear();
        var receivers = 0;
        foreach (var pair in scope.Subscribers)
            if (pair.Value.Active && !pair.Value.DigestOnly && !pair.Value.NeedsResync && pair.Value.PendingBaselineId == 0) receivers++;
        // Scoped to this fan-out: bytes are immutable, but different subscribers may have different bases.
        var patches = receivers > 1 ? new AuthorityDeltaCache() : null;
        foreach (var pair in scope.Subscribers)
        {
            // A subscriber still installing its baseline receives nothing yet: its backlog is
            // delivered after the ack, so no event is applied ahead of the identity baseline.
            if (pair.Value.PendingBaselineId != 0) continue;
            if (!CurrentInterest(pair.Key, pair.Value)) continue;
            if (pair.Value.NeedsResync) continue;
            if (!scope.TryTakePending(pair.Value, pendingScratch))
            {
                if (pair.Value.NeedsResync) recoveryScratch.Add((pair.Key, pair.Value.RequestId));
                continue;
            }
            DeliverEvents(scope, pair.Key, pair.Value, pendingScratch, patches);
        }
        // Subscribe replaces dictionary entries; do it only after enumerating the subscribers.
        foreach (var recovery in recoveryScratch)
            RequestResync(recovery.Player, scope.Scope, recovery.Request);
    }

    /// <summary>
    /// Turns one subscriber's pending events into lifecycle and state batches, all of them.
    /// </summary>
    /// <remarks>
    /// Every pending event is consumed in this frame, in log order, so the visible transaction is
    /// atomic: a lifecycle batch and the states after it are never reordered, and nothing is held
    /// back for a later frame. A key that changed several times ships every revision in order —
    /// a state record is absolute, so the replica converges either way, and shipping the history
    /// keeps the batch a plain statement of what the log holds.
    /// </remarks>
    private void DeliverEvents(ScopeReplicationState scope, ushort subscriberId, SubscriberCursor cursor,
        List<ScopeEvent> pending, AuthorityDeltaCache patches = null)
    {
        if (pending.Count == 0) return;
        var lifecycle = new List<AuthorityLifecycleRecord>();
        var states = new List<DeferredAuthorityState>();
        var packetTick = pending[0].HostTick;

        foreach (var @event in pending)
        {
            if (cursor.VisibleMembers != null)
            {
                if (@event.Kind == ScopeEventKind.Spawn)
                {
                    if (!@event.Interest.Includes(cursor.InterestStarId)) continue;
                    cursor.VisibleMembers[@event.Key] = new HostMemberRecord(@event.Revision, null, @event.Interest);
                }
                else
                {
                    if (!cursor.VisibleMembers.TryGetValue(@event.Key, out var visible)) continue;
                    if (@event.Kind == ScopeEventKind.State)
                    { visible.Revision = @event.Revision; visible.State = @event.State; }
                    else cursor.VisibleMembers.Remove(@event.Key);
                }
            }
            switch (@event.Kind)
            {
                case ScopeEventKind.Spawn:
                case ScopeEventKind.Despawn:
                case ScopeEventKind.Death:
                    FlushStates();
                    cursor.DeltaStates.Remove(@event.Key);
                    lifecycle.Add(new AuthorityLifecycleRecord(
                        @event.Kind == ScopeEventKind.Spawn ? AuthorityLifecycleOp.Spawn
                            : @event.Kind == ScopeEventKind.Death ? AuthorityLifecycleOp.Death
                            : AuthorityLifecycleOp.Despawn,
                        @event.Key, @event.Revision));
                    if (lifecycle.Count >= AuthorityLimits.LifecycleRecordCountMax) FlushLifecycle();
                    break;
                case ScopeEventKind.State:
                    FlushLifecycle();
                    cursor.DeltaStates.TryGetValue(@event.Key, out var previous);
                    states.Add(new DeferredAuthorityState(@event, previous));
                    if (previous == null) cursor.DeltaStates[@event.Key] = new HostMemberRecord(@event.Revision, @event.State);
                    else { previous.Revision = @event.Revision; previous.State = @event.State; }
                    if (states.Count >= AuthorityLimits.StateRecordCountMax) FlushStates();
                    break;
            }
        }

        FlushLifecycle();
        FlushStates();
        cursor.LastLogSequence = pending[pending.Count - 1].LogSequence;
        return;

        void FlushLifecycle()
        {
            if (lifecycle.Count == 0) return;
            if (!AuthorityLifecycleCodec.TryEncode(lifecycle, out var data))
            {
                // Encoding a fixed-size record batch cannot fail for counts the split enforces;
                // reaching here is a defect, not a delivery condition.
                throw new InvalidOperationException("Lifecycle batch refused by its own codec.");
            }
            Send(subscriberId, Stamped(AuthorityLifecyclePacket.Create(
                Header(AuthorityFamily.Lifecycle, cursor, packetTick, data.Length),
                scope.Scope, lifecycle.Count, data), cursor));
            lifecycle.Clear();
        }

        void FlushStates()
        {
            if (states.Count == 0) return;
            var owned = states.ToArray();
            var header = Header(AuthorityFamily.WorldState, cursor, packetTick, 0);
            var scopeKey = scope.Scope; var baseline = cursor.BaselineId; var subscriptionEpoch = cursor.SubscriptionEpoch;
            AuthorityWorldStatePacket Build() => DeferredAuthorityStates.Build(owned, header, scopeKey, baseline, subscriptionEpoch, patches);
            if (sink is IDeferredReplicationSink deferred)
            {
                deferred.SendWorldStates(subscriberId, Build);
                cursor.NextStreamSequence++;
            }
            else Send(subscriberId, Build());
            states.Clear();
        }
    }

    private AuthorityEnvelopeHeader Header(AuthorityFamily family, SubscriberCursor cursor, long hostTick,
        int payloadLength) =>
        new(family, epoch, connection: default, cursor.NextStreamSequence, hostTick,
            claimedPlayerId: 0, payloadLength);

    private void Send(ushort subscriberId, AuthorityEnvelopePacket packet)
    {
        sink.Send(subscriberId, packet);
        AdvanceStreamSequence(subscriberId, packet);
    }

    /// <summary>
    /// Advances the subscriber's stream sequence after a packet was handed to the sink.
    /// </summary>
    /// <remarks>
    /// The cursor is found through the packet's scope so both packet families share one per-subscriber,
    /// per-scope sequence space, which is what lets the replica's contiguous-sequence gate see the
    /// lifecycle and state packets of one frame as one ordered stream.
    /// </remarks>
    private void AdvanceStreamSequence(ushort subscriberId, AuthorityEnvelopePacket packet)
    {
        var scopeKey = packet switch
        {
            AuthorityLifecyclePacket lifecycle => new ScopeKey((PoolKind)lifecycle.ScopeKind, lifecycle.Scope),
            AuthorityWorldStatePacket worldState => new ScopeKey((PoolKind)worldState.ScopeKind, worldState.Scope),
            _ => default
        };
        if (!scopeKey.IsValid) return;
        if (scopes.TryGetValue(scopeKey, out var state) &&
            state.TryGetSubscriber(subscriberId, out var cursor))
        {
            cursor.NextStreamSequence++;
        }
    }

    private bool ReadMembers(ScopeReplicationState scope, out List<ObjectKey> current)
    {
        memberScratch.Clear();
        if (!worldView.TryReadMembers(scope.Scope, memberScratch))
        {
            scopesSkipped++;
            current = null;
            return false;
        }
        current = memberScratch;
        return true;
    }

    private void Append(ScopeReplicationState scope, in ScopeEvent @event)
    {
        scope.Append(@event);
        eventsAppended++;
    }

    private ScopeReplicationState GetOrCreateScope(ScopeKey scope)
    {
        if (!scopes.TryGetValue(scope, out var state))
        {
            state = new ScopeReplicationState(scope, epoch, logCapacity);
            scopes.Add(scope, state);
        }
        return state;
    }
}
