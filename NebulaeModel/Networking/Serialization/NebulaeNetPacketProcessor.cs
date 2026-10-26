using System;
using System.Collections.Generic;
using System.IO;
using NebulaeModel.Logger;
using NebulaeModel.DataStructures;

namespace NebulaeModel.Networking.Serialization;

internal class NebulaeNetPacketProcessor : NetPacketProcessor, INetPacketProcessor
{
    private readonly NetDataWriter writer = new();
    private readonly Queue<PendingPacket> pendingPackets = new();
    private readonly DiagnosticWatchdog watchdog = new();
    private double nextDiagnosticCheck;
    [ThreadStatic] private static double? currentPacketEnqueuedAt;
    // Nested batch/delta handlers inherit the outer packet's arrival time. This metadata is
    // local to dispatch and never comes from a timestamp supplied by the remote peer.
    internal static double CurrentPacketQueueSeconds => currentPacketEnqueuedAt.HasValue
        ? Math.Max(0, Diagnostics.Now - currentPacketEnqueuedAt.Value) : 0;

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
    private Exception dispatchFailure;
    private string dispatchFailurePath;
    public int DispatchDepth => dispatchDepth;

    // Router children are part of their parent's ordered operation, not new work at the
    // end of the queue (where a later snapshot/ready marker could overtake them).
    public void DispatchInline(byte[] data, object userData)
    {
        if (userData is NebulaeConnection { IsStopped: true }) return;
        if (data == null) throw new ParseException("Missing packet data");
        if (dispatchDepth == 0) { dispatchFailure = null; dispatchFailurePath = null; }
        if (++dispatchDepth > 8) { dispatchDepth--; throw new ParseException("Packet nesting limit exceeded"); }
        try { ReadPacket(new NetDataReader(data), userData); }
        catch (Exception error)
        {
            var id = data.Length >= 8 ? BitConverter.ToUInt64(data, 0) : 0;
            var context = $"{WireContract.PacketName(id)}(id={id},bytes={data.Length},depth={dispatchDepth})";
            dispatchFailurePath = context + (ReferenceEquals(dispatchFailure, error) ? " -> " + dispatchFailurePath : "");
            dispatchFailure = error;
            error.Data["Nebulae.PacketPath"] = dispatchFailurePath;
            throw;
        }
        finally { dispatchDepth--; }
    }

    public void ProcessPacketQueue(double budgetMilliseconds = 0)
    {
        ProcessPacketQueue(new WorkBudget(budgetMilliseconds));
    }

    internal void ProcessPacketQueue(WorkBudget budget)
    {
        if (Diagnostics.Now >= nextDiagnosticCheck)
        {
            nextDiagnosticCheck = Diagnostics.Now + 1;
            int count; double age;
            lock (pendingPackets) { count = pendingPackets.Count; age = count == 0 ? 0 : Diagnostics.Now - pendingPackets.Peek().EnqueuedAt; }
            watchdog.Observe("packet-queue", count > 0 && (age >= 5 || !EnablePacketProcessing),
                () => $"[network] pending={count} peak={PeakPendingCount} oldest={age:F1}s enabled={EnablePacketProcessing}", 10);
        }
        while (EnablePacketProcessing)
        {
            PendingPacket packet;
            lock (pendingPackets)
            {
                if (pendingPackets.Count == 0) break;
                packet = pendingPackets.Dequeue();
            }
            var previousArrival = currentPacketEnqueuedAt;
            currentPacketEnqueuedAt = packet.EnqueuedAt;
            try
            {
                DispatchInline(packet.Data, packet.UserData);
            }
            catch (Exception ex) when (ex is ParseException or InvalidDataException or EndOfStreamException)
            {
                Diagnostics.Warn("packet:malformed:" + (packet.UserData as NebulaeConnection)?.Id,
                    () => $"[network] malformed packet connection={(packet.UserData as NebulaeConnection)?.Id} path={ex.Data["Nebulae.PacketPath"]} bytes={packet.Data?.Length ?? 0} pending={PendingCount}", ex);
                if (packet.UserData is NebulaeConnection connection)
                {
                    connection.StopSending();
                    connection.peerSocket?.Close((ushort)DisconnectionReason.InvalidData, "Malformed packet");
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Error("packet:dispatch:" + ex.Data["Nebulae.PacketPath"],
                    () => $"[network] packet handler failed connection={(packet.UserData as NebulaeConnection)?.Id} path={ex.Data["Nebulae.PacketPath"]} bytes={packet.Data?.Length ?? 0} pending={PendingCount}", ex);
            }
            finally { currentPacketEnqueuedAt = previousArrival; }
            if (budget.Expired) break;
        }
    }

    public void EnqueuePacketForProcessing(byte[] rawData, object userData)
    {
        if (userData is NebulaeConnection { IsStopped: true }) return;
        lock (pendingPackets)
        {
            pendingPackets.Enqueue(new PendingPacket(rawData, userData));
            PeakPendingCount = Math.Max(PeakPendingCount, pendingPackets.Count);
        }
    }
}
