#region

using NebulaeModel.Authority;
using NebulaeModel.Logger;

#endregion

namespace NebulaeWorld.Authority;

/// <summary>The confirmed handshake and world identity of a multiplayer session.</summary>
/// <remarks>
/// The host generates one epoch per world load. A client confirms the handshake during the
/// handshake, then adopts that epoch from the first welcome. Construction and reset leave the
/// session uninitialized so neither a welcome nor world packets can bypass the handshake.
/// </remarks>
internal sealed class AuthoritySessionState
{
    private bool handshakeComplete;
    private AuthorityEpoch epoch;
    private ConnectionEpoch connection;
    private ushort localPlayerId;
    private bool isHost;

    /// <summary>True after the mod version handshake succeeds.</summary>
    public bool IsHandshakeComplete => handshakeComplete;

    /// <summary>Epoch of the loaded world, invalid before one is loaded.</summary>
    public AuthorityEpoch Epoch => epoch;

    /// <summary>Connection epoch clients must name on their commands.</summary>
    public ConnectionEpoch Connection => connection;

    /// <summary>Player id the transport assigned to this connection.</summary>
    public ushort LocalPlayerId => localPlayerId;

    /// <summary>True when this side is the host.</summary>
    public bool IsHost => isHost;

    /// <summary>True once the handshake is confirmed and a world epoch exists.</summary>
    public bool IsActive => IsHandshakeComplete && epoch.IsValid;

    /// <summary>Records successful completion of the mod version handshake.</summary>
    public void ConfirmHandshake() => handshakeComplete = true;

    /// <summary>
    /// Sets the host-owned world identity when a world is loaded.
    /// </summary>
    public void BeginAuthorityWorld(AuthorityEpoch worldEpoch, bool isHost)
    {
        if (!worldEpoch.IsValid)
        {
            Log.Warn("[authority] refusing to begin an authority world with an invalid epoch");
            return;
        }
        handshakeComplete = true;
        epoch = worldEpoch;
        this.isHost = isHost;
    }

    /// <summary>Assigns this connection's epoch and player id, which every command must echo.</summary>
    public void SetConnection(ConnectionEpoch connectionEpoch, ushort playerId)
    {
        connection = connectionEpoch;
        localPlayerId = playerId;
    }

    /// <summary>
    /// Adopts the world epoch a host stated in its welcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The welcome is the one message that cannot be validated against a known epoch, because it is
    /// what establishes it: a client has no world identity until the host names one. This method is
    /// therefore the single, explicit bootstrap path, and it is deliberately narrow.
    /// </para>
    /// <para>
    /// It only works while this session has no epoch, so a second welcome cannot silently replace
    /// the world under a running client; after the first one, every message must match the adopted
    /// epoch through the normal gate. The mod version handshake must already have completed.
    /// </para>
    /// </remarks>
    /// <returns>True when the epoch was adopted, false when this session already has one.</returns>
    public bool TryAdoptWorldEpoch(AuthorityEpoch worldEpoch)
    {
        if (!worldEpoch.IsValid) return false;
        if (!IsHandshakeComplete) return false;
        if (epoch.IsValid) return false;
        epoch = worldEpoch;
        return true;
    }

    /// <summary>Clears handshake and world identity when leaving a room or loading a different world.</summary>
    public void Reset()
    {
        handshakeComplete = false;
        epoch = default;
        connection = default;
        localPlayerId = 0;
        isHost = false;
    }

    /// <summary>
    /// The immutable snapshot the packet gate validates against.
    /// </summary>
    /// <remarks>
    /// The gate takes a value so it cannot observe the session changing underneath it mid-message,
    /// and so the same gate can be driven directly from tests.
    /// </remarks>
    public AuthoritySessionContext Context =>
        new(handshakeComplete, epoch, connection, localPlayerId, isHost);

    /// <summary>
    /// The context the gate must use for a message that arrived on one connection.
    /// </summary>
    /// <remarks>
    /// On the host every connection has its own player id, so a single session-wide id would accept
    /// a command that claims someone else's seat. <paramref name="connectionPlayerId"/> is what the
    /// transport assigned to this connection, and it is what the claimed id must equal. On the
    /// client the local id is the only one there is, so the argument is ignored there.
    /// </remarks>
    public AuthoritySessionContext ContextForConnection(ushort connectionPlayerId) =>
        ContextForConnection(connectionPlayerId, connection);

    /// <summary>
    /// The context the gate must use for a message that arrived on one connection, naming the epoch
    /// the host assigned to <em>that</em> connection.
    /// </summary>
    /// <remarks>
    /// The session-wide <see cref="Connection"/> is only meaningful on a client, which has exactly
    /// one connection. On a host every client has its own epoch, so validating all of them against
    /// one value would let a reconnecting client replay an old sequence number. The command queue supplies the
    /// assigned per-connection epoch; the session-wide value remains the fallback for a peer that
    /// has not been given one yet.
    /// </remarks>
    public AuthoritySessionContext ContextForConnection(ushort connectionPlayerId, ConnectionEpoch connectionEpoch) =>
        isHost
            ? new AuthoritySessionContext(handshakeComplete, epoch, connectionEpoch, connectionPlayerId, true)
            : Context;
}
