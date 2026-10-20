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
        if (packet.Data == null || packet.Data.Length > VegetableCollectionState.MaxBytes) return;
        if (!IsHost)
        {
            if (!packet.IsAuthoritative) return;
            Multiplayer.Session.Vegetation.RestoreLocal(packet.Data);
            ((PlayerData)Multiplayer.Session.LocalPlayer.Data).VegetableCollectionData = packet.Data;
            return;
        }
        if (packet.IsAuthoritative) return;
        var player = Players.Get(conn, EConnectionStatus.Connected);
        if (player == null) return;
        var canonical = Multiplayer.Session.Vegetation.VerifySnapshot(player.Id, packet.Data);
        if (canonical == null) return;
        Log.Warn($"Vegetation collection mismatch for player {player.Id}; restoring server state.");
        conn.SendPacket(new VegetableCollectionSnapshotPacket(canonical, true));
    }
}
