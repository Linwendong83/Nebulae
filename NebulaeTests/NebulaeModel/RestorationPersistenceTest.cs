using HarmonyLib;
using NebulaeWorld.GameStates;
using global::NebulaeModel.DataStructures;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Utils;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class RestorationPersistenceTest
{
    [TestMethod]
    [DataRow(100L, 5000000000L, 5000000000L)]
    [DataRow(100L, 25L, 25L)]
    [DataRow(100L, 100L, 100L)]
    [DataRow(100L, 0L, 0L)]
    [DataRow(100L, -1L, 0L)]
    public void RestoringMechaDoesNotReportSandGainOrChangeCollectionStatistics(long initial, long saved, long expected)
    {
        var previousGame = GameMain.data;
        try
        {
            var game = Uninitialized<GameData>();
            var history = Uninitialized<GameHistoryData>();
            history.featureValues = new Dictionary<int, int> { [2130001] = 42 };
            game.history = history;
            GameMain.data = game;
            var player = Uninitialized<Player>();
            player.SetHiddenProperty("mecha", Uninitialized<Mecha>());
            player.SetHiddenProperty("package", EmptyStorage());
            player.SetHiddenProperty("deliveryPackage", EmptyDeliveryPackage());
            player.SetHiddenProperty(nameof(Player.sandCount), initial);
            var changes = new List<(long Total, long Delta, ESandSource Source)>();
            player.sandCountChanged += (total, delta, source) => changes.Add((total, delta, source));
            var snapshot = new MechaData
            {
                SandCount = saved,
                Inventory = EmptyStorage(),
                DeliveryPackage = EmptyDeliveryPackage(),
                FightData = Uninitialized<MechaFightData>()
            };

            snapshot.UpdateMech(player);
            snapshot.UpdateMech(player); // Reapplying a checkpoint must also stay silent.

            TestAssert.AreEqual(expected, player.sandCount);
            TestAssert.IsEmpty(changes);
            TestAssert.AreEqual(42, history.GetFeatureValue(2130001));

            player.SetSandCount(player.sandCount + 7, ESandSource.Other);

            TestAssert.HasCount(1, changes);
            TestAssert.AreEqual((expected + 7, 7L, ESandSource.Other), changes[0]);
            TestAssert.AreEqual(49, history.GetFeatureValue(2130001));

            player.SetSandCount(player.sandCount - 3, ESandSource.Other);

            TestAssert.HasCount(2, changes);
            TestAssert.AreEqual((expected + 4, -3L, ESandSource.Other), changes[1]);
        }
        finally { GameMain.data = previousGame; }
    }

    private static StorageComponent EmptyStorage()
    {
        var storage = Uninitialized<StorageComponent>();
        storage.grids = [];
        return storage;
    }

    private static DeliveryPackage EmptyDeliveryPackage()
    {
        var package = new DeliveryPackage();
        package.Init();
        return package;
    }

    [TestMethod]
    public void EmptyMechaSnapshotDoesNotBecomeADeadReturningPlayer()
    {
        var writer = new NetDataWriter();
        new MechaData().Serialize(writer);
        var restored = new MechaData();
        restored.Deserialize(new NetDataReader(writer));
        TestAssert.IsNull(restored.ReactorStorage);
        TestAssert.IsNull(restored.FightData);
    }

    [TestMethod]
    public void PlayerRevisionTenKeepsVegetationAndOlderRevisionsRemainReadable()
    {
        var collection = new VegetableCollection();
        collection.AddVegeToPlayer(1101, 3);
        var player = new PlayerData(7, 102, "test")
        {
            VegetableCollectionData = VegetableCollectionState.Capture(collection),
            Life = new PlayerLifeData
            {
                IsAlive = false,
                DeathCount = 4,
                RespawnMode = 3,
                RespawnStage = 1,
                RespawnTick = 42,
                Revision = 99,
                TransactionId = Guid.NewGuid().ToString("N"),
                RedeployItemsDropped = true
            }
        };
        var writer = new NetDataWriter(); player.Serialize(writer);
        var data = writer.CopyData();
        var restored = new PlayerData(); restored.Deserialize(new NetDataReader(data));
        TestAssert.IsFalse(restored.Life.IsAlive);
        TestAssert.AreEqual(4, restored.Life.DeathCount);
        TestAssert.IsTrue(restored.Life.RedeployItemsDropped);
        var restoredCollection = new VegetableCollection();
        VegetableCollectionState.Restore(restoredCollection, restored.VegetableCollectionData);
        TestAssert.AreEqual(3, restoredCollection.playerVegeDict[1101]);
        const int flightAndStarBytes = 1 + 1 + 3 * sizeof(double) + 3 * sizeof(float) + sizeof(int);
        var revisionTenBytes = data.Take(data.Length - flightAndStarBytes).ToArray();
        var revisionTenReader = new NetDataReader(revisionTenBytes);
        var revisionTen = new PlayerData(); revisionTen.Import(revisionTenReader, 10);
        CollectionAssert.AreEqual(player.VegetableCollectionData, revisionTen.VegetableCollectionData);
        TestAssert.AreEqual(0, revisionTenReader.AvailableBytes);
        var revisionNineBytes = revisionTenBytes.Take(revisionTenBytes.Length - sizeof(int) - player.VegetableCollectionData.Length).ToArray();
        var revisionNineReader = new NetDataReader(revisionNineBytes);
        var revisionNine = new PlayerData(); revisionNine.Import(revisionNineReader, 9);
        TestAssert.IsFalse(revisionNine.Life.IsAlive);
        TestAssert.IsEmpty(revisionNine.VegetableCollectionData);
        TestAssert.AreEqual(0, revisionNineReader.AvailableBytes);
        var lifeWriter = new NetDataWriter(); player.Life.Serialize(lifeWriter);
        var legacyBytes = revisionNineBytes.Take(revisionNineBytes.Length - lifeWriter.Length - sizeof(int)).ToArray();
        var revisionEightReader = new NetDataReader(legacyBytes);
        var legacy = new PlayerData(); legacy.Import(revisionEightReader, 8);
        TestAssert.IsTrue(legacy.Life.IsAlive);
        TestAssert.AreEqual("test", legacy.Username);
        TestAssert.IsNull(legacy.Mecha.ReactorStorage);
        TestAssert.AreEqual(0, revisionEightReader.AvailableBytes);
    }

    [TestMethod]
    public void PlayerRevisionElevenKeepsFlightStateAndTwelveAddsTheLocalStar()
    {
        var player = new PlayerData(7, 102, "test")
        {
            LocalStarId = 7,
            AcceptedMovementState = EMovementState.Sail,
            AcceptedWarping = true,
            AcceptedVelocityU = new Double3(1.5, -2.25, 3.75),
            AcceptedVelocityL = new Float3(4.5f, 5.5f, 6.5f)
        };
        var writer = new NetDataWriter(); player.Serialize(writer);
        var data = writer.CopyData();

        var current = new PlayerData(); current.Import(new NetDataReader(data), 12);
        TestAssert.AreEqual(EMovementState.Sail, current.AcceptedMovementState);
        TestAssert.IsTrue(current.AcceptedWarping);
        TestAssert.AreEqual(new Double3(1.5, -2.25, 3.75), current.AcceptedVelocityU);
        TestAssert.AreEqual(7, current.LocalStarId);

        // Revision 11 stopped right after the flight state, so the star falls back to "unknown"
        // while the flight state it did write still has to survive the import.
        var revisionElevenBytes = data.Take(data.Length - sizeof(int)).ToArray();
        var revisionEleven = new PlayerData(); revisionEleven.Import(new NetDataReader(revisionElevenBytes), 11);
        TestAssert.AreEqual(EMovementState.Sail, revisionEleven.AcceptedMovementState);
        TestAssert.AreEqual(new Float3(4.5f, 5.5f, 6.5f), revisionEleven.AcceptedVelocityL);
        TestAssert.AreEqual(0, revisionEleven.LocalStarId);
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    [DataRow(9)]
    [DataRow(10)]
    [DataRow(11)]
    [DataRow(12)]
    public void ConsecutiveSavedPlayersKeepRevisionBoundariesAndCanBeResaved(int revision)
    {
        var players = new[]
        {
            new PlayerData(7, 102, "first") { LocalStarId = 4, AcceptedMovementState = EMovementState.Sail },
            new PlayerData(8, 203, "second") { LocalStarId = 5 }
        };
        foreach (var player in players) player.Mecha.SandCount = revision >= 8 ? 5000000000L : 1234567;
        var entries = players.Select(player => SavedEmptyPlayer(player, revision)).ToArray();
        var reader = new NetDataReader(entries[0].Concat(entries[1]).ToArray());
        var rewritten = new NetDataWriter();
        for (var index = 0; index < players.Length; index++)
        {
            var restored = new PlayerData();
            restored.Import(reader, revision);
            TestAssert.AreEqual(entries.Take(index + 1).Sum(entry => entry.Length), reader.Position);
            TestAssert.AreEqual(players[index].Username, restored.Username);
            TestAssert.AreEqual(players[index].PlayerId, restored.PlayerId);
            TestAssert.AreEqual(players[index].Mecha.SandCount, restored.Mecha.SandCount);
            TestAssert.AreEqual(revision >= 12 ? players[index].LocalStarId : 0, restored.LocalStarId);
            TestAssert.AreEqual(revision >= 11 ? players[index].AcceptedMovementState : EMovementState.Walk, restored.AcceptedMovementState);
            restored.Serialize(rewritten);
        }
        TestAssert.AreEqual(0, reader.AvailableBytes);
        var currentReader = new NetDataReader(rewritten.CopyData());
        foreach (var player in players)
        {
            var restored = new PlayerData();
            restored.Deserialize(currentReader);
            TestAssert.AreEqual(player.Username, restored.Username);
            TestAssert.AreEqual(player.Mecha.SandCount, restored.Mecha.SandCount);
            TestAssert.IsTrue(restored.Life.IsAlive);
        }
        TestAssert.AreEqual(0, currentReader.AvailableBytes);
    }

    private static byte[] SavedEmptyPlayer(PlayerData player, int revision)
    {
        var writer = new NetDataWriter();
        if (revision >= 8)
        {
            player.Serialize(writer);
            var omitted = revision < 12 ? sizeof(int) : 0;
            if (revision < 11) omitted += 1 + 1 + 3 * sizeof(double) + 3 * sizeof(float);
            if (revision < 10) omitted += sizeof(int) + player.VegetableCollectionData.Length;
            if (revision < 9)
            {
                var life = new NetDataWriter(); player.Life.Serialize(life);
                omitted += life.Length + sizeof(int) + player.PersonalKillData.Length;
            }
            return writer.CopyData().Take(writer.Length - omitted).ToArray();
        }

        writer.Put(player.Username); writer.Put(player.PlayerId); writer.Put(player.LocalPlanetId);
        if (revision < 7) writer.Put(0); // MechaColors count.
        player.LocalPlanetPosition.Serialize(writer); player.UPosition.Serialize(writer);
        player.Rotation.Serialize(writer); player.BodyRotation.Serialize(writer);
        // Revisions 5-7 have a shorter bonus block, 32-bit sand and no combat payload.
        for (var field = 0; field < 13; field++) writer.Put(0d);
        writer.Put(0); writer.Put(0);
        for (var field = 0; field < 7; field++) writer.Put(0f);
        writer.Put(100); // Inventory size; drone count was introduced in revision 8.
        if (revision >= 7) { writer.Put(false); writer.Put(0); writer.Put(0); }
        writer.Put((int)player.Mecha.SandCount); writer.Put(0d); writer.Put(0d); writer.Put(false);
        writer.Put(false); // Appearance.
        if (revision >= 6) { writer.Put(false); writer.Put(0); } // DIY appearance and items.
        return writer.CopyData();
    }

    [TestMethod]
    public void TornFinalDropRecordIsRecoveredWithoutDiscardingCommittedDrops()
    {
        var root = Path.Combine(Path.GetTempPath(), "nebulae-restoration-tests");
        var directory = Path.GetFullPath(Path.Combine(root, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "drops.log");
            BinaryRecordLog.Append(path, [1, 2, 3]);
            using (var stream = new FileStream(path, FileMode.Append))
            using (var writer = new BinaryWriter(stream)) { writer.Write(100); writer.Write((byte)1); }
            var records = BinaryRecordLog.Read(path);
            TestAssert.HasCount(1, records);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, records[0]);
            BinaryRecordLog.Append(path, [4, 5]);
            TestAssert.HasCount(2, BinaryRecordLog.Read(path));
            TestAssert.IsTrue(File.Exists(path + ".interrupted"));
        }
        finally
        {
            if (directory.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void AtomicReplacementPreservesThePreviousValidFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nebulae-restoration-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "wallet");
        try
        {
            AtomicFile.Write(path, [1, 2]);
            AtomicFile.Write(path, [3, 4]);
            CollectionAssert.AreEqual(new byte[] { 3, 4 }, File.ReadAllBytes(path));
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, File.ReadAllBytes(path + ".bak"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}

[TestClass]
public class MechaCheckpointSerializationTest
{
    private GameData previousGame = null!;
    private bool previousStorageLoaded;
    private Harmony harmony = null!;

    [TestInitialize]
    public void Initialize()
    {
        previousGame = GameMain.data;
        previousStorageLoaded = StorageComponent.staticLoaded;
        GameMain.data = Uninitialized<GameData>();
        StorageComponent.staticLoaded = true;
        harmony = new Harmony("nebulae.tests.mecha-checkpoint-serialization");
        // Fleet asset setup needs Unity; all serialized storage and checkpoint reads stay native.
        harmony.Patch(AccessTools.Method(typeof(CombatModuleComponent), "Setup", [typeof(int), typeof(GameData)]),
            prefix: new HarmonyMethod(typeof(MechaCheckpointSerializationTest), nameof(SkipFleetAssets)));
    }

    [TestCleanup]
    public void Cleanup()
    {
        harmony.UnpatchSelf();
        StorageComponent.staticLoaded = previousStorageLoaded;
        GameMain.data = previousGame;
    }

    private static bool SkipFleetAssets() => false;

    [TestMethod]
    public void CombatPayloadMustEndAtItsDeclaredBoundary()
    {
        var writer = new NetDataWriter();
        Mecha().FightData.Serialize(writer);
        var bytes = writer.CopyData();
        const int lengthOffset = 61; // Fixed combat scalar header.
        var payloadOffset = lengthOffset + sizeof(int);
        TestAssert.AreEqual(bytes.Length - payloadOffset, BitConverter.ToInt32(bytes, lengthOffset));
        var invalid = bytes.Concat(new byte[] { 0 }).ToArray();
        BitConverter.GetBytes(invalid.Length - payloadOffset).CopyTo(invalid, payloadOffset - sizeof(int));
        TestAssert.Throws<ParseException>(() => new MechaFightData().Deserialize(new NetDataReader(invalid)));
    }

    [TestMethod]
    public void CombatPayloadRequiresTheStorageHeader()
    {
        var writer = new NetDataWriter();
        Mecha().FightData.Serialize(writer);
        var bytes = writer.CopyData();
        const int payloadOffset = 65;
        foreach (var header in new[] { -1, 0, 1, 3, int.MaxValue })
        {
            var invalid = bytes.ToArray();
            BitConverter.GetBytes(header).CopyTo(invalid, payloadOffset);
            TestAssert.Throws<ParseException>(() => new MechaFightData().Deserialize(new NetDataReader(invalid)));
        }
    }

    private static MechaData Mecha()
    {
        var delivery = new DeliveryPackage();
        delivery.Init();
        var mecha = new MechaData
        {
            SandCount = 5000000000L,
            CoreEnergy = 1234,
            ReactorEnergy = 5678,
            ReactorItemId = 1802,
            ReactorItemInc = 4,
            Inventory = new StorageComponent(4),
            DeliveryPackage = delivery,
            ReactorStorage = new StorageComponent(4),
            WarpStorage = new StorageComponent(1),
            Forge = new MechaForge { tasks = [] },
            ConstructionModule = new ConstructionModuleComponent(),
            FightData = new MechaFightData
            {
                Hp = 120,
                GroundCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] },
                SpaceCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] }
            },
            HandItem = 1001,
            HandCount = 3,
            HandInc = 9
        };
        mecha.Inventory.grids[0] = new StorageComponent.GRID { itemId = 1002, count = 7, inc = 14, stackSize = 100 };
        mecha.ResearchPoints.Add(1001, 3599);
        mecha.ResearchPoints.Add(1002, 0);
        return mecha;
    }

    private static PlayerData Player(MechaData mecha) => new(2, 103, "Lin")
    {
        Mecha = mecha,
        LocalStarId = 7,
        Life = new PlayerLifeData { IsAlive = true, Revision = 41 },
        PersonalKillData = new byte[] { 4, 5 },
        VegetableCollectionData = new byte[] { 6, 7 },
        AcceptedMovementState = EMovementState.Sail,
        AcceptedWarping = true,
        AcceptedVelocityU = new Double3(1, 2, 3),
        AcceptedVelocityL = new Float3(4, 5, 6)
    };

    [TestMethod]
    public void FullPlayerAndTransactionSnapshotsPreserveEveryCheckpointField()
    {
        var player = Player(Mecha());
        var bytes = SerializePlayer(player);
        var transaction = new global::NebulaeModel.DataStructures.MetadataTransaction
        { Id = Guid.NewGuid().ToString("N"), BeforePlayer = bytes, AfterPlayer = bytes };
        var received = global::NebulaeModel.DataStructures.MetadataTransaction.ImportWire(transaction.ExportWire());
        CollectionAssert.AreEqual(bytes, received.AfterPlayer);
        var restored = MetadataTransactionManager.ReadPlayer(received.AfterPlayer);
        TestAssert.IsNotNull(restored);
        TestAssert.AreEqual(5000000000L, restored.Mecha.SandCount);
        TestAssert.AreEqual(7, restored.Mecha.Inventory.grids[0].count);
        TestAssert.AreEqual(120, restored.Mecha.FightData.Hp);
        var mecha = (MechaData)restored.Mecha;
        TestAssert.AreEqual(1802, mecha.ReactorItemId);
        TestAssert.AreEqual(4, mecha.ReactorItemInc);
        TestAssert.AreEqual(3, mecha.HandCount);
        TestAssert.AreEqual(9, mecha.HandInc);
        TestAssert.AreEqual(3599, mecha.ResearchPoints[1001]);
        TestAssert.AreEqual(0, mecha.ResearchPoints[1002]);
        CollectionAssert.AreEqual(bytes, SerializePlayer(restored));
    }

    [TestMethod]
    [DataRow(0, 3, 2)]
    [DataRow(24, 3, 0)]
    [DataRow(40, 0, 0)]
    public void CurrentRevisionSaveImportsOptionalMechaCheckpointsAndRewritesCurrentFormat(
        int omittedBytes, int handCount, int researchCount)
    {
        var player = Player(Mecha());
        var bytes = SavedPlayerWithoutCheckpoints(player, omittedBytes);
        var reader = new NetDataReader(bytes.Concat(bytes).ToArray());
        for (var entry = 0; entry < 2; entry++)
        {
            var restored = new PlayerData();
            restored.Import(reader, 12);
            TestAssert.AreEqual((entry + 1) * bytes.Length, reader.Position);
            TestAssert.AreEqual(5000000000L, restored.Mecha.SandCount);
            TestAssert.AreEqual(7, restored.LocalStarId);
            var mecha = (MechaData)restored.Mecha;
            TestAssert.AreEqual(handCount, mecha.HandCount);
            TestAssert.AreEqual(handCount == 0 ? 0 : 9, mecha.HandInc);
            TestAssert.HasCount(researchCount, mecha.ResearchPoints);
            var rewritten = MetadataTransactionManager.ReadPlayer(SerializePlayer(restored));
            TestAssert.IsNotNull(rewritten);
            CollectionAssert.AreEqual(SerializePlayer(restored), SerializePlayer(rewritten));
        }
        TestAssert.AreEqual(0, reader.AvailableBytes);
    }

    [TestMethod]
    public void SavedMechaWithoutCheckpointsClearsPreviouslyReadInventory()
    {
        var mecha = Mecha();
        var player = Player(mecha);
        var saved = SavedPlayerWithoutCheckpoints(player, 40);
        var reader = new NetDataReader(saved);
        ReadPosition(reader);
        mecha.Import(reader, 12);
        TestAssert.AreEqual(0, mecha.HandItem);
        TestAssert.AreEqual(0, mecha.HandCount);
        TestAssert.AreEqual(0, mecha.HandInc);
        TestAssert.IsEmpty(mecha.ResearchPoints);
        TestAssert.AreEqual(0, mecha.ReactorItemId);
    }

    [TestMethod]
    public void SavedTransactionPlayerSnapshotsAreMigratedWithoutChangingTheirInventory()
    {
        var legacy = SavedPlayerWithoutCheckpoints(Player(Mecha()), 0);
        var upgraded = MetadataTransactionManager.ReadPlayer(MetadataTransactionManager.UpgradeSavedPlayer(legacy));
        TestAssert.IsNotNull(upgraded);
        TestAssert.AreEqual(7, upgraded.Mecha.Inventory.grids[0].count);
        TestAssert.AreEqual(3, ((MechaData)upgraded.Mecha).HandCount);
        TestAssert.AreEqual(3599, ((MechaData)upgraded.Mecha).ResearchPoints[1001]);
        TestAssert.AreEqual(41L, upgraded.Life.Revision);
    }

    [TestMethod]
    [DataRow(9)]
    [DataRow(10)]
    [DataRow(11)]
    [DataRow(12)]
    [DataRow(13)]
    public void UnversionedTransactionSnapshotsRecoverHistoricalPlayerTails(int revision)
    {
        var original = Player(Mecha());
        var bytes = SavedPlayerWithoutCheckpoints(original, 0);
        const int flightAndStar = 42;
        var omitted = revision < 11 ? flightAndStar : revision == 11 ? 4 : 0;
        if (revision == 9) omitted += 4 + original.VegetableCollectionData.Length;
        bytes = bytes.Take(bytes.Length - omitted).ToArray();
        if (revision == 13) bytes = SerializePlayer(original);
        var upgraded = MetadataTransactionManager.ReadPlayer(MetadataTransactionManager.UpgradeSavedPlayer(bytes));
        TestAssert.IsNotNull(upgraded);
        TestAssert.AreEqual(7, upgraded.Mecha.Inventory.grids[0].count);
        TestAssert.AreEqual(3, ((MechaData)upgraded.Mecha).HandCount);
        TestAssert.AreEqual(3599, ((MechaData)upgraded.Mecha).ResearchPoints[1001]);
        TestAssert.AreEqual(original.Life.Revision, upgraded.Life.Revision);
        TestAssert.AreEqual(original.Life.DeathCount, upgraded.Life.DeathCount);
        TestAssert.AreEqual(revision >= 12 ? original.LocalStarId : 0, upgraded.LocalStarId);
        TestAssert.AreEqual(revision >= 11 ? original.AcceptedMovementState : EMovementState.Walk, upgraded.AcceptedMovementState);
        CollectionAssert.AreEqual(revision >= 10 ? original.VegetableCollectionData : Array.Empty<byte>(), upgraded.VegetableCollectionData);
    }

    [TestMethod]
    public void LegacyJournalMigratesInMemoryAndRewritesWithExplicitFormat()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nebulae-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "journal");
            var bytes = SavedPlayerWithoutCheckpoints(Player(Mecha()), 0);
            var snapshot = bytes.Take(bytes.Length - 42).ToArray();
            var tx = new MetadataTransaction
            {
                Id = Guid.NewGuid().ToString("N"),
                Owner = "owner",
                State = MetadataTransactionState.Debited,
                BeforePlayer = snapshot,
                AfterPlayer = snapshot,
                Cost = [1, 2, 3, 4, 5, 6],
                Before = [10, 20, 30, 40, 50, 60],
                After = [11, 22, 33, 44, 55, 66]
            };
            WriteJournalFixture(path, 1, tx);
            var original = File.ReadAllBytes(path);
            var loaded = new Dictionary<string, MetadataTransaction>();
            AccessTools.Method(typeof(MetadataTransactionManager), "Load").Invoke(null, [path, loaded]);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            TestAssert.IsNotNull(MetadataTransactionManager.ReadPlayer(loaded[tx.Id].BeforePlayer));
            AccessTools.Method(typeof(MetadataTransactionManager), "Save").Invoke(null, [path, loaded]);
            using (var reader = new BinaryReader(File.OpenRead(path)))
            { TestAssert.AreEqual(2, reader.ReadInt32()); TestAssert.AreEqual(1, reader.ReadInt32()); }
            var again = new Dictionary<string, MetadataTransaction>();
            AccessTools.Method(typeof(MetadataTransactionManager), "Load").Invoke(null, [path, again]);
            var restored = again[tx.Id];
            TestAssert.AreEqual(tx.State, restored.State);
            TestAssert.AreEqual(tx.Owner, restored.Owner);
            CollectionAssert.AreEqual(tx.Cost, restored.Cost);
            CollectionAssert.AreEqual(tx.Before, restored.Before);
            CollectionAssert.AreEqual(tx.After, restored.After);
            CollectionAssert.AreEqual(loaded[tx.Id].Export(), restored.Export());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow("truncated-entry")]
    [DataRow("snapshot")]
    [DataRow("trailing")]
    [DataRow("unknown-journal")]
    [DataRow("unknown-snapshot")]
    public void InvalidJournalsPreserveOriginalFileAndDoNotPublishPartialRecovery(string failure)
    {
        var directory = Path.Combine(Path.GetTempPath(), "nebulae-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "journal");
            var good = new MetadataTransaction { Id = Guid.NewGuid().ToString("N"), BeforePlayer = SerializePlayer(Player(Mecha())) };
            var bad = new MetadataTransaction { Id = Guid.NewGuid().ToString("N"), BeforePlayer = good.BeforePlayer.Take(good.BeforePlayer.Length - 42).ToArray() };
            WriteJournalFixture(path, failure == "unknown-journal" ? 99 : 2, good, failure == "snapshot" ? bad : good);
            var bytes = File.ReadAllBytes(path);
            if (failure == "unknown-snapshot") Buffer.BlockCopy(BitConverter.GetBytes(99), 0, bytes, 4, 4);
            if (failure == "truncated-entry") bytes = bytes.Take(bytes.Length - 1).ToArray();
            if (failure == "trailing")
            {
                // Use distinct IDs so the failure exercises the trailing-data check.
                bad.BeforePlayer = good.BeforePlayer;
                WriteJournalFixture(path, 2, good, bad);
                bytes = File.ReadAllBytes(path).Append((byte)1).ToArray();
            }
            File.WriteAllBytes(path, bytes);
            var sentinel = new MetadataTransaction { Id = Guid.NewGuid().ToString("N") };
            var target = new Dictionary<string, MetadataTransaction> { [sentinel.Id] = sentinel };
            var error = TestAssert.Throws<System.Reflection.TargetInvocationException>(() =>
                AccessTools.Method(typeof(MetadataTransactionManager), "Load").Invoke(null, [path, target]));
            TestAssert.IsInstanceOfType<InvalidDataException>(error.InnerException);
            StringAssert.Contains(error.InnerException!.Message, path);
            TestAssert.HasCount(1, target);
            TestAssert.AreSame(sentinel, target[sentinel.Id]);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void WriteJournalFixture(string path, int version, params MetadataTransaction[] transactions)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(version);
        if (version != 1) writer.Write(1);
        writer.Write(transactions.Length);
        foreach (var tx in transactions) { var bytes = tx.Export(); writer.Write(bytes.Length); writer.Write(bytes); }
    }

    private static byte[] SavedPlayerWithoutCheckpoints(PlayerData player, int omittedBytes)
    {
        // Historical hand/research tails predate both the fuel and personal settings sections.
        omittedBytes += 3 * sizeof(int) + sizeof(int) + 2 * sizeof(bool) + sizeof(float);
        var bytes = SerializePlayer(player);
        var reader = new NetDataReader(bytes);
        ReadPosition(reader);
        new PlayerTechBonuses().Deserialize(reader);
        reader.GetLong(); reader.GetDouble(); reader.GetDouble(); reader.GetBool();
        var fight = new MechaFightData();
        fight.Deserialize(reader);
        var lengthOffset = reader.Position;
        var payloadLength = reader.GetInt();
        var payloadEnd = reader.Position + payloadLength;
        var saved = bytes.Take(payloadEnd - omittedBytes).Concat(bytes.Skip(payloadEnd)).ToArray();
        Buffer.BlockCopy(BitConverter.GetBytes(payloadLength - omittedBytes), 0, saved, lengthOffset, sizeof(int));
        return saved;
    }

    private static void ReadPosition(NetDataReader reader)
    {
        reader.GetString(); reader.GetUShort(); reader.GetInt();
        reader.GetFloat3(); reader.GetDouble3(); reader.GetFloat3(); reader.GetFloat3();
    }
}
