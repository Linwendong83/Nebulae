#region

using NebulaeModel.Contracts;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Statistics;

#endregion

namespace NebulaeNetwork.PacketProcessors.Players;

internal class PlayerMechaStatBatchProcessor : PacketProcessor<PlayerMechaStatBatchPacket>
{
    protected override void ProcessPacket(PlayerMechaStatBatchPacket packet, NebulaeConnection conn)
    {
        if (IsClient || !packet.IsConsistent())
        {
            return;
        }

        var player = Players.Get(conn);
        if (player == null)
        {
            return;
        }
        // The factory is resolved once for the whole batch: every entry in it was reported by the same
        // player in the same tick, so they all resolve to the same factory.
        var factory = FindFactoryForPlayer(player);
        for (var i = 0; i < packet.ItemId.Length; i++)
        {
            if (packet.ItemCount[i] >= 0)
            {
                GameMain.mainPlayer.mecha.AddProductionStat(packet.ItemId[i], packet.ItemCount[i], factory);
            }
            else
            {
                GameMain.mainPlayer.mecha.AddConsumptionStat(packet.ItemId[i], -packet.ItemCount[i], factory);
            }
        }
    }

    /// <summary>
    ///     The factory a player's mecha statistics belong to: the one on their planet, or the nearest
    ///     loaded one when they are in space.
    /// </summary>
    internal static PlanetFactory FindFactoryForPlayer(INebulaePlayer player)
    {
        var factory = GameMain.galaxy.PlanetById(player.Data.LocalPlanetId)?.factory;
        if (factory != null)
        {
            return factory;
        }

        // If client is in space, find the nearest planet factory base on its position
        // code modified from Player.get_nearestFactory
        var uPosition = player.Data.UPosition.ToVectorLF3();
        if (player.Data.LocalStarId > 0)
        {
            // find the nearest planet in the star system
            var minDistance = double.MaxValue;
            var planets = GameMain.galaxy.StarById(player.Data.LocalStarId).planets;
            foreach (var planetData in planets)
            {
                if (planetData.factory == null)
                {
                    continue;
                }
                var distance = (planetData.uPosition - uPosition).magnitude;
                distance -= planetData.realRadius;
                if (!(distance < minDistance))
                {
                    continue;
                }
                minDistance = distance;
                factory = planetData.factory;
            }
        }

        if (factory == null)
        {
            // find the nearest planet from all factories
            var minDistance = double.MaxValue;
            var factories = GameMain.data.factories;
            foreach (var t in factories)
            {
                if (t == null)
                {
                    continue;
                }
                var distance = (t.planet.uPosition - uPosition).magnitude;
                distance -= t.planet.realRadius;
                if (!(distance < minDistance))
                {
                    continue;
                }
                minDistance = distance;
                factory = t;
            }
        }
        return factory;
    }
}
