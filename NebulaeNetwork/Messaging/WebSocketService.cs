using System;
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
        if (TcpSocketOptions.TryEnableNoDelay(Context, out var previousNoDelay))
            Log.Info($"[network] accepted TCP NoDelay: {previousNoDelay} -> true");
        var conn = new NebulaeConnection(Context.WebSocket, Context.UserEndPoint, PacketProcessor, hostSide: true);
        Server.OnSocketConnection(conn);

        connection = conn;
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
            // The stream cannot be trusted past a message its framing cannot read, so the peer is
            // dropped instead of being answered with packets decoded from a corrupt stream.
            Log.Warn($"Dropping socket {ID} after an unreadable message: {exception.Message}");
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

        // If the reason of a client disconnect is because we are still loading the game,
        // we don't need to inform the other clients since the disconnected client never
        // joined the game in the first place.
        if (e.Code == (short)DisconnectionReason.HostStillLoading)
        {
            return;
        }

        Log.Info($"Client disconnected: {ID}, reason: {e.Reason}");
        UnityDispatchQueue.RunOnMainThread(() =>
        {
            // This is to make sure that we don't try to deal with player disconnection
            // if it is because we have stopped the server and are not in a multiplayer game anymore.
            if (Multiplayer.IsActive)
            {
                Server.OnSocketDisconnection(departing);
            }
        });
    }

    protected override void OnError(ErrorEventArgs e)
    {
        var departing = Interlocked.Exchange(ref connection, null);
        if (departing == null)
        {
            return;
        }

        Log.Info($"Client disconnected because of an error: {ID}, reason: {e.Exception}");
        UnityDispatchQueue.RunOnMainThread(() =>
        {
            // This is to make sure that we don't try to deal with player disconnection
            // if it is because we have stopped the server and are not in a multiplayer game anymore.
            if (Multiplayer.IsActive)
            {
                Server.OnSocketDisconnection(departing);
            }
        });
    }
}
