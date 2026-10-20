#region

using System.IO;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Statistics;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Statistics;
internal class StatisticsRequestEventProcessor : PacketProcessor<StatisticsRequestEvent>
{
    protected override void ProcessPacket(StatisticsRequestEvent packet, NebulaeConnection conn)
    {
        if (IsClient)
        {
            return;
        }

        var player = Players.Get(conn);
        if (player == null)
        {
            return;
        }
        switch (packet.Event)
        {
            case StatisticEvent.WindowOpened:
                {
                    Multiplayer.Session.Statistics.RegisterPlayer(conn, player.Id);
                    Multiplayer.Session.Kills.Subscribe(player.Id, conn);

                    using (var writer = new BinaryUtils.Writer())
                    {
                        Multiplayer.Session.Statistics.ExportAllData(writer.BinaryWriter);
                        conn.SendPacket(new StatisticsDataPacket(writer.CloseAndGetBytes()));
                    }
                    SendExtraData(conn, packet.AstroFilter);
                    break;
                }
            case StatisticEvent.WindowClosed:
                Multiplayer.Session.Statistics.UnRegisterPlayer(player.Id);
                Multiplayer.Session.Kills.Unsubscribe(player.Id);
                break;

            case StatisticEvent.AstroFilterChanged:
                SendExtraData(conn, packet.AstroFilter);
                break;
        }
    }

    static void SendExtraData(NebulaeConnection conn, int astroFilter)
    {
        if (astroFilter == 0) return;
        var window = UIRoot.instance.uiGame.statWindow;
        var originalAstroFilter = window.astroFilter;
        try
        {
            window.astroFilter = astroFilter;
            window.RefreshProductionExtraInfo(true);
        }
        finally { window.astroFilter = originalAstroFilter; }

        using var writer = new BinaryUtils.Writer();
        var factoryCount = ExportExtension(writer.BinaryWriter, astroFilter);
        conn.SendPacket(new StatisticsExtraDataPacket(factoryCount, writer.CloseAndGetBytes()));
    }

    static int ExportExtension(BinaryWriter writer, int astroFilter)
    {
        var factoryStatPool = GameMain.data.statistics.production.factoryStatPool;
        var factoryCount = 0;
        if (astroFilter == -1)
        {
            for (var i = 0; i < GameMain.data.factoryCount; i++)
            {
                writer.Write(i);
                ExportProductStatExtension(writer, factoryStatPool[i]);
                factoryCount++;
            }
        }
        else if (astroFilter % 100 == 0)
        {
            var star = GameMain.galaxy.StarById(astroFilter / 100);
            if (star == null) return 0;
            for (var i = 0; i < star.planetCount; i++)
            {
                var planet = star.planets[i];
                if (planet?.factory != null)
                {
                    var factoryIndex = planet.factoryIndex;
                    writer.Write(factoryIndex);
                    ExportProductStatExtension(writer, factoryStatPool[factoryIndex]);
                    factoryCount++;
                }
            }
        }
        else
        {
            var planet = GameMain.galaxy.PlanetById(astroFilter);
            if (planet?.factory == null) return 0;
            writer.Write(planet.factoryIndex);
            ExportProductStatExtension(writer, factoryStatPool[planet.factoryIndex]);
            factoryCount = 1;
        }
        return factoryCount;
    }

    static void ExportProductStatExtension(BinaryWriter writer, FactoryProductionStat factoryProductionStat)
    {
        writer.Write(factoryProductionStat.productCursor);
        for (var i = 1; i < factoryProductionStat.productCursor; i++)
        {
            var product = factoryProductionStat.productPool[i];
            writer.Write(product.itemId);
            writer.Write(product.refProductSpeed);
            writer.Write(product.refConsumeSpeed);
            writer.Write(product.storageCount);
            writer.Write(product.trashCount);
            writer.Write(product.importStorageCount);
            writer.Write(product.exportStorageCount);
        }
    }
}
