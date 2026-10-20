using System;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Session;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Session;

internal class JoinSyncBeginProcessor : PacketProcessor<JoinSyncBegin>
{
    protected override void ProcessPacket(JoinSyncBegin packet, NebulaeConnection conn)
    {
        if (IsHost) return;
        try { Multiplayer.Session.JoinSync.Adopt(packet, conn); }
        catch (Exception error) { Multiplayer.Session.JoinSync.FailLocal(error); }
    }
}

internal class JoinSyncBatchProcessor : PacketProcessor<JoinSyncBatch>
{
    protected override void ProcessPacket(JoinSyncBatch packet, NebulaeConnection conn)
    {
        if (IsHost) return;
        try { Multiplayer.Session.JoinSync.Apply(packet, conn); }
        catch (Exception error) { Multiplayer.Session.JoinSync.FailLocal(error); }
    }
}

internal class JoinSyncBarrierProcessor : PacketProcessor<JoinSyncBarrier>
{
    protected override void ProcessPacket(JoinSyncBarrier packet, NebulaeConnection conn)
    {
        if (IsHost) return;
        try { Multiplayer.Session.JoinSync.Barrier(packet); }
        catch (Exception error) { Multiplayer.Session.JoinSync.FailLocal(error); }
    }
}

internal class JoinSyncAckProcessor : PacketProcessor<JoinSyncAck>
{
    protected override void ProcessPacket(JoinSyncAck packet, NebulaeConnection conn)
    {
        if (IsHost) Multiplayer.Session.JoinSync.Acknowledge(conn, packet);
    }
}

internal class JoinSyncProgressProcessor : PacketProcessor<JoinSyncProgress>
{
    protected override void ProcessPacket(JoinSyncProgress packet, NebulaeConnection conn)
    {
        if (IsHost) Multiplayer.Session.JoinSync.ReportProgress(conn, packet);
    }
}
