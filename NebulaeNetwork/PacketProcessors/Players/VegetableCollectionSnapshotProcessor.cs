using NebulaeModel.Networking;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Players;
using NebulaeWorld;
using NebulaeModel.Logger;

namespace NebulaeNetwork.PacketProcessors.Players;
internal sealed class VegetableCollectionSnapshotProcessor : PacketProcessor<VegetableCollectionSnapshotPacket>
{
    protected override void ProcessPacket(VegetableCollectionSnapshotPacket packet, NebulaeConnection conn)
    {
        if (packet.Data == null || packet.Data.Length > VegetableCollectionState.MaxBytes || packet.OperationSequence < 0 || packet.Revision < 0) return;
        if (!IsHost)
        {
            if (!packet.IsAuthoritative) return;
            Multiplayer.Session.Vegetation.Reconcile(packet);
            return;
        }
        if (packet.IsAuthoritative) return;
        var player = Players.Get(conn, EConnectionStatus.Connected);
        if (player == null) return;
        var canonical = Multiplayer.Session.Vegetation.VerifySnapshot(player.Id, packet.Data);
        if (canonical != null) Log.Warn($"Vegetation collection mismatch for player {player.Id}; restoring server state.");
        // Ordered behind all operations represented by this sequence. Always acknowledge,
        // including equal states, so the client can retire its pending predictions.
        conn.SendPacket(Multiplayer.Session.Vegetation.Receipt(player.Id, packet.OperationSequence));
    }
}
