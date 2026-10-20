#region

using NebulaeModel.Networking;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority;

#endregion

namespace NebulaeNetwork.PacketProcessors.Authority;

/// <summary>Shared admission for every authority envelope processor.</summary>
/// <remarks>
/// <para>Every authority family passes <see cref="Admit"/> before processing, enforcing direction, schema,
/// epoch, size and identity checks. Family-specific validation follows admission.</para>
/// <para>This class performs no world work. Admitted commands are queued for host execution at the frame
/// boundary.</para>
/// </remarks>
internal static class AuthorityPacketAdmission
{
    /// <summary>
    /// Runs the gate for one packet received on a specific connection.
    /// </summary>
    /// <remarks>
    /// Identity comes from the connection, so on the host the claimed id is compared
    /// with the id the transport assigned to <em>this</em> connection. A connection with no player
    /// record cannot assert any id, so its message is refused as a forgery rather than accepted.
    /// </remarks>
    /// <param name="packet">The received envelope.</param>
    /// <param name="direction">Direction the packet arrived from.</param>
    /// <param name="conn">Connection the packet arrived on, or null for host-originated messages.</param>
    /// <param name="hostTick">Current host tick, for the log line.</param>
    /// <param name="context">The context the gate validated against, so callers reuse it.</param>
    /// <param name="reject">The rejection, or <see cref="AuthorityRejectCode.None"/>.</param>
    /// <remarks>
    /// The resolved context is handed back deliberately. A caller that needs to check a command key
    /// or an object key must do it against the same connection epoch the gate used; re-deriving the
    /// context would let the two checks disagree about which connection a message came from.
    /// </remarks>
    internal static bool Admit(AuthorityEnvelopePacket packet, AuthorityDirection direction, NebulaeConnection conn,
        long hostTick, out AuthoritySessionContext context, out AuthorityReject reject)
    {
        var session = Multiplayer.Session;
        var state = session?.Authority;
        if (state is null)
        {
            context = AuthoritySessionContext.Uninitialized;
            reject = new AuthorityReject(AuthorityRejectCode.SessionNotReady, "no authority session");
            return false;
        }

        context = ResolveContext(state, direction, conn);
        reject = AuthorityEnvelopeGateRuntime.Validate(packet, context, direction, hostTick);
        return !reject.IsRejected;
    }

    /// <summary>
    /// Runs the gate for a message whose context the caller does not need.
    /// </summary>
    internal static bool Admit(AuthorityEnvelopePacket packet, AuthorityDirection direction, NebulaeConnection conn,
        long hostTick, out AuthorityReject reject) =>
        Admit(packet, direction, conn, hostTick, out _, out reject);

    /// <summary>
    /// Picks the context the gate must validate against.
    /// </summary>
    /// <remarks>
    /// A client-to-server message is checked against the connection it arrived on, because only the
    /// host has several connections to tell apart. Everything else uses the session's own identity.
    /// </remarks>
    private static AuthoritySessionContext ResolveContext(AuthoritySessionState state,
        AuthorityDirection direction, NebulaeConnection conn)
    {
        if (direction != AuthorityDirection.ClientToServer || !state.IsHost || conn is null)
        {
            return state.Context;
        }

        // An unknown connection resolves to the invalid player id, so its claim cannot match and the
        // message is refused as a forgery. That is deliberate: admitting it would trust the packet.
        var player = Multiplayer.Session?.Server?.Players?.Get(conn, EConnectionStatus.Connected)
                     ?? Multiplayer.Session?.Server?.Players?.Get(conn, EConnectionStatus.Syncing);
        var playerId = player?.Id ?? 0;

        // Every client has its own connection epoch, so the message is checked against the epoch the
        // host assigned to this connection rather than one session-wide value. Otherwise a
        // reconnecting client could replay a sequence number the host still remembers.
        var epoch = Multiplayer.Session?.AuthorityRuntime?.ConnectionEpochFor(playerId) ?? default;
        return epoch.IsValid ? state.ContextForConnection(playerId, epoch) : state.ContextForConnection(playerId);
    }
}
