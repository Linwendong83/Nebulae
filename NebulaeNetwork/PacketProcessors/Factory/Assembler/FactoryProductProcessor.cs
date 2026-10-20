using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Factory.Assembler;
using NebulaeWorld;

namespace NebulaeNetwork.PacketProcessors.Factory.Assembler;

internal class FactoryProductProcessor : PacketProcessor<FactoryProductPacket>
{
    protected override void ProcessPacket(FactoryProductPacket packet, NebulaeConnection conn)
    {
        if (IsClient) { Multiplayer.Session.FactoryProducts.Receive(packet); return; }
        if (packet.Message != (byte)FactoryProductMessage.RequestSnapshot || !packet.TryKey(Multiplayer.Session.Authority.Epoch, out var key)) return;
        var player = Multiplayer.Session.Server.Players.Get(conn) ??
            Multiplayer.Session.Server.Players.Get(conn, EConnectionStatus.Syncing);
        if (player?.Data.LocalStarId != key.Scope / 100) return;
        NebulaeWorld.GameStates.JoinSyncManager.WaitForSimulation();
        var snapshot = Multiplayer.Session.FactoryProducts.Snapshot(packet);
        if (snapshot != null) conn.SendPacket(snapshot);
    }
}
