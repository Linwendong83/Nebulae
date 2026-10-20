
#region
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Players;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Players;
internal class PlayerUpdateLocalStarIdProcessor : PacketProcessor<PlayerUpdateLocalStarId>
{
    protected override void ProcessPacket(PlayerUpdateLocalStarId packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player != null)
            {
                var changedStar = player.Data.LocalStarId != packet.StarId;
                player.Data.LocalStarId = packet.StarId;
                packet.PlayerId = player.Id;
                if (changedStar) { Multiplayer.Session.BattleVisuals.SendInitial(conn, packet.StarId); Multiplayer.Session.PlayerWeapons.SendInitial(conn, packet.StarId); }
            }
            Server.SendPacketExclude(packet, conn);

            //Realize hives in this star system
            var star = GameMain.galaxy.StarById(packet.StarId);
            if (star != null)
            {
                var enemyDFHiveSystem = GameMain.spaceSector.dfHives[star.index];
                while (enemyDFHiveSystem != null)
                {
                    enemyDFHiveSystem.Realize();
                    enemyDFHiveSystem = enemyDFHiveSystem.nextSibling;
                }
            }
        }

        using (Multiplayer.Session.World.GetRemotePlayersModels(out var remotePlayersModels))
        {
            if (remotePlayersModels.TryGetValue(packet.PlayerId, out var remotePlayerModel))
            {
                remotePlayerModel.Movement.LocalStarId = packet.StarId;
            }
        }
    }
}
