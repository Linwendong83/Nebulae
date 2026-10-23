#region

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using NebulaeModel.Logger;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Session;
using WebSocketSharp;

#endregion

namespace NebulaeModel.Networking;

internal class NebulaeConnection : INebulaeConnection
{
    private readonly NebulaeNetPacketProcessor packetProcessor;
    [ThreadStatic] private static NebulaeNetPacketProcessor deferredWriter;
    private readonly BackgroundPacketEncoder encoder = new();
    private NebulaeNetPacketProcessor batchWriter;

    private readonly EndPoint peerEndpoint;

    // Temporarily public until refactor finished
    public WebSocket peerSocket { get; private set; }

    private readonly OrderedSendQueue sender;
    private readonly StateDeltaChannel deltas;
    private EConnectionStatus connectionStatus = EConnectionStatus.Undefined;
    private static int nextConnectionId;
    private long nextTransferId;
    private int stopped;

    public bool IsStopped => Volatile.Read(ref stopped) != 0;
    public bool HandshakeCompleted { get; private set; }

    internal void CompleteHandshake() => HandshakeCompleted = true;

    public bool IsAlive => peerSocket?.ReadyState == WebSocketState.Open;

    public int Id { get; }

    public EConnectionStatus ConnectionStatus
    {
        get => connectionStatus;
        set
        {
            if (value < connectionStatus)
                throw new InvalidOperationException("Connection Status cannot be rolled back to a lower state.");

            connectionStatus = value;
        }
    }

    public NebulaeConnection(WebSocket peerSocket, EndPoint peerEndpoint, NebulaeNetPacketProcessor packetProcessor, bool hostSide = false)
    {
        this.peerEndpoint = peerEndpoint;
        this.peerSocket = peerSocket;
        this.packetProcessor = packetProcessor;
        // Endpoint reuse must not make a reconnect equal to a socket awaiting cleanup.
        Id = Interlocked.Increment(ref nextConnectionId);
        deltas = new StateDeltaChannel(bytes => sender.Enqueue(bytes), factory => sender.Enqueue(factory));
        sender = new OrderedSendQueue(bytes =>
        {
            var state = deltas.Prepare(bytes);
            return state == null ? null : PacketCompression.WrapForSend(state, sender.PreparationBacklogged);
        }, (bytes, done) =>
        {
            if (peerSocket.ReadyState == WebSocketState.Open)
            {
                var success = BlockingWebSocketSender.Send(peerSocket, bytes);
                done(success);
            }
            else done(false);
        }, onFailure: error =>
        {
            Log.Warn($"Send queue failed for connection {Id} ({this.peerEndpoint}): {error.Message}");
            NebulaeModel.Utils.UnityDispatchQueue.RunOnMainThread(() =>
                peerSocket.Close((ushort)DisconnectionReason.InvalidData, "Packet send failed"));
        }, combine: hostSide ? packets =>
        {
            if (batchWriter == null) { batchWriter = new NebulaeNetPacketProcessor(); PacketUtils.RegisterNestedTypes(batchWriter); }
            return RealtimePacketBatch.Encode(packets, batchWriter);
        }
        : null, canCombine: bytes => hostSide && ConnectionStatus == EConnectionStatus.Connected && RealtimePacketBatch.CanInclude(bytes));
    }


    public void SendPacket<T>(T packet) where T : class, new()
    {
        lock (packetProcessor) sender.Enqueue(packetProcessor.Write(packet));
    }

    public byte[] SerializePacket<T>(T packet) where T : class, new()
    {
        lock (packetProcessor) return packetProcessor.Write(packet);
    }

    // Encode independently of compression/socket progress. The sender admits the future
    // now and consumes its bytes in connection order. Factories own immutable inputs only.
    internal void SendDeferredPacket<T>(Func<T> factory) where T : class, new() => sender.Enqueue(encoder.Prepare(() =>
    {
        var packet = factory();
        // Isolated from the main-thread reader/writer; the ordered worker owns this serializer.
        if (deferredWriter == null)
        {
            deferredWriter = new NebulaeNetPacketProcessor();
            PacketUtils.RegisterNestedTypes(deferredWriter);
        }
        var bytes = deferredWriter.Write(packet);
        return bytes;
    }), ConnectionStatus == EConnectionStatus.Connected);

    public void SendRawPacket(byte[] rawData) => sender.Enqueue(rawData);

    public void SendJoinTransfer(JoinDataStage stage, int planetId, byte[][] messages)
    {
        var transferId = Interlocked.Increment(ref nextTransferId);
        sender.EnqueueBatch(messages, prepared => JoinDataTransfer.Encode(Id, transferId, stage, planetId, prepared));
    }
    public byte[] ReceivePacket(byte[] rawData) => IsStopped ? null : deltas.Receive(rawData);
    public void StopSending()
    {
        Interlocked.Exchange(ref stopped, 1);
        sender.Stop();
        deltas.Clear();
    }
    public bool Equals(INebulaeConnection connection)
    {
        return ReferenceEquals(this, connection);
    }

    public bool FlushSendQueue(int timeoutMilliseconds = 1000) => sender.Flush(timeoutMilliseconds);

    public static bool operator ==(NebulaeConnection left, NebulaeConnection right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(NebulaeConnection left, NebulaeConnection right)
    {
        return !Equals(left, right);
    }

    public override bool Equals(object obj)
    {
        return ReferenceEquals(this, obj);
    }

    public override int GetHashCode()
    {
        return Id;
    }
}
