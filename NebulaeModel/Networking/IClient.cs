#region

using System.Net;
using NebulaeModel.Contracts;

#endregion

namespace NebulaeModel.Networking;

internal interface IClient : INetworkProvider
{
    /// <summary>
    ///     Host name or IP literal as configured by the player, without scheme, port or IPv6
    ///     brackets. It is kept as is: resolving it is the job of the socket layer, which does
    ///     it every time a connection is opened.
    /// </summary>
    string ServerHost { get; }

    /// <summary>
    ///     Port of the server.
    /// </summary>
    int ServerPort { get; }

    /// <summary>
    ///     Websocket scheme used to connect: "ws" or "wss".
    /// </summary>
    string ServerProtocol { get; }

    /// <summary>
    ///     Endpoint of the peer this client is actually connected to, or null while there is no
    ///     open connection. It is informational only and is never used to build the connection
    ///     url (that keeps <see cref="ServerHost" /> intact).
    /// </summary>
    IPEndPoint ServerEndpoint { get; set; }

    public void Update();

    public void Start();

    public void Stop();
}
