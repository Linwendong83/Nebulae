using System.Net;
using HarmonyLib;
using global::NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority.Adapters;
using NebulaeWorld.Player;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
[DoNotParallelize]
public class OpenIssueRegressionTest
{
    private static PlayerData Checkpoint(long revision, bool alive = true, int deaths = 0) => new(900, 999, "untrusted-name")
    {
        Mecha = new MechaData { ReactorStorage = Storage(), Inventory = Storage(), CoreEnergy = revision },
        Life = new PlayerLifeData { Revision = revision, IsAlive = alive, DeathCount = deaths }
    };

    [TestMethod]
    public void InitialCheckpointAndReconnectKeepTheLatestInventoryForTheSameIdentity()
    {
        var data = new PlayerData(2, 101, "Alice") { PersistentId = "alice", DashboardData = [7] };
        var initial = Checkpoint(10);
        initial.Mecha.Inventory.grids[0].itemId = 1101;
        initial.Mecha.Inventory.grids[0].count = 23;
        TestAssert.IsTrue(data.TryApplyPersonalSnapshot(initial));
        var later = Checkpoint(20);
        later.Mecha.Inventory.grids[0].itemId = 1101;
        later.Mecha.Inventory.grids[0].count = 11;
        TestAssert.IsTrue(data.TryApplyPersonalSnapshot(later));
        TestAssert.IsFalse(data.TryApplyPersonalSnapshot(initial), "A retransmitted checkpoint must not duplicate the consumed items.");
        TestAssert.AreEqual(11, data.Mecha.Inventory.grids[0].count);
        TestAssert.AreEqual("alice", data.PersistentId);
        TestAssert.AreEqual("Alice", data.Username);
        TestAssert.AreEqual(101, data.LocalPlanetId);
        TestAssert.AreEqual((ushort)2, data.PlayerId);
        CollectionAssert.AreEqual(new byte[] { 7 }, data.DashboardData);
    }

    [TestMethod]
    public void RespawnCheckpointSurvivesAReplayOfTheDeathAndRefusesDeathCountRollback()
    {
        var data = new PlayerData();
        var dead = Checkpoint(10, false, 1);
        var revived = Checkpoint(20, true, 1);
        TestAssert.IsTrue(data.TryApplyPersonalSnapshot(dead));
        TestAssert.IsTrue(data.TryApplyPersonalSnapshot(revived));
        TestAssert.IsFalse(data.TryApplyPersonalSnapshot(dead));
        TestAssert.IsFalse(data.TryApplyPersonalSnapshot(Checkpoint(30, true, 0)));
        TestAssert.IsTrue(data.Life.IsAlive);
        TestAssert.AreEqual(1, data.Life.DeathCount);
        TestAssert.AreEqual(20d, data.Mecha.CoreEnergy);
    }

    [TestMethod]
    public void EmptyOrMalformedCheckpointCannotEraseAValidInventory()
    {
        var data = new PlayerData();
        TestAssert.IsTrue(data.TryApplyPersonalSnapshot(Checkpoint(10)));
        var mecha = data.Mecha;
        TestAssert.IsFalse(data.TryApplyPersonalSnapshot(new PlayerData { Life = new PlayerLifeData { Revision = 99 } }));
        TestAssert.AreNotEqual(global::NebulaeModel.Packets.Combat.Mecha.CheckpointReceiptStatus.Accepted, NebulaeWorld.Combat.PlayerLifeManager.StoreServerResult(data, [255, 255], out _));
        TestAssert.AreSame(mecha, data.Mecha);
    }

    [TestMethod]
    public void ReusingAnEndpointCreatesANewSeatAndAnOldCloseCannotRemoveIt()
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, 8469);
        var processor = new NebulaeNetPacketProcessor();
        var old = new NebulaeConnection(null, endpoint, processor) { ConnectionStatus = EConnectionStatus.Connected };
        var next = new NebulaeConnection(null, endpoint, processor) { ConnectionStatus = EConnectionStatus.Connected };
        TestAssert.AreNotEqual(old.Id, next.Id);
        TestAssert.IsFalse(old.Equals((INebulaeConnection)next));
        TestAssert.IsFalse(old == next);
        var players = new ConcurrentPlayerCollection();
        TestAssert.IsTrue(players.TryAdd(old, new NebulaePlayer(old, new PlayerData(2, 101))));
        TestAssert.IsTrue(players.TryAdd(next, new NebulaePlayer(next, new PlayerData(3, 101))));
        TestAssert.IsTrue(players.TryRemove(old, out var removed));
        TestAssert.AreEqual((ushort)2, removed.Id);
        TestAssert.IsFalse(players.TryRemove(old, out _));
        TestAssert.AreEqual((ushort)3, players.Get(next).Id);
    }

    [TestMethod]
    public void StatisticsFavoriteItemsAndSelectedFiltersRoundTripIndependently()
    {
        var preferences = new StatisticsFavorites { ProductionMask = 33, KillMask = 5, Production = new int[12000], Kills = new int[2048] };
        preferences.Production[1101] = 17;
        preferences.Kills[285] = 4;
        TestAssert.IsTrue(StatisticsFavorites.TryDecode(preferences.Encode(), out var decoded));
        TestAssert.AreEqual(33, decoded.ProductionMask);
        TestAssert.AreEqual(5, decoded.KillMask);
        TestAssert.AreEqual(17, decoded.Production[1101]);
        TestAssert.AreEqual(4, decoded.Kills[285]);
        TestAssert.IsFalse(StatisticsFavorites.TryDecode(preferences.Encode().Take(16).ToArray(), out _));
        TestAssert.IsFalse(StatisticsFavorites.TryDecode(preferences.Encode().Concat(new byte[] { 1 }).ToArray(), out _));
    }

    [TestMethod]
    public void HiveGeometryAnimationRoundTripsAndIsAppliedWithoutLogicComponents()
    {
        var state = new SpaceEnemyState(SpaceEnemyKind.SpaceCore, false, false, 8101, 273, 0, 0, 0,
            1000001, 1000001, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
            animationTime: 11.5f, prepareLength: -25f, workingLength: 2.5f, animationState: 1, animationPower: -1f);
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(state, out var bytes));
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryDecode(bytes, 0, bytes.Length, out var decoded, out _));
        var sector = Uninitialized<SpaceSector>();
        sector.enemyPool = new EnemyData[2];
        sector.enemyPool[1].id = 1;
        sector.enemyAnimPool = new AnimData[2];
        SpaceEnemyReplicaPools.WriteDisplayState(sector, 1, decoded);
        TestAssert.AreEqual(11.5f, sector.enemyAnimPool[1].time);
        TestAssert.AreEqual(-25f, sector.enemyAnimPool[1].prepare_length);
        TestAssert.AreEqual(2.5f, sector.enemyAnimPool[1].working_length);
        TestAssert.AreEqual(1u, sector.enemyAnimPool[1].state);
        TestAssert.AreEqual(-1f, sector.enemyAnimPool[1].power);
        TestAssert.AreEqual(0, sector.enemyPool[1].builderId);
        TestAssert.AreEqual(0, sector.enemyPool[1].dfSCoreId);
        TestAssert.AreEqual(0, sector.enemyPool[1].unitId);
    }

    [TestMethod]
    public void RemoteDroneRenderingRefreshesTheGpuWhenThereAreNoNativeDrones()
    {
        var previous = GameMain.data;
        try
        {
            var game = Uninitialized<GameData>();
            var history = Uninitialized<GameHistoryData>();
            history.constructionDroneSpeed = 12;
            game.history = history;
            GameMain.data = game;
            var planet = Uninitialized<PlanetData>();
            planet.id = 101;
            planet.factoryModel = Uninitialized<FactoryModel>();
            var factory = Uninitialized<PlanetFactory>();
            AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, planet);
            AccessTools.Field(typeof(PlanetFactory), "<gameData>k__BackingField").SetValue(factory, game);
            factory.prebuildPool = new PrebuildData[2]; // A target just completed on the host.
            factory.entityPool = new EntityData[2];
            var manager = new DroneManager();
            AccessTools.Field(typeof(DroneManager), "lastCheckedTick").SetValue(manager, GameMain.gameTick);
            var positions = (Dictionary<ushort, global::NebulaeModel.DataStructures.PlayerPosition>)AccessTools.Field(
                typeof(DroneManager), "cachedPositions").GetValue(manager);
            positions[2] = new(Vector3.zero, 101);
            var pool = (DataPool<DroneComponent>)AccessTools.Field(typeof(DroneManager), "drones").GetValue(manager);
            pool.cursor = 2;
            pool.buffer = new DroneComponent[2];
            pool.buffer[1] = new DroneComponent { id = 1, owner = 101, craftId = 0, stage = 2, targetObjectId = -1 };
            var crafts = (List<CraftData>)AccessTools.Field(typeof(DroneManager), "crafts").GetValue(manager);
            crafts.Add(new CraftData { owner = 2, modelIndex = 1, modelId = 1, pos = new VectorLF3(7, 8, 9) });
            var renderer = Uninitialized<DynamicRenderer>();
            renderer.instPool = new GPUOBJECT[2];
            renderer.extraPool = new Vector4[2];
            manager.UpdateDrones(factory, [null, renderer], true, 1f / 60f);
            TestAssert.IsTrue(planet.factoryModel.craftDirty);
            TestAssert.AreEqual(7f, renderer.instPool[1].posx);
            TestAssert.AreEqual(8f, renderer.instPool[1].posy);
            TestAssert.AreEqual(9f, renderer.instPool[1].posz);
            TestAssert.AreEqual(4f, renderer.extraPool[1].x);
        }
        finally { GameMain.data = previous; }
    }

    private static StorageComponent Storage()
    {
        var storage = Uninitialized<StorageComponent>();
        storage.size = 4;
        storage.grids = new StorageComponent.GRID[4];
        return storage;
    }

    [TestMethod]
    public void AutosavesRotateAuthorityDataTogetherAndNeverReuseAStaleCompanion()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nebulae-issue-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var suffix in new[] { ".server", ".server.server.authority" })
            {
                File.WriteAllText(Path.Combine(folder, "temporary" + suffix), "latest");
                File.WriteAllText(Path.Combine(folder, "slot0" + suffix), "previous");
                File.WriteAllText(Path.Combine(folder, "slot1" + suffix), "oldest");
            }
            global::NebulaeModel.Utils.ServerSaveRotation.Rotate(folder, "temporary", ["slot0", "slot1"],
                [".server", ".server.server.authority"]);
            TestAssert.AreEqual("latest", File.ReadAllText(Path.Combine(folder, "slot0.server")));
            TestAssert.AreEqual("latest", File.ReadAllText(Path.Combine(folder, "slot0.server.server.authority")));
            TestAssert.AreEqual("previous", File.ReadAllText(Path.Combine(folder, "slot1.server")));
            TestAssert.AreEqual("previous", File.ReadAllText(Path.Combine(folder, "slot1.server.server.authority")));
            File.WriteAllText(Path.Combine(folder, "temporary.server"), "legacy");
            global::NebulaeModel.Utils.ServerSaveRotation.Rotate(folder, "temporary", ["slot0", "slot1"],
                [".server", ".server.server.authority"]);
            TestAssert.IsFalse(File.Exists(Path.Combine(folder, "slot0.server.server.authority")));
            TestAssert.AreEqual("latest", File.ReadAllText(Path.Combine(folder, "slot1.server.server.authority")));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

    [TestMethod]
    public void SavingAFormerClientAsHostDoesNotWriteTheirOlderInventoryFirst()
    {
        var identity = "test-host-" + Guid.NewGuid().ToString("N");
        var data = new PlayerData();
        TestAssert.IsTrue(SaveManager.TryAdd(identity, data));
        try
        {
            var select = AccessTools.Method(typeof(SaveManager), "GetPlayerSaveEntries");
            var hosted = (KeyValuePair<string, global::NebulaeModel.Contracts.IPlayerData>[])select.Invoke(null, [identity]);
            TestAssert.IsFalse(hosted.Any(entry => entry.Key == identity));
            var dedicated = (KeyValuePair<string, global::NebulaeModel.Contracts.IPlayerData>[])select.Invoke(null, new object?[] { null });
            TestAssert.IsTrue(dedicated.Any(entry => entry.Key == identity));
        }
        finally { SaveManager.TryRemove(identity); }
    }
}
