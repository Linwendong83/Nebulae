using System.Runtime.Serialization;
using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Authority.Adapters;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class HivePresentationTest
{
    private static T Uninitialized<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
    private static int opened, closed;
    private static bool Open() { opened++; return false; }
    private static bool Close() { closed++; return false; }
    private static bool SkipPose() => false;

    private static (SpaceSector Sector, EnemyDFHiveSystem Hive) Space(int starId = 1)
    {
        var slot = (starId - 1) * 8 + 1;
        var astroId = 1000000 + slot;
        var capacity = Math.Max(18, starId * 8 + 2);
        var sector = Uninitialized<SpaceSector>(); sector.maxHiveCount = capacity;
        sector.SetEnemyCapacity(8); sector.enemyCursor = 3;
        var orbit = new AstroOrbitData { orbitalPeriod = 3600, orbitRadius = 2 };
        var star = new StarData { id = starId, index = starId - 1, hiveAstroOrbits = [orbit] };
        sector.galaxy = new GalaxyData { starCount = starId, stars = new StarData[starId] };
        sector.galaxy.stars[starId - 1] = star;
        sector.dfHives = new EnemyDFHiveSystem[starId]; sector.dfHivesByAstro = new EnemyDFHiveSystem[capacity];
        sector.astros = new AstroData[capacity]; sector.astros[slot].id = astroId;
        var hive = new EnemyDFHiveSystem
        {
            sector = sector,
            starData = star,
            hiveAstroId = astroId,
            hiveAstroOrbit = orbit,
            pbuilders = new GrowthPattern_DFSpace.Builder[3]
        };
        hive.pbuilders[1] = new GrowthPattern_DFSpace.Builder { protoId = 8105 };
        hive.pbuilders[2] = new GrowthPattern_DFSpace.Builder { protoId = 8109, length = 100 };
        hive.builders = new DataPool<EnemyBuilderComponent>(); hive.builders.Reset();
        hive.connectors = new DataPool<DFSConnectorComponent>(); hive.connectors.Reset();
        hive.units = new DataPool<EnemyUnitComponent>(); hive.units.Reset();
        hive.relays = new ObjectPool<DFRelayComponent>(); hive.relays.Reset();
        hive.tinders = new DataPool<DFTinderComponent>(); hive.tinders.Reset();
        hive.InitFormations();
        sector.dfHives[starId - 1] = hive; sector.dfHivesByAstro[slot] = hive;
        sector.enemyPool[1] = new EnemyData { id = 1, protoId = 8109, originAstroId = astroId, astroId = astroId };
        return (sector, hive);
    }

    private static SpaceEnemyState Building() => new(SpaceEnemyKind.SpaceConnector, false, false,
        8109, 0, 0, 0, 0, 1000001, 1000001, 0, 2, 0,
        0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
        builderSp: 100, builderSpMax: 100, builderState: 4);

    [TestMethod]
    public void DistantCoreStatusComesFromTheDirectoryEvenAfterItsShellIsRemoved()
    {
        var session = Multiplayer.Session;
        var runtime = new AuthoritySession(new AuthoritySessionState());
        try
        {
            runtime.BeginAuthorityWorld(new AuthorityEpoch(10, 20), false);
            Multiplayer.Session = Uninitialized<MultiplayerSession>();
            Multiplayer.Session.Client = Uninitialized<global::NebulaeNetwork.Client>();
            Multiplayer.Session.AuthorityRuntime = runtime;
            var (_, hive) = Space(); hive.pbuilders[1].instId = 0;
            runtime.DarkFogReplica.HiveRoots[hive.hiveAstroId] = 1234;
            TestAssert.AreEqual(1234, HivePatternStorage.CoreEnemyId(hive, 1));
            runtime.DarkFogReplica.HiveRoots[hive.hiveAstroId] = 0;
            hive.pbuilders[1].instId = 1234;
            TestAssert.AreEqual(0, HivePatternStorage.CoreEnemyId(hive, 1));
            var targets = NebulaePatcher.Patches.Dynamic.Combat.HiveMonitorCoreAccess_Patch.TargetMethods().ToArray();
            TestAssert.IsNotEmpty(targets);
            foreach (var method in targets)
            {
                var code = NebulaePatcher.Patches.Dynamic.Combat.HiveMonitorCoreAccess_Patch.Transpiler(PatchProcessor.GetOriginalInstructions(method));
                code = NebulaePatcher.Patches.Dynamic.Combat.HivePatternAccess_Patch.Transpiler(code).ToArray();
                TestAssert.IsTrue(code.Any(instruction => instruction.Calls(AccessTools.Method(typeof(HivePatternStorage), nameof(HivePatternStorage.CoreEnemyId)))));
            }
        }
        finally { runtime.Dispose(); Multiplayer.Session = session; }
    }

    [TestMethod]
    public void GlobalDirectorySlicesFiveThousandHivesAndAuditsANewDirectoryEntry()
    {
        var previous = GameMain.data;
        try
        {
            var sector = Uninitialized<SpaceSector>();
            sector.dfHivesByAstro = new EnemyDFHiveSystem[5002];
            for (var i = 1; i <= 5000; i++) sector.dfHivesByAstro[i] = new EnemyDFHiveSystem();
            GameMain.data = Uninitialized<GameData>(); GameMain.data.spaceSector = sector;
            var adapter = new DarkFogSnapshotAdapter(new AuthorityEpoch(10, 20));
            var scope = new ScopeKey(PoolKind.HiveSummary, 0);
            var members = new List<ObjectKey>(); TestAssert.IsTrue(adapter.TryReadMembers(scope, members));
            TestAssert.HasCount(5000, members);
            var changed = new List<ObjectKey>(); var removed = new List<ObjectKey>();
            TestAssert.IsTrue(adapter.TryReadChanges(scope, GameMain.gameTick + 1, changed, removed));
            TestAssert.IsTrue(changed.Count is >= 83 and <= 84, "Only one sixtieth of the directory is sampled on a normal frame.");
            sector.dfHivesByAstro[5001] = new EnemyDFHiveSystem(); adapter.InvalidateDirectory();
            TestAssert.IsFalse(adapter.TryReadChanges(scope, GameMain.gameTick + 2, changed, removed));
            members.Clear(); TestAssert.IsTrue(adapter.TryReadMembers(scope, members)); TestAssert.HasCount(5001, members);
        }
        finally { GameMain.data = previous; }
    }

    [TestMethod]
    public void FormationPortChangesDoNotReencodeTheGlobalDirectory()
    {
        var previous = GameMain.data;
        try
        {
            var (sector, hive) = Space(); hive.forms[0].AddUnit();
            GameMain.data = Uninitialized<GameData>(); GameMain.data.spaceSector = sector;
            var adapter = new DarkFogSnapshotAdapter(new AuthorityEpoch(10, 20));
            var directory = new ScopeKey(PoolKind.HiveSummary, 0); var details = new ScopeKey(PoolKind.Hive, hive.hiveAstroId);
            var keys = new List<ObjectKey>(); adapter.TryReadMembers(directory, keys);
            TestAssert.IsTrue(adapter.TryReadState(keys.Single(), out var summary));
            var detailKeys = new List<ObjectKey>(); adapter.TryReadMembers(details, detailKeys);
            TestAssert.IsTrue(adapter.TryReadState(detailKeys.Single(), out var formation));
            hive.forms[0].units[1] = 2;
            TestAssert.IsTrue(adapter.TryReadState(keys.Single(), out var same)); TestAssert.AreSame(summary, same);
            TestAssert.IsTrue(adapter.TryReadState(detailKeys.Single(), out var changed));
            TestAssert.IsFalse(formation.SequenceEqual(changed));
        }
        finally { GameMain.data = previous; }
    }

    [TestMethod]
    public void RemoteWarningLinesWaitForTheirEnemyInsteadOfIndexingOutsideTheCroppedPool()
    {
        var previous = GameMain.data; var session = Multiplayer.Session;
        try
        {
            var (sector, _) = Space(); GameMain.data = Uninitialized<GameData>(); GameMain.data.spaceSector = sector;
            Multiplayer.Session = Uninitialized<MultiplayerSession>(); Multiplayer.Session.Client = Uninitialized<global::NebulaeNetwork.Client>();
            var entry = Uninitialized<UIBroadcastWarningEntry>();
            entry.broadcastData = new BroadcastData { vocal = EBroadcastVocal.LandingRelay, context = 70000 };
            TestAssert.IsFalse(NebulaePatcher.Patches.Dynamic.World.RemoteWarningLine_Patch.Prefix(entry));
            entry.broadcastData.context = 1;
            TestAssert.IsTrue(NebulaePatcher.Patches.Dynamic.World.RemoteWarningLine_Patch.Prefix(entry));
        }
        finally { GameMain.data = previous; Multiplayer.Session = session; }
    }

    [TestMethod]
    public void BuildingsArrivingBeforeTheirHiveRestoreLayoutAndAntsAfterRealizationAndPoolGrowth()
    {
        var (sector, hive) = Space(); sector.dfHivesByAstro[1] = null!;
        var creates = 0; var removes = 0;
        hive.onBuilderCreate += (_, _) => creates++;
        hive.onBuilderRemove += _ => removes++;
        SpaceEnemyPresentation.Install(sector, 1, Building());
        TestAssert.AreEqual(0, sector.enemyPool[1].builderId);
        sector.SetEnemyCapacity(16); sector.dfHivesByAstro[1] = hive;
        SpaceEnemyPresentation.HiveReady(sector, hive);
        var builder = sector.enemyPool[1].builderId;
        var connector = sector.enemyPool[1].dfSConnectorId;
        TestAssert.IsGreaterThan(0, builder); TestAssert.IsGreaterThan(0, connector);
        TestAssert.AreEqual(100f, hive.connectors.buffer[connector].length);
        TestAssert.AreEqual(1, hive.pbuilders[2].instId);
        TestAssert.AreEqual(builder, hive.pbuilders[2].instBuilderId);
        DarkFogReplicaBinding.PrepareHivePresentation(hive, true);
        SpaceEnemyPresentation.HiveReady(sector, hive);
        TestAssert.IsTrue(hive.antSegments.Take(5).All(tag => tag > 0));
        SpaceEnemyPresentation.Install(sector, 1, Building());
        TestAssert.AreEqual(builder, sector.enemyPool[1].builderId);
        TestAssert.AreEqual(1, hive.builders.count); TestAssert.AreEqual(1, creates);
        SpaceEnemyPresentation.Forget(sector, 1);
        TestAssert.AreEqual(0, hive.builders.count); TestAssert.AreEqual(0, hive.connectors.count);
        TestAssert.AreEqual(0, hive.pbuilders[2].instId); TestAssert.AreEqual(1, removes);
        TestAssert.IsTrue(hive.antSegments.All(tag => tag == 0));
    }

    [TestMethod]
    public void NativeFormationCountsVacanciesAndAllThreeTagsSurvivePackedSnapshots()
    {
        var (_, hive) = Space();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        for (var form = 0; form < 3; form++)
        {
            var units = hive.forms[form];
            for (var port = 1; port < units.units.Length; port++) { units.AddUnit(); units.units[port] = port % 4; }
            HiveFormationStateCodec.Write(writer, form, units.units);
        }
        TestAssert.AreEqual(392L, stream.Length);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        for (var form = 0; form < 3; form++)
        {
            var restored = new EnemyFormation();
            DarkFogReplicaBinding.InstallFormation(restored, HiveFormationStateCodec.Read(reader, form));
            CollectionAssert.AreEqual(hive.forms[form].units, restored.units);
            TestAssert.AreEqual(restored.units.Count(tag => tag > 0), restored.unitCount);
            TestAssert.AreEqual(restored.units.Count(tag => tag == 0) - 1, restored.vacancyCount);
            TestAssert.IsGreaterThan(0, restored.AddUnit());
        }
        TestAssert.Throws<EndOfStreamException>(() => HiveFormationStateCodec.Read(reader, 0));
        TestAssert.Throws<InvalidDataException>(() => HiveFormationStateCodec.Read(new BinaryReader(new MemoryStream([0, 0xf0])), 2));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4096)]
    [DataRow(8191)]
    public void HiveDetailsPublishFormationChangesSeparatelyFromTheGlobalDirectory(int starId)
    {
        var previous = GameMain.data; var session = Multiplayer.Session;
        var runtime = new AuthoritySession(new AuthoritySessionState());
        var harmony = new Harmony("nebulae.tests.hive-summary-formation");
        try
        {
            // Unity's LookRotation internal call is unavailable in the managed test runner.
            harmony.Patch(AccessTools.Method(typeof(AstroOrbitData), nameof(AstroOrbitData.PredictPose)),
                prefix: new HarmonyMethod(typeof(HivePresentationTest), nameof(SkipPose)));
            var (sector, hive) = Space(starId);
            var game = Uninitialized<GameData>(); game.galaxy = sector.galaxy; game.spaceSector = sector;
            GameMain.data = game;
            var epoch = new AuthorityEpoch(111, 222);
            var scope = new ScopeKey(PoolKind.Hive, hive.hiveAstroId);
            var adapter = new DarkFogSnapshotAdapter(epoch); var members = new List<ObjectKey>();
            TestAssert.IsTrue(adapter.TryReadMembers(scope, members));
            TestAssert.IsTrue(adapter.TryReadState(members.Single(), out var first));
            TestAssert.IsLessThanOrEqualTo(AuthorityLimits.StateRecordMaxBytes, first.Length);
            var fields = AuthorityStateFields.Describe(PoolKind.Hive, first);
            TestAssert.IsNotNull(fields);
            TestAssert.IsTrue(adapter.TryReadState(members.Single(), out var same)); TestAssert.AreSame(first, same);
            hive.forms[0].AddUnit(); hive.forms[2].AddUnit();
            TestAssert.IsTrue(adapter.TryReadState(members.Single(), out var next));
            TestAssert.IsFalse(first.SequenceEqual(next));
            TestAssert.IsTrue(FieldDeltaCodec.TryEncode(first, fields, next,
                AuthorityStateFields.Describe(PoolKind.Hive, next), out _));
            hive.forms[0].Clear(); hive.forms[2].Clear();
            Multiplayer.Session = Uninitialized<MultiplayerSession>(); Multiplayer.Session.AuthorityRuntime = runtime;
            runtime.BeginAuthorityWorld(epoch, false);
            runtime.ApplyContext.Run(new ApplyScope(scope), () => runtime.DarkFogReplica.OnStateApplied(scope, members.Single(), 1, next));
            TestAssert.AreEqual(1, hive.forms[0].units[1]); TestAssert.AreEqual(1, hive.forms[2].units[1]);
            TestAssert.AreEqual(1, hive.forms[0].unitCount); TestAssert.AreEqual(1, hive.forms[2].unitCount);
        }
        finally { harmony.UnpatchSelf(); runtime.Dispose(); GameMain.data = previous; Multiplayer.Session = session; }
    }

    [TestMethod]
    public void MultipleViewersAndTheHostKeepOneVirtualPreviewUntilTheLastViewerLeaves()
    {
        var (sector, hive) = Space();
        var manager = new EnemyManager(); var harmony = new Harmony("nebulae.tests.hive-preview-lifetime");
        opened = closed = 0;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(EnemyDFHiveSystem), "InstantiateEnemies"), prefix: new HarmonyMethod(typeof(HivePresentationTest), nameof(Open)));
            harmony.Patch(AccessTools.Method(typeof(EnemyDFHiveSystem), "UninstantiateEnemies"), prefix: new HarmonyMethod(typeof(HivePresentationTest), nameof(Close)));
            // The realized sibling used to stop the native starmap loop before this virtual hive.
            var realized = new EnemyDFHiveSystem { realized = true, hiveAstroId = 1000002, nextSibling = hive };
            sector.dfHives[0] = realized;
            manager.QueueHivePreview(1, 1); manager.QueueHivePreview(2, 1);
            TestAssert.AreEqual(0, opened, "Socket callbacks must not mutate game pools.");
            manager.TickHivePreviews(sector); manager.TickHivePreviews(sector);
            TestAssert.AreEqual(1, opened); TestAssert.IsTrue(hive.isPreview); TestAssert.IsFalse(hive.realized);
            manager.QueueHivePreview(1, 0); manager.TickHivePreviews(sector); TestAssert.AreEqual(0, closed);
            manager.OpenLocalHivePreview(hive);
            manager.QueueHivePreview(2, 0); manager.TickHivePreviews(sector); TestAssert.AreEqual(0, closed);
            manager.CloseLocalHivePreview(hive); TestAssert.AreEqual(1, closed); TestAssert.IsFalse(hive.isPreview);
            manager.QueueHivePreview(1, 1); manager.TickHivePreviews(sector); TestAssert.AreEqual(2, opened);
            manager.QueueHivePreview(1, 999); manager.TickHivePreviews(sector); TestAssert.AreEqual(1, closed);
            manager.QueueHivePreview(1, 0); manager.TickHivePreviews(sector); TestAssert.AreEqual(2, closed);
        }
        finally { harmony.UnpatchSelf(); manager.Dispose(); }
    }
}
