#region

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

    public void Update();

    public void Start();
}
