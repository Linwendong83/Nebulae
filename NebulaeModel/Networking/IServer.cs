using System.Collections.Generic;
using NebulaeModel.DataStructures;
using NebulaeModel.Contracts;

namespace NebulaeModel.Networking;

internal interface IServer : INetworkProvider
{
    ushort Port { get; set; }

    public ConcurrentPlayerCollection Players { get; }

    public void Update();

    public void Start();

    void Disconnect(INebulaeConnection conn, DisconnectionReason reason, string reasonMessage = "");

    public void SendToPlayers<T>(IEnumerable<KeyValuePair<INebulaeConnection, INebulaePlayer>> players, T packet) where T : class, new();
}
