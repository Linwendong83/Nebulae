using System.Threading;
using NebulaeModel.Networking;
using NebulaeModel.Logger;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Utils;
using NebulaeWorld;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace NebulaeNetwork.Messaging;

internal class WebSocketService : WebSocketBehavior
{
    public static Server Server;
    public static NebulaeNetPacketProcessor PacketProcessor;
    private NebulaeConnection connection;
    private Server owner;

    public WebSocketService() { }

    protected override void OnOpen()
    {
        if (Multiplayer.Session.IsGameLoaded == false && Multiplayer.Session.IsInLobby == false)
        {
            // Reject any connection that occurs while the host's game is loading.
            Context.WebSocket.Close((ushort)DisconnectionReason.HostStillLoading,
                "Host still loading, please try again later.".Translate());
            return;
        }

        Log.Info($"Client connected ID: {ID}");
        TcpSocketOptions.TryEnableNoDelay(Context, out _);
        var conn = new NebulaeConnection(Context.WebSocket, Context.UserEndPoint, PacketProcessor, hostSide: true);
        owner = Server;
        owner.OnSocketConnection(conn);

        connection = conn;
        Log.Debug($"[network] accepted socket={ID} connection={conn.Id} status={conn.ConnectionStatus}");
    }

    protected override void OnMessage(MessageEventArgs e)
    {
        // Find created NebulaeConnection
        var conn = connection;
        if (conn == null)
        {
            Log.Warn($"Unregister socket {Context.UserEndPoint.GetHashCode()}");
            return;
        }

        byte[] packet;
        try
        {
            if (!e.IsBinary) throw new System.IO.InvalidDataException("Packet frames require binary websocket messages");
            packet = conn.ReceivePacket(PacketCompression.UnwrapForReceive(e.RawData));
        }
        catch (System.IO.InvalidDataException exception)
        {
            conn.StopSending();
            // The stream cannot be trusted past a message its framing cannot read, so the peer is
            // dropped instead of being answered with packets decoded from a corrupt stream.
            Diagnostics.Warn("socket:unreadable:" + conn.Id, () => $"[network] unreadable socket message socket={ID} connection={conn.Id} status={conn.ConnectionStatus} bytes={e.RawData?.Length} binary={e.IsBinary}", exception);
            UnityDispatchQueue.RunOnMainThread(() =>
                Server?.Disconnect(conn, DisconnectionReason.InvalidData, "Corrupt packet stream"));
            return;
        }

        if (packet != null) PacketProcessor.EnqueuePacketForProcessing(packet, conn);
    }

    protected override void OnClose(CloseEventArgs e)
    {
        var departing = Interlocked.Exchange(ref connection, null);
        if (departing == null)
        {
            return;
        }
        // Retire transport work on this thread; Unity/player cleanup may wait for a frame.
        departing.StopSending();
        var server = owner;

        // If the reason of a client disconnect is because we are still loading the game,
        // we don't need to inform the other clients since the disconnected client never
        // joined the game in the first place.
        if (e.Code == (short)DisconnectionReason.HostStillLoading)
        {
            return;
        }

        if (e.Code == (ushort)DisconnectionReason.ClientRequestedDisconnect || e.Code == 1000)
            Log.Debug($"[network] socket closed socket={ID} connection={departing.Id} code={e.Code} clean={e.WasClean} reason={e.Reason}");
        else Diagnostics.Warn("socket:close:" + departing.Id, () => $"[network] unexpected socket close socket={ID} connection={departing.Id} status={departing.ConnectionStatus} code={e.Code} clean={e.WasClean} reason={e.Reason}");
        UnityDispatchQueue.RunOnMainThread(() =>
        {
            // This is to make sure that we don't try to deal with player disconnection
            // if it is because we have stopped the server and are not in a multiplayer game anymore.
            if (server != null && ReferenceEquals(Multiplayer.Session?.Server, server))
            {
                server.OnSocketDisconnection(departing);
            }
        });
    }

    protected override void OnError(ErrorEventArgs e)
    {
        var departing = Interlocked.Exchange(ref connection, null);
        departing?.StopSending();
        var server = owner;
        Diagnostics.Warn("socket:error:" + (departing?.Id.ToString() ?? ID),
            () => $"[network] socket error socket={ID} connection={departing?.Id} status={departing?.ConnectionStatus} message={e.Message}", e.Exception);
        if (departing == null)
        {
            return;
        }

        UnityDispatchQueue.RunOnMainThread(() =>
        {
            // This is to make sure that we don't try to deal with player disconnection
            // if it is because we have stopped the server and are not in a multiplayer game anymore.
            if (server != null && ReferenceEquals(Multiplayer.Session?.Server, server))
            {
                server.OnSocketDisconnection(departing);
            }
        });
    }
}
