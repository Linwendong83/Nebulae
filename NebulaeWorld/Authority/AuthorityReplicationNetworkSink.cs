#region

using System;
using NebulaeModel.Contracts;
using NebulaeModel.Authority;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Networking;

#endregion

namespace NebulaeWorld.Authority;

/// <summary>
/// The host-side delivery sink: hands each replication packet to the subscriber's connection.
/// </summary>
/// <remarks>
/// <para>
///
/// It resolves the subscriber id — the player id the session assigned — to the player's connection
/// and sends there; a subscriber the server no longer knows is dropped with a log line, because a
/// departing player's stream dies with its connection and any catch-up goes through a fresh
/// subscription after reconnect.
/// </para>
/// <para>
/// The sink touches no world state, so calling it from the frame boundary's delivery path keeps
/// the threading rules intact. Ordering is the WebSocket's reliable ordered channel, which is what
/// the design requires of the transport in G1.
/// </para>
/// </remarks>
internal sealed class NetworkReplicationSink : IDeferredReplicationSink
{
    private readonly Func<ushort, INebulaePlayer> resolvePlayer;

    /// <summary>Creates a sink that resolves subscribers through the given lookup.</summary>
    /// <remarks>
    /// The lookup is injected so the transport dependency stays testable; production wiring passes
    /// the server's player collection. A null lookup resolves nothing and every send is dropped.
    /// </remarks>
    public NetworkReplicationSink(Func<ushort, INebulaePlayer> resolvePlayer)
    {
        this.resolvePlayer = resolvePlayer;
    }

    public void Send(ushort subscriberId, AuthorityEnvelopePacket packet)
    {
        if (packet == null) return;
        INebulaePlayer player = null;
        try
        {
            player = resolvePlayer?.Invoke(subscriberId);
        }
        catch (Exception e)
        {
            Log.Warn($"[authority] resolving player {subscriberId} for delivery failed: {e.Message}");
            return;
        }
        if (player == null)
        {
            Log.Warn($"[authority] cannot deliver {packet.Family} to player {subscriberId}: no connection");
            return;
        }
        Multiplayer.Session?.JoinSync?.FlushBeforeAuthority(player.Connection);
        SendConcrete(player, packet);
    }

    public void SendWorldStates(ushort subscriberId, Func<AuthorityWorldStatePacket> factory)
    {
        // Resolve and flush replay on the main thread. The worker retains this exact
        // connection, so a disconnect/reconnect cannot deliver old work to a new socket.
        INebulaePlayer player;
        try { player = resolvePlayer?.Invoke(subscriberId); }
        catch (Exception error) { Log.Warn($"[authority] resolving player {subscriberId} for delivery failed: {error.Message}"); return; }
        if (player == null) { Log.Warn($"[authority] cannot deliver WorldState to player {subscriberId}: no connection"); return; }
        Multiplayer.Session?.JoinSync?.FlushBeforeAuthority(player.Connection);
        if (player.Connection is NebulaeConnection connection) connection.SendDeferredPacket(factory);
        else player.SendPacket(factory());
    }

    /// <summary>
    /// Sends through the packet's concrete type: the transport API is typed per family, so an
    /// unhandled family is a visible refusal instead of a guessed serialization.
    /// </summary>
    private static void SendConcrete(INebulaePlayer player, AuthorityEnvelopePacket packet)
    {
        switch (packet)
        {
            case AuthorityLifecyclePacket lifecycle:
                player.SendPacket(lifecycle);
                return;
            case AuthorityWorldStatePacket worldState:
                player.SendPacket(worldState);
                return;
            case AuthoritySnapshotBeginPacket begin:
                player.SendPacket(begin);
                return;
            case AuthoritySnapshotChunkPacket chunk:
                player.SendPacket(chunk);
                return;
            case AuthoritySnapshotCommitPacket commit:
                player.SendPacket(commit);
                return;
            case AuthorityCommandResultPacket result:
                player.SendPacket(result);
                return;
            case AuthorityWelcomePacket welcome:
                player.SendPacket(welcome);
                return;
            case AuthorityScopeDigestPacket digest:
                player.SendPacket(digest);
                return;
            default:
                Log.Warn($"[authority] no typed send path for family {packet.Family}; refused");
                return;
        }
    }
}

/// <summary>
/// The client-side ack sink: fills the transport identity the replica cannot know and sends the
/// snapshot acknowledgement to the server.
/// </summary>
/// <remarks>
/// Identity comes from the connection, so the claimed player id and connection epoch
/// come from this session's own state at send time, never from the replica's bookkeeping.
/// </remarks>
internal sealed class NetworkSnapshotAckSink : ISnapshotAckSink
{
    private readonly Func<ushort> localPlayerId;
    private readonly Func<ulong> connectionEpoch;
    private readonly Action<AuthoritySnapshotAckPacket> send;

    public NetworkSnapshotAckSink(Func<ushort> localPlayerId, Func<ulong> connectionEpoch,
        Action<AuthoritySnapshotAckPacket> send)
    {
        this.localPlayerId = localPlayerId;
        this.connectionEpoch = connectionEpoch;
        this.send = send;
    }

    public void Send(AuthoritySnapshotAckPacket packet)
    {
        if (packet == null) return;
        packet.ClaimedPlayerId = localPlayerId?.Invoke() ?? 0;
        packet.ConnectionEpoch = connectionEpoch?.Invoke() ?? 0;
        send?.Invoke(packet);
    }
}

/// <summary>
/// The client-side scope-control sink: fills the transport identity and sends subscribe/unsubscribe/
/// resync requests to the server.
/// </summary>
/// <remarks>
/// The same identity rule as the ack sink: the claimed player id and connection epoch come from
/// this session's own state at send time, never from the replica's bookkeeping.
/// </remarks>
internal sealed class NetworkScopeControlSink : IScopeControlSink
{
    private readonly Func<ushort> localPlayerId;
    private readonly Func<ulong> connectionEpoch;
    private readonly Func<AuthorityEpoch> worldEpoch;
    private readonly Action<AuthorityScopeControlPacket> send;

    public NetworkScopeControlSink(Func<ushort> localPlayerId, Func<ulong> connectionEpoch,
        Func<AuthorityEpoch> worldEpoch, Action<AuthorityScopeControlPacket> send)
    {
        this.localPlayerId = localPlayerId;
        this.connectionEpoch = connectionEpoch;
        this.worldEpoch = worldEpoch;
        this.send = send;
    }

    public void Send(ScopeControlOp op, ScopeKey scope, ScopeRecoveryReason reason, bool digestOnly,
        long subscriptionEpoch, long lastAppliedSequence)
    {
        var epoch = worldEpoch?.Invoke() ?? default;
        var packet = AuthorityScopeControlPacket.Create(
            new AuthorityEnvelopeHeader(AuthorityFamily.ScopeControl, epoch,
                connection: default, sequence: 0, hostTick: 0, claimedPlayerId: 0, payloadLength: 0),
            op, scope, digestOnly, reason, subscriptionEpoch, lastAppliedSequence);
        packet.ClaimedPlayerId = localPlayerId?.Invoke() ?? 0;
        packet.ConnectionEpoch = connectionEpoch?.Invoke() ?? 0;
        packet.ObservedStarId = Math.Max(0, GameMain.localStar?.id ?? 0);
        packet.RequestId = scope.Kind == PoolKind.SpaceEnemy
            ? Multiplayer.Session?.AuthorityRuntime?.WorldReplica?.SpaceEnemyRequestId ?? 0 : 0;
        send?.Invoke(packet);
    }
}
