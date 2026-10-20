#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameStates;
using NebulaeModel.Packets.Planet;
using NebulaeModel.Packets.Session;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Planet;
internal class FactoryLoadRequestProcessor : PacketProcessor<FactoryLoadRequest>
{
    protected override void ProcessPacket(FactoryLoadRequest packet, NebulaeConnection conn)
    {
        if (IsClient)
        {
            return;
        }

        var planet = GameMain.galaxy.PlanetById(packet.PlanetID);
        if (planet == null || (Players.Get(conn, EConnectionStatus.Syncing) == null &&
                               Players.Get(conn, EConnectionStatus.Connected) == null)) return;
        Multiplayer.Session.Planets.HostPreparation.RequestFactory(planet, conn,
            () => SendFactory(planet, conn), error =>
            {
                NebulaeModel.Logger.Log.Error($"Factory preparation failed on planet {planet.id}", error);
                conn.peerSocket.Close((ushort)DisconnectionReason.InvalidData, "Factory preparation failed");
            });
    }

    private static void SendFactory(PlanetData planet, NebulaeConnection conn)
    {
        NebulaeWorld.GameStates.JoinSyncManager.WaitForSimulation();
        Multiplayer.Session.Ships.FlushLifecycle();
        var started = NebulaeModel.DataStructures.LoadingTimings.Now;
        var factory = GameMain.data.GetOrCreateFactory(planet);
        GameMain.galaxy.astrosFactory[planet.id] = factory;
        OnPlanetFactoryLoad(planet);
        Multiplayer.Session.BuildDispatch.SeedForSnapshot(factory);

        using (var writer = new BinaryUtils.Writer())
        {
            factory.Export(writer.BinaryWriter.BaseStream, writer.BinaryWriter);
            var data = writer.CloseAndGetBytes();
            var assignments = Multiplayer.Session.BuildDispatch.ExportSnapshot(planet.id);
            var snapshot = new FactoryData(planet.id, data, planet.data.modData)
            { EnemyGenerations = Multiplayer.Session.Generations.Export(planet.id), BuildAssignments = assignments };
            if (Multiplayer.Session.Server.Players.Get(conn, EConnectionStatus.Syncing) != null)
                conn.SendJoinTransfer(JoinDataStage.Factory, planet.id, new[] { conn.SerializePacket(snapshot) });
            else
            {
                conn.SendPacket(new FragmentInfo(data.Length + planet.data.modData.Length + assignments.Length));
                conn.SendPacket(snapshot);
            }
            Multiplayer.Session.JoinSync.Checkpoint(conn, planet.id);
        }
        Multiplayer.Session.Planets.Timings.Record("factory-export", NebulaeModel.DataStructures.LoadingTimings.Elapsed(started));

        // Update syncing player data (Connected player will be update by movement packets)
        var player = Multiplayer.Session.Server.Players.Get(conn, EConnectionStatus.Syncing);
        if (player != null)
        {
            player.Data.LocalPlanetId = planet.id;
            player.Data.LocalStarId = planet.star.id;
            // Subscription eligibility reads this accepted location directly.
        }
    }

    static void OnPlanetFactoryLoad(PlanetData planet)
    {
        // Realize planet bases before sending the factory data. This is the host lifecycle
        // transaction of the load: bases must exist as objects before any snapshot or replication
        // read of this factory, and it is kept as its own step so that reading never mixes with
        // mutating.
        // Damaged state ships as canonical replication state; a load request never heals.
        RealizeDarkFogPlanetBases(planet);
    }

    static void RealizeDarkFogPlanetBases(PlanetData planet)
    {
        var spaceSector = GameMain.data.spaceSector;
        var enemyDFHiveSystem = spaceSector.dfHives[planet.star.index];
        while (enemyDFHiveSystem != null)
        {
            for (var i = 0; i < enemyDFHiveSystem.relays.cursor; i++)
            {
                var dfrelayComponent = enemyDFHiveSystem.relays[i];
                if (dfrelayComponent?.targetAstroId == planet.astroId && dfrelayComponent.baseState == 1 && dfrelayComponent.stage == 2)
                {
                    dfrelayComponent.RealizePlanetBase(spaceSector);
                }
            }
            enemyDFHiveSystem = enemyDFHiveSystem.nextSibling;
        }
    }

}
