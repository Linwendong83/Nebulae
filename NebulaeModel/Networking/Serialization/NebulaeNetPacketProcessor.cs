using System;
using System.Collections.Generic;
using NebulaeModel.Logger;
using NebulaeModel.DataStructures;

namespace NebulaeModel.Networking.Serialization;

internal class NebulaeNetPacketProcessor : NetPacketProcessor, INetPacketProcessor
{
    private readonly NetDataWriter writer = new();
    private readonly Queue<PendingPacket> pendingPackets = new();

    /// <summary>
    /// Whether or not packet processing is enabled
    /// </summary>
    public bool EnablePacketProcessing { get; set; } = true;

    public NebulaeNetPacketProcessor()
    {
        _netSerializer = new NebulaeNetSerializer();
    }

    /// <summary>Serializes a packet using the shared writer.</summary>
    /// <remarks>Not thread-safe: callers must serialize access to the writer.</remarks>
    public byte[] Write<T>(T packet) where T : class, new()
    {
        writer.Reset();
        Write(writer, packet);

        return writer.CopyData();
    }

    public int PendingCount { get { lock (pendingPackets) return pendingPackets.Count; } }
    public int PeakPendingCount { get; private set; }
    private int dispatchDepth;
    public int DispatchDepth => dispatchDepth;

    // Router children are part of their parent's ordered operation, not new work at the
    // end of the queue (where a later snapshot/ready marker could overtake them).
    public void DispatchInline(byte[] data, object userData)
    {
        if (data == null) throw new ParseException("Missing packet data");
        if (++dispatchDepth > 8) { dispatchDepth--; throw new ParseException("Packet nesting limit exceeded"); }
        try { ReadPacket(new NetDataReader(data), userData); }
        finally { dispatchDepth--; }
    }

    public void ProcessPacketQueue(double budgetMilliseconds = 0)
    {
        ProcessPacketQueue(new WorkBudget(budgetMilliseconds));
    }

    internal void ProcessPacketQueue(WorkBudget budget)
    {
        while (EnablePacketProcessing)
        {
            PendingPacket packet;
            lock (pendingPackets)
            {
                if (pendingPackets.Count == 0) break;
                packet = pendingPackets.Dequeue();
            }
            try
            {
                DispatchInline(packet.Data, packet.UserData);
            }
            catch (ParseException ex)
            {
                Log.Warn($"Rejected malformed packet: {ex.Message}");
                if (packet.UserData is NebulaeConnection { peerSocket: not null } connection)
                {
                    connection.StopSending();
                    connection.peerSocket.Close((ushort)DisconnectionReason.InvalidData, "Malformed packet");
                }
            }
            catch (Exception ex) { Log.Error(ex); }
            if (budget.Expired) break;
        }
    }

    public void EnqueuePacketForProcessing(byte[] rawData, object userData)
    {
        lock (pendingPackets)
        {
            pendingPackets.Enqueue(new PendingPacket(rawData, userData));
            PeakPendingCount = Math.Max(PeakPendingCount, pendingPackets.Count);
        }
    }
}
