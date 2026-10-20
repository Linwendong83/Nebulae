using System;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Session;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Session;

internal sealed class JoinDataTransferBeginProcessor : PacketProcessor<JoinDataTransferBegin>
{
    protected override void ProcessPacket(JoinDataTransferBegin packet, NebulaeConnection conn)
    {
        if (IsHost) return;
        try { Multiplayer.Session.JoinSync.BeginTransfer(packet, conn); }
        catch (Exception error) { Multiplayer.Session.JoinSync.FailLocal(error); }
    }
}

internal sealed class JoinDataTransferChunkProcessor : PacketProcessor<JoinDataTransferChunk>
{
    protected override void ProcessPacket(JoinDataTransferChunk packet, NebulaeConnection conn)
    {
        if (IsHost) return;
        try { Multiplayer.Session.JoinSync.ReceiveTransfer(packet, conn); }
        catch (Exception error) { Multiplayer.Session.JoinSync.FailLocal(error); }
    }
}
