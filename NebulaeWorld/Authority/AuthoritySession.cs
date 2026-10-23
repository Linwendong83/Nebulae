#region

using System;
using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority.Adapters;

#endregion

namespace NebulaeWorld.Authority;

/// <summary>A replica message waiting for the frame boundary.</summary>
/// <remarks>The packet is retained as sent and its scope is pre-resolved. The receive path never touches world
/// state.</remarks>
internal readonly struct PendingReplicaMessage
{
    public PendingReplicaMessage(AuthorityEnvelopePacket packet, in ApplyScope scope)
    {
        Packet = packet;
        Scope = scope;
    }

    public AuthorityEnvelopePacket Packet { get; }

    public ApplyScope Scope { get; }
}

/// <summary>
/// A snapshot acknowledgement that arrived on the socket thread and waits for the frame boundary.
/// </summary>
internal readonly struct PendingSnapshotAck
{
    public PendingSnapshotAck(ushort playerId, long baselineId, long lastAppliedSequence, bool accepted)
    {
        PlayerId = playerId;
        BaselineId = baselineId;
        LastAppliedSequence = lastAppliedSequence;
        Accepted = accepted;
    }

    /// <summary>Player the ack came from, taken from the connection, never from a claim alone.</summary>
    public ushort PlayerId { get; }

    public long BaselineId { get; }

    public long LastAppliedSequence { get; }

    public bool Accepted { get; }
}

/// <summary>
/// A scope-control request (subscribe/unsubscribe/resync) that arrived on the socket thread and
/// waits for the frame boundary.
/// </summary>
/// <remarks>
/// Subscriber cursors move only at the quiescent frame boundary, so the replicator sees this
/// request at the same safe point as capture and acks. Identity comes from the connection via the
/// processor; the packet's claims were only an assertion.
/// </remarks>
internal readonly struct PendingScopeControl
{
    public PendingScopeControl(ushort playerId, ScopeControlOp op, ScopeKey scope, ScopeRecoveryReason reason,
        bool digestOnly, long subscriptionEpoch, long lastAppliedSequence, int observedStarId = 0, long requestId = 0)
    {
        PlayerId = playerId;
        Op = op;
        Scope = scope;
        Reason = reason;
        DigestOnly = digestOnly;
        SubscriptionEpoch = subscriptionEpoch;
        LastAppliedSequence = lastAppliedSequence;
        ObservedStarId = observedStarId;
        RequestId = requestId;
    }

    public ushort PlayerId { get; }

    public ScopeControlOp Op { get; }

    public ScopeKey Scope { get; }

    public ScopeRecoveryReason Reason { get; }

    public bool DigestOnly { get; }

    public long SubscriptionEpoch { get; }

    public long LastAppliedSequence { get; }
    public int ObservedStarId { get; }
    public long RequestId { get; }
}

/// <summary>Applies one queued message inside a scope-bound replica write window.</summary>
/// <remarks>The interface keeps apply-window exception handling testable without a game process.</remarks>
internal interface IReplicaMessageApplier
{
    /// <summary>
    /// Applies one message. Called only from the frame boundary, inside an open apply scope.
    /// </summary>
    /// <returns>True when the message was applied; false when it was refused as not applicable.</returns>
    bool Apply(in PendingReplicaMessage message);
}

/// <summary>Optional per-frame capture hook installed by the replicator.</summary>
/// <remarks>Capture runs at the quiescent frame boundary and publishes only state supplied by a canonical world
/// view.</remarks>
internal interface IAuthorityFrameCapture
{
    /// <summary>Runs once per host frame boundary, before the frame's rule work.</summary>
    void Capture(long hostTick);
}

/// <summary>Runs host commands and applies client replicas at world-frame boundaries.</summary>
internal sealed class AuthoritySession : IDisposable
{
    private readonly object gate = new();
    private readonly AuthoritySessionState identity;
    private readonly Dictionary<ushort, ConnectionEpoch> connectionEpochs = [];
    private readonly Queue<PendingReplicaMessage> inbound = new();
    private readonly Queue<PendingSnapshotAck> snapshotAcks = new();
    private readonly Queue<PendingScopeControl> scopeControl = new();
    private readonly Queue<Action> lifecycle = new();
    private bool applyingPausedJoin;

    // During a paused world's initial installation there are no local frames to reach the
    // snapshot's tick. Install its ordered tail without changing the simulation clock.
    public void ApplyPausedJoin(long localTick)
    {
        applyingPausedJoin = true;
        try { OnFrameBoundary(localTick); }
        finally { applyingPausedJoin = false; }
    }

    public void EnqueueLifecycle(Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        lock (gate) lifecycle.Enqueue(action);
    }
    // the client's standing subscription set and its planning scratch. The planet sentinel is
    // -1 so the first observation after a world begin always plans the initial subscriptions.
    private readonly List<ScopeKey> standingScopes = [];
    private readonly List<ScopeKey> desiredScratch = [];
    private readonly Dictionary<ScopeKey, object> replicaPools = [];
    public Func<ScopeKey, object> ReplicaPoolIdentity { get; set; }
    public DarkFogReplicaBinding DarkFogReplica { get; private set; }
    public TurretReplicaBinding TurretReplica { get; private set; }
    public RuinSnapshotAdapter RuinHost { get; set; }
    public RuinReplicaBinding RuinReplica { get; private set; }
    public Action<long> AfterClientFrame { get; set; }
    public Action<long> BeforeHostFrame { get; set; }
    public Action<long> AfterHostFrame { get; set; }
    public bool RequireGamePools { get; set; }
    private int standingPlanetId = -1;
    private int standingStarId = -1;
    private bool standingInSector;
    private ulong lastConnectionEpoch;
    private HostCommandQueue commands;
    private IHostCommandExecutor hostExecutor;
    private IReplicaMessageApplier replicaApplier;
    private ISnapshotAckSink snapshotAckSink;
    private IScopeControlSink scopeControlSink;
    private bool disposed;
    private long framesDrained;
    private long inboundApplied;
    private long inboundDropped;
    private long acksDropped;
    private long scopeControlDropped;
    private long scopeControlRefused;
    private long scopeControlHandled;

    public AuthoritySession(AuthoritySessionState identity)
    {
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    /// <summary>The shared identity the packet gate validates against.</summary>
    public AuthoritySessionState Identity => identity;

    /// <summary>
    /// Client apply and traffic counters. Replaced when a world begins, so a report describes
    /// one world instead of accumulating across a reload.
    /// </summary>

    /// <summary>The window in which a replica write is legal. Never open outside the frame boundary.</summary>
    public ReplicaApplyContext ApplyContext { get; } = new();

    /// <summary>
    /// The host command inbox, or null when this session is not a host authority world.
    /// </summary>
    /// <remarks>
    /// Created together with the world epoch, so a client can never enqueue host work and a host
    /// cannot accept commands before it has a world identity to key them with.
    /// </remarks>
    public HostCommandQueue Commands => commands;

    /// <summary>Optional capture hook installed by the replicator.</summary>
    public IAuthorityFrameCapture Capture { get; set; }

    /// <summary>Applier for queued replica messages.</summary>
    public IReplicaMessageApplier ReplicaApplier
    {
        get => replicaApplier;
        set => replicaApplier = value;
    }

    /// <summary>
    /// The client's canonical replica, or null while this session is not an authority client.
    /// </summary>
    public ClientWorldReplica WorldReplica { get; private set; }
    private readonly RuntimeObjectVerifier runtimeVerifier = new();

    /// <summary>
    /// Where this session's replica returns its snapshot acknowledgements. Assigning it after the
    /// world began forwards to the live replica; assigning before is picked up at
    /// <see cref="BeginAuthorityWorld"/>.
    /// </summary>
    public ISnapshotAckSink SnapshotAckSink
    {
        get => snapshotAckSink;
        set
        {
            snapshotAckSink = value;
            if (WorldReplica != null) WorldReplica.SnapshotAckSink = value;
        }
    }

    /// <summary>
    /// Where this session's replica sends scope-control requests. Assigning it after the world
    /// began forwards to the live replica; assigning before is picked up at
    /// <see cref="BeginAuthorityWorld"/>.
    /// </summary>
    public IScopeControlSink ScopeControlSink
    {
        get => scopeControlSink;
        set
        {
            scopeControlSink = value;
            if (WorldReplica != null) WorldReplica.ScopeControlSink = value;
        }
    }

    /// <summary>
    /// The host's replicator, or null until a world view and sink are registered. A host
    /// without a registered world view captures nothing, which is fail-closed: no canonical source,
    /// no invented facts.
    /// </summary>
    public HostWorldReplicator HostReplicator { get; private set; }
    public FactoryCombatSnapshotAdapter FactoryCombatHost { get; set; }
    public CraftSnapshotAdapter CraftHost { get; set; }
    public GroundEnemySnapshotAdapter GroundEnemyHost { get; set; }
    public SpaceEnemySnapshotAdapter SpaceEnemyHost { get; set; }

    /// <summary>Reads subscription eligibility directly from the transport's live player data.</summary>
    public Func<ushort, int?> SubscriptionPlanetProvider { get; set; }

    /// <summary>
    /// The host's death transaction ledger, or null until a host authority world begins.
    /// </summary>
    /// <remarks>
    /// Host-only. Every damage/death source funnels into <see cref="HostDeathLedger.OpenDeath"/>,
    /// which opens the one death transaction per object generation and runs the side-effect binding
    /// (kill statistics, drops, construction-task release) exactly once. Statistics and drops are
    /// vanilla-delegated (the host's own
    /// HandleZeroHp runs them once), and <see cref="HostDeathCapture"/> observes the
    /// vanilla commit. A client never owns one — a client-visible death is the replica's
    /// tombstone, not a local transaction.
    /// </remarks>
    public HostDeathLedger HostDeaths { get; private set; }

    /// <summary>
    /// The host's vanilla-death capture, or null until the domain adapters register it.
    /// </summary>
    /// <remarks>
    /// Host-only. The vanilla death commit (CombatStat.HandleZeroHp) observes its deaths here from
    /// any rule thread; <see cref="OnFrameComplete"/> commits them into <see cref="HostDeaths"/> at
    /// the quiescent boundary, before the replicator's scan publishes the frame's despawns. Until
    /// the adapters register it, vanilla deaths produce no ledger transaction (fail-closed).
    /// </remarks>
    public HostDeathCapture HostDeathCapture { get; private set; }

    /// <summary>Registers the vanilla-death capture. Called once per world by the adapter wiring.</summary>
    public void SetHostDeathCapture(HostDeathCapture capture) => HostDeathCapture = capture;

    /// <summary>True when this session is a host authority world with a live command inbox.</summary>
    public bool IsHostAuthority => identity.IsHost && identity.IsActive && commands != null;

    /// <summary>
    /// True when the runtime side of the current identity was built: the host inbox or the
    /// client replica exists. Called with the gate held.
    /// </summary>
    private bool IsRuntimeBuiltLocked(bool isHost) =>
        isHost ? commands != null : WorldReplica != null;

    /// <summary>Frame boundaries processed since the session started.</summary>
    public long FramesDrained => System.Threading.Interlocked.Read(ref framesDrained);

    /// <summary>Host tick of the most recent frame boundary, or 0 before the first one.</summary>
    public long LastFrameTick { get; private set; }
    /// <summary>Tick of the native frame whose pools are being captured, before the next frame starts.</summary>
    public long CompletedSimulationTick { get; private set; }

    /// <summary>Replica messages applied at a frame boundary.</summary>
    public long InboundApplied => System.Threading.Interlocked.Read(ref inboundApplied);

    /// <summary>Replica messages refused or failed at a frame boundary.</summary>
    public long InboundDropped => System.Threading.Interlocked.Read(ref inboundDropped);

    /// <summary>Snapshot acknowledgements refused or dropped for lack of a replicator.</summary>
    public long AcksDropped => System.Threading.Interlocked.Read(ref acksDropped);

    /// <summary>Scope-control requests dropped (no host world, no replicator, or full queue).</summary>
    public long ScopeControlDropped => System.Threading.Interlocked.Read(ref scopeControlDropped);

    /// <summary>Scope-control requests refused by the subscription policy or for an unknown player.</summary>
    public long ScopeControlRefused => System.Threading.Interlocked.Read(ref scopeControlRefused);

    /// <summary>Scope-control requests applied to the replicator at a frame boundary.</summary>
    public long ScopeControlHandled => System.Threading.Interlocked.Read(ref scopeControlHandled);

    /// <summary>Host ticks between scope digest publications. Zero disables digests.</summary>
    public long DigestIntervalTicks { get; set; } = 300;

    /// <summary>Replica messages waiting for the frame boundary.</summary>
    public int InboundCount
    {
        get
        {
            lock (gate)
            {
                return inbound.Count;
            }
        }
    }

    /// <summary>
    /// Establishes the world identity and, on the host, opens the command inbox.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once per world load. A second call for the same epoch and role is a no-op once
    /// the runtime was built for it, so a duplicate load notification cannot mint two identities;
    /// a different epoch replaces the runtime, which is what makes packets from the previous
    /// world fail the gate's epoch check. An adopted-but-unbuilt world still builds: the welcome
    /// processor adopts the epoch on the shared identity before the runtime builds.
    /// </para>
    /// <para>
    /// This is also the point the authority guard policy is verified. Both sides reach it — the host from
    /// its world load and the client from adopting the host's welcome — so a missing required hook
    /// stops multiplayer here, on both peers, instead of letting one of them run without its guard.
    /// Verification failures are reported and stop multiplayer startup.
    /// </para>
    /// </remarks>
    public void BeginAuthorityWorld(AuthorityEpoch epoch, bool isHost)
    {
        if (!epoch.IsValid)
        {
            Log.Warn("[authority] refusing to begin an authority world with an invalid epoch");
            return;
        }

        // Verified before the identity is adopted, so a failed check leaves the session in whatever
        // state it was in without installing a runtime that lacks its guards.
        if (!AuthorityRuleGuard.VerifyLoadOnce())
        {
            Log.Error("[authority] refusing to initialize multiplayer world: " + AuthorityRuleGuard.LoadFailure);
            return;
        }

        lock (gate)
        {
            // A second call for the same epoch and role is a no-op, but only when the runtime
            // was actually built for it. The welcome processor adopts the epoch on the shared
            // identity before the runtime builds, so an adopted-but-unbuilt world must still
            // build its replica/registries; otherwise no client ever gets a WorldReplica.
            if (identity.Epoch.IsValid && identity.Epoch.Equals(epoch) && identity.IsHost == isHost &&
                IsRuntimeBuiltLocked(isHost))
            {
                return;
            }

            // A new world invalidates everything the previous one queued, so nothing from the old
            // epoch can be executed against the new world. Player presence and ledger balances are
            // per-world for the same reason: a reloaded world must not inherit the previous one's
            // seats or stock.
            inbound.Clear();
            connectionEpochs.Clear();
            lastConnectionEpoch = 0;
            commands?.Clear();
            scopeControl.Clear();
            hostExecutor = null;
            CommandResultSink = null;
            SubscriptionPlanetProvider = null;
            HostDeaths?.Clear();
            HostDeaths = null;
            HostDeathCapture?.Clear();
            HostDeathCapture = null;
            FactoryCombatHost = null;
            CraftHost = null;
            GroundEnemyHost = null; SpaceEnemyHost = null;
            // the new world plans its standing subscriptions from scratch; the old world's
            // scopes must not suppress the new world's initial subscribes.
            standingScopes.Clear();
            standingRetries.Clear();
            replicaPools.Clear();
            standingPlanetId = -1;
            standingStarId = -1;
            standingInSector = false;
        }

        identity.BeginAuthorityWorld(epoch, isHost);
        commands = isHost ? new HostCommandQueue(epoch) : null;
        if (isHost)
        {
            // Native host rules already settle drops and statistics. This ledger observes their
            // generation-aware deaths without installing a second construction/resource model.
            HostDeaths = new HostDeathLedger(epoch);
        }

        // A client gets a replica and the applier that feeds it inside the apply
        // window; a host gets nothing here — its replicator needs a canonical world view, and
        // running capture without one would mean inventing facts.
        if (isHost)
        {
            WorldReplica = null;
            replicaApplier = null;
            HostReplicator = null;
            Capture = null;
        }
        else
        {
            WorldReplica = new ClientWorldReplica(epoch)
            {
                SnapshotAckSink = snapshotAckSink,
                ScopeControlSink = scopeControlSink
            };
            replicaApplier = new AuthorityReplicaApplier(identity, ApplyContext, WorldReplica);

            // mirror mutations reach the client's pools through the domain bindings,
            // which write only inside this session's apply window (the applier is the sole caller).
            var factoryBinding = new FactoryCombatReplicaBinding
            {
                Replica = WorldReplica
            };
            var groundBinding = new GroundEnemyReplicaBinding
            {
                Replica = WorldReplica
            };
            var spaceBinding = new SpaceEnemyReplicaBinding
            {
                Replica = WorldReplica
            };
            var craftBinding = new CraftReplicaBinding
            {
                Replica = WorldReplica
            };
            DarkFogReplica = new DarkFogReplicaBinding();
            TurretReplica = new TurretReplicaBinding(epoch) { Replica = WorldReplica };
            RuinReplica = new RuinReplicaBinding();
            WorldReplica.MirrorObserver = new CompositeMirrorObserver(factoryBinding, groundBinding, spaceBinding, craftBinding, DarkFogReplica, TurretReplica, RuinReplica)
            { EnforceReadiness = RequireGamePools };
            ReplicaPoolIdentity = scope => scope.Kind switch
            {
                PoolKind.GroundEnemy => GameMain.galaxy?.PlanetById(scope.Scope)?.factory?.enemyPool,
                PoolKind.Entity => GameMain.galaxy?.PlanetById(scope.Scope)?.factory?.entityPool,
                PoolKind.GroundCraft => GameMain.galaxy?.PlanetById(scope.Scope)?.factory?.craftPool,
                PoolKind.Base => DarkFogReplica.PoolIdentity(scope.Scope,
                    GameMain.galaxy?.PlanetById(scope.Scope)?.factory?.enemyPool,
                    GameMain.galaxy?.PlanetById(scope.Scope)?.factory?.enemySystem?.bases.buffer),
                PoolKind.Turret => GameMain.galaxy?.PlanetById(scope.Scope)?.factory?.entityPool,
                PoolKind.Ruin => GameMain.galaxy?.PlanetById(scope.Scope)?.factory?.ruinPool,
                PoolKind.SpaceEnemy => GameMain.spaceSector?.enemyPool,
                PoolKind.SpaceCraft => GameMain.spaceSector?.craftPool,
                _ => (object)GameMain.spaceSector
            };
            // the client's visual-event table. Predictions merge by cause when the host
            // event arrives; the table carries no HP/shield/ledger reference by construction.
        }

    }

    /// <summary>
    /// Registers the host's canonical world view and delivery sink, enabling frame capture.
    /// </summary>
    /// <remarks>
    /// Called once per world by the adapter cards that can read the vanilla pools
    /// and by the tests' fake world. Registering twice would strand two divergent replication
    /// states behind one capture seam, so it is refused instead of replaced. Until this is called,
    /// a host authority world captures nothing: multiplayer cannot publish facts it has no source for.
    /// </remarks>
    /// <param name="deathView">
    /// The host's death ledger, so a published removal can say whether it was a death. Omitting it
    /// keeps every removal a plain despawn, which is the fail-closed direction.
    /// </param>
    public bool RegisterHostReplication(IHostWorldView worldView, IReplicationSink sink,
        IHostDeathView deathView = null)
    {
        if (worldView == null || sink == null) return false;
        if (!identity.IsActive || !identity.IsHost) return false;
        if (HostReplicator != null) return false;
        HostReplicator = new HostWorldReplicator(identity.Epoch, worldView, sink,
            deathView: deathView);
        Capture = new HostReplicationCapture(HostReplicator);
        return true;
    }

    /// <summary>Assigns this connection's epoch and player id. Every command must echo both.</summary>
    public void SetConnection(ConnectionEpoch connectionEpoch, ushort playerId)
    {
        identity.SetConnection(connectionEpoch, playerId);
        if (playerId != 0)
        {
            lock (gate)
            {
                connectionEpochs[playerId] = connectionEpoch;
            }
        }
    }

    /// <summary>
    /// Allocates the next connection epoch for a player on the host.
    /// </summary>
    /// <remarks>
    /// The counter is monotonic across the session, so a reconnecting player is never handed the
    /// epoch of their previous connection and the old dedup window cannot be replayed.
    /// </remarks>
    public ConnectionEpoch AssignConnectionEpoch(ushort playerId)
    {
        lock (gate)
        {
            lastConnectionEpoch = ConnectionEpoch.Next(lastConnectionEpoch).Value;
            var epoch = new ConnectionEpoch(lastConnectionEpoch);
            if (playerId != 0) connectionEpochs[playerId] = epoch;
            return epoch;
        }
    }

    /// <summary>The connection epoch the host assigned to a player, or an invalid epoch if unknown.</summary>
    public ConnectionEpoch ConnectionEpochFor(ushort playerId)
    {
        lock (gate)
        {
            return connectionEpochs.TryGetValue(playerId, out var epoch) ? epoch : default;
        }
    }

    /// <summary>
    /// Forgets one player's connection and drops their dedup history.
    /// </summary>
    /// <remarks>
    /// Called when a client disconnects. A reconnect is a new connection epoch, so keeping the old
    /// window would only waste memory and leave a stale mapping that a message could still match.
    /// Queued commands from that connection are dropped with it. Presence goes offline but the
    /// persistent entry — and every ledger balance under it — survives, so a reconnect with the
    /// same persistent id rebinds without resetting its stock.
    /// </remarks>
    public void ForgetPlayerConnection(ushort playerId)
    {
        if (playerId == 0) return;
        lock (gate)
        {
            if (connectionEpochs.TryGetValue(playerId, out var epoch))
            {
                connectionEpochs.Remove(playerId);
                commands?.ForgetConnection(epoch);
            }
        }
        // the replicator's cursors are per connection. A reconnecting client reuses the same
        // session player id with a new connection epoch, so leaving the old cursors in place made
        // its re-subscribe hit the "healthy active cursor" path and return no baseline — the new
        // process then sat in Snapshotting forever. Dropping the cursors forces a fresh baseline.
        HostReplicator?.RemoveSubscriber(playerId);
    }

    /// <summary>
    /// Offers an admitted command to the host inbox. Called on the socket thread.
    /// </summary>
    /// <remarks>
    /// A false return is back-pressure or a session that is not a host authority world; the caller
    /// answers the client with a rejection and must not assume the command will run.
    /// </remarks>
    public bool TryEnqueueHostCommand(CommandKey key, AuthorityCommandPacket packet, ushort connectionPlayerId,
        int connectionId, long enqueuedTick)
    {
        var queue = commands;
        if (queue == null) return false;
        return queue.TryEnqueue(key, packet, connectionPlayerId, connectionId, enqueuedTick);
    }

    /// <summary>
    /// Offers a validated replica message to the client inbox. Called on the socket thread.
    /// </summary>
    /// <remarks>
    /// The message is stored, never applied: applying here would run game code on the socket thread,
    /// which is forbidden. A full inbox refuses the message so a stalled client
    /// cannot be made to allocate without bound.
    /// </remarks>
    public bool TryEnqueueReplicaMessage(AuthorityEnvelopePacket packet, in ApplyScope scope)
    {
        if (packet == null) return false;
        // The transport reuses one packet instance per type (`SubscribeReusable`), so the queued
        // message must own its data; otherwise the next packet of the same type overwrites it
        // before the frame boundary applies it.
        var owned = packet.CreateOwnedCopy();
        lock (gate)
        {
            if (inbound.Count >= AuthorityLimits.CommandQueueMax)
            {
                System.Threading.Interlocked.Increment(ref inboundDropped);
                return false;
            }
            inbound.Enqueue(new PendingReplicaMessage(owned, scope));
        }
        // inbound traffic is counted per family at the moment it is accepted, so the receive
        // rate reflects what the session actually took in rather than what the transport delivered.
        return true;
    }

    /// <summary>
    /// The pre-frame safe point: admit host commands or apply queued replica messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called from the frame boundary patch at the quiescent safe point. It is deliberately
    /// idempotent per tick: draining twice applies nothing the second time because both queues are
    /// empty, and the host's dedup window answers a repeated command from its cache instead of
    /// executing it again. That is what makes a duplicated frame notification harmless.
    /// </para>
    /// <para>
    /// If no host executor is configured, an admitted command completes with <see
    /// cref="CommandResultCode.RejectedNotReady"/>.
    /// </para>
    /// </remarks>
    public void OnFrameBoundary(long hostTick)
    {
        if (!identity.IsActive) return;
        LastFrameTick = hostTick;
        System.Threading.Interlocked.Increment(ref framesDrained);

        while (true)
        {
            Action action;
            lock (gate) { if (lifecycle.Count == 0) break; action = lifecycle.Dequeue(); }
            action();
        }

        if (identity.IsHost)
        {
            BeforeHostFrame?.Invoke(hostTick);
            if (hostExecutor is IHostTickAware tickAware) tickAware.HostTick = hostTick;
            commands?.Drain(hostExecutor, CommandResultSink);
            DrainSnapshotAcks();
            // subscription and resync requests join the acks at the frame boundary, where the
            // replicator's cursors may move. The subscription policy is enforced from the host's
            // accepted live player data, never from the packet's claims.
            DrainScopeControl();
            return;
        }

        var frameObserver = WorldReplica?.MirrorObserver as IReplicaFrameObserver;
        frameObserver?.BeginReplicaFrame();
        try
        {
            if (ReplicaPoolIdentity != null && WorldReplica != null)
                foreach (var scope in standingScopes)
                {
                    if (WorldReplica.MirrorObserver is IReplicaBaselineReadiness readiness && !readiness.IsReadyForBaseline(scope)) continue;
                    var pool = ReplicaPoolIdentity(scope);
                    if (pool == null) continue;
                    if (replicaPools.TryGetValue(scope, out var oldPool) && ReferenceEquals(pool, oldPool)) continue;
                    ApplyContext.Run(new ApplyScope(scope, hostTick: hostTick), () => WorldReplica.ReapplyScope(scope));
                    replicaPools[scope] = pool;
                }
            DrainReplicaInbox();
            WorldReplica?.RetryPendingBaselines((scope, apply) => ApplyContext.Run(new ApplyScope(scope, hostTick: hostTick), apply));
            if (RequireGamePools)
            {
                TurretReplica?.RetryPending();
                DarkFogReplica?.FlushSearchReferences();
            }
        }
        finally
        {
            frameObserver?.EndReplicaFrame((scope, write) => ApplyContext.Run(new ApplyScope(scope, hostTick: hostTick), write));
        }
        AfterClientFrame?.Invoke(hostTick);
    }

    public void AuditRuntime(long tick)
    {
        if (RequireGamePools) WorldReplica?.AuditRuntimeObjects(tick, runtimeVerifier);
    }

    /// <summary>Captures host facts after the frame's rules have run.</summary>
    public void OnFrameComplete(long hostTick, long? completedSimulationTick = null)
    {
        if (!identity.IsActive || !identity.IsHost) return;
        CompletedSimulationTick = completedSimulationTick ?? hostTick;
        AfterHostFrame?.Invoke(hostTick);
        // commit the deaths the frame's rules captured before the scan reads the pools, so a
        // generation-aware death observation exists by the time the frame's despawn is
        // published. Drain is a no-op when no capture is registered (fail-closed).
        HostDeathCapture?.Drain(hostTick);
        Capture?.Capture(hostTick);
        // the periodic digest rides the same post-frame safe point as capture, so it observes
        // exactly the member table the frame's scan published.
        HostReplicator?.PublishDigests(hostTick, DigestIntervalTicks);
    }

    /// <summary>Returns command outcomes to the authenticated connection.</summary>
    public Action<QueuedHostCommand, CommandDrainDisposition, CommandOutcome> CommandResultSink { get; set; }

    /// <summary>Optional command handler; unsupported commands return NotReady.</summary>
    public IHostCommandExecutor HostExecutor
    {
        get => hostExecutor;
        set => hostExecutor = value;
    }

    /// <summary>
    /// Applies every queued replica message inside a validated apply scope.
    /// </summary>
    /// <remarks>
    /// Each message opens and closes its own scope through <see cref="ReplicaApplyContext.Run"/>, so
    /// an applier that throws leaves the context closed and the session not writable. The failed
    /// message is dropped rather than retried: the design requires an explained failure, not an
    /// unbounded retry of a message that may already have partially applied.
    /// </remarks>
    private void DrainReplicaInbox()
    {
        while (true)
        {
            // The queue is drained in order until it is empty, so the frame applies everything that
            // arrived. Nothing is dropped or reordered by a budget; the bound on a frame's work is
            // the inbox's own ceiling, applied when a message is admitted.
            PendingReplicaMessage message;
            lock (gate)
            {
                if (inbound.Count == 0) break;
                var next = inbound.Peek().Packet;
                if (!applyingPausedJoin && next.HostTick > LastFrameTick && next.Family is AuthorityFamily.WorldState or
                    AuthorityFamily.Lifecycle or AuthorityFamily.PlayerCombatState or AuthorityFamily.ScopeDigest) break;
                message = inbound.Dequeue();
            }

            var applier = replicaApplier;
            if (applier == null)
            {
                // Refuse the message when no replica applier is configured.
                System.Threading.Interlocked.Increment(ref inboundDropped);
                continue;
            }

            try
            {
                var appliedMessage = false;
                ApplyContext.Run(message.Scope, () => appliedMessage = applier.Apply(message));
                if (appliedMessage)
                {
                    System.Threading.Interlocked.Increment(ref inboundApplied);
                }
                else
                {
                    System.Threading.Interlocked.Increment(ref inboundDropped);
                }
            }
            catch (Exception e)
            {
                System.Threading.Interlocked.Increment(ref inboundDropped);
                Log.Error($"[authority] replica apply failed for {message.Scope}: {e}");
            }
        }

    }

    /// <summary>
    /// Offers a validated snapshot acknowledgement to the host. Called on the socket thread.
    /// </summary>
    /// <remarks>
    /// The ack is queued for the frame boundary like every other inbound message: the replicator is
    /// not thread-safe and the subscriber cursors may only move at the quiescent frame boundary.
    /// </remarks>
    public bool NotifySnapshotAck(ushort playerId, long baselineId, long lastAppliedSequence, bool accepted)
    {
        if (!identity.IsActive || !identity.IsHost) return false;
        lock (gate)
        {
            if (snapshotAcks.Count >= AuthorityLimits.CommandQueueMax)
            {
                System.Threading.Interlocked.Increment(ref acksDropped);
                return false;
            }
            snapshotAcks.Enqueue(new PendingSnapshotAck(playerId, baselineId, lastAppliedSequence, accepted));
            return true;
        }
    }

    /// <summary>
    /// Offers a validated scope-control request to the host. Called on the socket thread.
    /// </summary>
    public bool TryEnqueueScopeControl(ushort playerId, ScopeControlOp op, in ScopeKey scope,
        ScopeRecoveryReason reason, bool digestOnly, long subscriptionEpoch, long lastAppliedSequence,
        int observedStarId = 0, long requestId = 0)
    {
        if (!identity.IsActive || !identity.IsHost) return false;
        lock (gate)
        {
            if (scopeControl.Count >= AuthorityLimits.CommandQueueMax)
            {
                System.Threading.Interlocked.Increment(ref scopeControlDropped);
                return false;
            }
            scopeControl.Enqueue(new PendingScopeControl(playerId, op, scope, reason, digestOnly,
                subscriptionEpoch, lastAppliedSequence, observedStarId, requestId));
            return true;
        }
    }

    /// <summary>
    /// Applies every queued scope-control request to the replicator.
    /// </summary>
    /// <remarks>
    /// The host-side eligibility check reads the transport's live player data: an unknown player
    /// subscribes to nothing, and a planet pool is only served for the planet the host accepted the
    /// player on. Resync requests are not eligibility-gated — a broken stream is repaired whatever
    /// the player did, because the alternative is a permanently divergent replica.
    /// </remarks>
    private void DrainScopeControl()
    {
        while (true)
        {
            PendingScopeControl request;
            lock (gate)
            {
                if (scopeControl.Count == 0) return;
                request = scopeControl.Dequeue();
            }
            if (HostReplicator == null)
            {
                System.Threading.Interlocked.Increment(ref scopeControlDropped);
                continue;
            }
            if (request.Op is ScopeControlOp.Subscribe or ScopeControlOp.Resync &&
                request.Scope.Kind == PoolKind.SpaceEnemy && HostReplicator.SubscriberStarProvider != null &&
                (request.RequestId <= 0 || request.ObservedStarId != HostReplicator.SubscriberStarProvider(request.PlayerId)))
            {
                System.Threading.Interlocked.Increment(ref scopeControlRefused);
                continue; // The standing subscription retries after the location has arrived.
            }
            switch (request.Op)
            {
                case ScopeControlOp.Subscribe:
                    var location = SubscriptionPlanetProvider?.Invoke(request.PlayerId);
                    var planetId = location.GetValueOrDefault();
                    if (ScopeSubscriptionPolicy.MaySubscribe(request.Scope, location.HasValue, planetId)
                            != ScopeSubscriptionDecision.Allowed)
                    {
                        System.Threading.Interlocked.Increment(ref scopeControlRefused);
                        Log.Warn($"[authority] scope subscribe refused for player {request.PlayerId}: " +
                                 $"{request.Scope} (registered planet {planetId})");
                        continue;
                    }
                    HostReplicator.Subscribe(request.PlayerId, request.Scope, request.DigestOnly, request.RequestId);
                    System.Threading.Interlocked.Increment(ref scopeControlHandled);
                    break;
                case ScopeControlOp.Unsubscribe:
                    HostReplicator.Unsubscribe(request.PlayerId, request.Scope);
                    System.Threading.Interlocked.Increment(ref scopeControlHandled);
                    break;
                case ScopeControlOp.Resync:
                    if (!HostReplicator.RequestResync(request.PlayerId, request.Scope, request.RequestId))
                    {
                        System.Threading.Interlocked.Increment(ref scopeControlRefused);
                        continue;
                    }
                    System.Threading.Interlocked.Increment(ref scopeControlHandled);
                    break;
                default:
                    System.Threading.Interlocked.Increment(ref scopeControlRefused);
                    break;
            }
        }
    }

    /// <summary>
    /// Client-side entry: subscribe one scope and tell the host.
    /// </summary>
    /// <remarks>
    /// The replica's phase and the wire request move together, so a caller cannot leave one behind;
    /// without a sink the phase still changes and the omission is counted on the replica.
    /// </remarks>
    public bool TrySubscribeScope(ScopeKey scope, bool digestOnly = false)
    {
        if (WorldReplica == null || !scope.IsValid) return false;
        if (scope.Kind == PoolKind.SpaceEnemy && (WorldReplica.SpaceEnemyRequestId == 0 ||
            !WorldReplica.Versions.TryGetScope(scope, out var pending) ||
            pending.Phase is not (SubscriptionPhase.Snapshotting or SubscriptionPhase.Installing)))
            WorldReplica.BeginSpaceEnemySubscription();
        WorldReplica.Subscribe(scope, digestOnly);
        scopeControlSink?.Send(ScopeControlOp.Subscribe, scope, ScopeRecoveryReason.None, digestOnly,
            WorldReplica.Versions.TryGetScope(scope, out var state) ? state.SubscriptionEpoch : 0,
            lastAppliedSequence: 0);
        return true;
    }

    /// <summary>Client-side entry: leave one scope and tell the host.</summary>
    public bool TryUnsubscribeScope(ScopeKey scope)
    {
        if (WorldReplica == null || !scope.IsValid) return false;
        ApplyContext.Run(new ApplyScope(scope), () =>
        {
            if (scope.Kind == PoolKind.Turret) TurretReplica?.ClearScope(scope.Scope);
            WorldReplica.Unsubscribe(scope);
        });
        replicaPools.Remove(scope);
        scopeControlSink?.Send(ScopeControlOp.Unsubscribe, scope, ScopeRecoveryReason.None,
            digestOnly: false, subscriptionEpoch: 0, lastAppliedSequence: 0);
        return true;
    }

    /// <summary>
    /// Client-side lifecycle trigger: re-plans the standing subscription set from where the
    /// subscriber actually is and issues only the delta.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called at the frame boundary with the client's own local view — the same observation the
    /// standing-set policy (<see cref="ScopeSubscriptionPolicy.DesiredScopes"/>) is defined over.
    /// A context that yields no change sends nothing, so calling this every frame is free. The
    /// host re-checks every request against its own registry facts
    /// (<see cref="ScopeSubscriptionPolicy.MaySubscribe"/>); this side only plans.
    /// </para>
    /// <para>
    /// Digest-only observations are deliberately untouched: a star map names what it watches per
    /// frame, while the standing set is where the subscriber lives. Leaving a planet unsubscribes
    /// its pools and landing on another subscribes them fresh — which re-enters Snapshotting and
    /// installs a new baseline, so a fast A→B→A can never let one planet's history pollute the
    /// other's.
    /// </para>
    /// </remarks>
    public void UpdateStandingSubscriptions(int currentPlanetId, bool isInSector,
        Func<PoolKind, bool> supportsPool = null, int currentStarId = 0)
    {
        // Only a client holds a replica; a host session has nothing to subscribe.
        if (WorldReplica == null) return;
        currentStarId = Math.Max(0, currentStarId);
        if (currentPlanetId == standingPlanetId && isInSector == standingInSector && currentStarId == standingStarId)
        { RetryStandingSubscriptions(); return; }
        var changedStar = currentStarId != standingStarId;
        standingPlanetId = currentPlanetId;
        standingStarId = currentStarId;
        standingInSector = isInSector;

        var context = new ScopeObservationContext
        {
            CurrentPlanetId = currentPlanetId,
            IsInSector = isInSector
        };
        ScopeSubscriptionPolicy.DesiredScopes(context, desiredScratch);
        if (supportsPool != null)
            desiredScratch.RemoveAll(scope => !supportsPool(scope.Kind));

        // Leave first: scopes no longer desired are released, so a planet switch never holds two
        // planets' pools at once.
        for (var i = standingScopes.Count - 1; i >= 0; i--)
        {
            var held = standingScopes[i];
            var stillDesired = false;
            foreach (var scope in desiredScratch)
            {
                if (scope.Equals(held))
                {
                    stillDesired = true;
                    break;
                }
            }
            if (!stillDesired || changedStar && held.Kind == PoolKind.SpaceEnemy)
            {
                TryUnsubscribeScope(held);
                standingRetries.Remove(held);
                standingScopes.RemoveAt(i);
            }
        }

        // Then join: desired scopes not already held subscribe through the normal path.
        foreach (var scope in desiredScratch)
        {
            var alreadyHeld = false;
            foreach (var held in standingScopes)
            {
                if (held.Equals(scope))
                {
                    alreadyHeld = true;
                    break;
                }
            }
            if (!alreadyHeld && TrySubscribeScope(scope))
            {
                standingScopes.Add(scope);
                standingRetries[scope] = LastFrameTick + 120;
            }
        }
    }

    /// <summary>The standing set this client currently holds, as last planned.</summary>
    public IReadOnlyList<ScopeKey> StandingScopes => standingScopes;

    private readonly Dictionary<ScopeKey, long> standingRetries = new();
    private void RetryStandingSubscriptions()
    {
        foreach (var scope in standingScopes)
        {
            if (!WorldReplica.Versions.TryGetScope(scope, out var state) || state.Phase != SubscriptionPhase.Snapshotting) continue;
            if (standingRetries.TryGetValue(scope, out var due) && LastFrameTick < due) continue;
            standingRetries[scope] = LastFrameTick + 120;
            // A location packet can arrive after the first subscribe. Repeat the request without
            // replacing staged data or incrementing identity; the host coalesces in-flight baselines.
            scopeControlSink?.Send(ScopeControlOp.Subscribe, scope, ScopeRecoveryReason.None, false,
                state.SubscriptionEpoch, state.LastAppliedSequence);
        }
    }

    private void DrainSnapshotAcks()
    {
        while (true)
        {
            PendingSnapshotAck ack;
            lock (gate)
            {
                if (snapshotAcks.Count == 0) return;
                ack = snapshotAcks.Dequeue();
            }
            if (HostReplicator == null)
            {
                // An ack with no replicator cannot be matched to anything; counting it keeps the
                // omission visible instead of stranding a client's subscription silently.
                System.Threading.Interlocked.Increment(ref acksDropped);
                continue;
            }
            HostReplicator.OnSnapshotAck(ack.PlayerId, ack.BaselineId, ack.LastAppliedSequence, ack.Accepted);
        }
    }

    // A user-requested pause stops simulation, not the connection's baseline conversation.
    public void PumpHostSubscriptions()
    {
        if (!identity.IsHost) return;
        DrainSnapshotAcks();
        DrainScopeControl();
    }

    /// <summary>
    /// Clears the session protocol and world identity and clears every queue.
    /// </summary>
    /// <remarks>
    /// Called on pause, world load and leaving the room. Clearing the epoch is what makes a packet
    /// from the previous world fail the gate instead of being applied to the new one.
    /// </remarks>
    public void Reset()
    {
        CompletedSimulationTick = 0;
        lock (gate)
        {
            inbound.Clear();
            connectionEpochs.Clear();
            lastConnectionEpoch = 0;
            commands?.Clear();
            commands = null;
            hostExecutor = null;
            CommandResultSink = null;
            SubscriptionPlanetProvider = null;
            HostDeaths?.Clear();
            HostDeaths = null;
            HostDeathCapture?.Clear();
            HostDeathCapture = null;
            FactoryCombatHost = null;
            CraftHost = null;
            GroundEnemyHost = null; SpaceEnemyHost = null;
            standingScopes.Clear();
            standingRetries.Clear();
            replicaPools.Clear();
            standingPlanetId = -1;
            standingStarId = -1;
            standingInSector = false;
            snapshotAcks.Clear();
            scopeControl.Clear();
            lifecycle.Clear();
        }
        identity.Reset();
        ApplyContext.Reset();
        WorldReplica = null;
        TurretReplica = null;
        RuinReplica = null; RuinHost = null;
        DarkFogReplica = null;
        HostReplicator = null;
        Capture = null;
        replicaApplier = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Reset();
        GC.SuppressFinalize(this);
    }
}
