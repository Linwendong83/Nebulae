using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Authority.Adapters;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class DarkFogLifecycleTest
{
    private MultiplayerSession previousSession = null!;
    private GameData previousGame = null!;
    private AuthoritySession runtime = null!;
    private readonly Harmony harmony = new("nebulae.tests.dark-fog-lifecycle");

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session; previousGame = GameMain.data;
        Multiplayer.Session = Uninitialized<MultiplayerSession>();
        Multiplayer.Session.Client = Uninitialized<global::NebulaeNetwork.Client>();
        runtime = new AuthoritySession(new AuthoritySessionState());
        Multiplayer.Session.AuthorityRuntime = runtime;
        runtime.BeginAuthorityWorld(new AuthorityEpoch(111, 222), false);
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchSelf(); runtime.Dispose();
        Multiplayer.Session = previousSession; GameMain.data = previousGame;
    }

    private static SpaceEnemyState State(SpaceEnemyKind kind, SpaceEnemyActivity activity = default, int astro = 0) => new(
        kind, false, kind is SpaceEnemyKind.SpaceRelay or SpaceEnemyKind.SpaceTinder,
        8119, 291, 1, 0, 0, astro, 1000001, 0, 1, 0,
        100, 200, 300, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, activity: activity);

    private static (SpaceSector Sector, EnemyDFHiveSystem Hive) Space()
    {
        var sector = Uninitialized<SpaceSector>(); sector.maxHiveCount = 10;
        sector.SetEnemyCapacity(8); sector.enemyCursor = 3;
        sector.dfHivesByAstro = new EnemyDFHiveSystem[10]; sector.dfHives = new EnemyDFHiveSystem[1];
        var hive = new EnemyDFHiveSystem { hiveAstroId = 1000001, starData = new StarData { id = 1, index = 0 }, sector = sector };
        hive.relays = new ObjectPool<DFRelayComponent>(); hive.relays.Reset();
        hive.tinders = new DataPool<DFTinderComponent>(); hive.tinders.Reset();
        hive.builders = new DataPool<EnemyBuilderComponent>(); hive.builders.Reset();
        hive.cores = new DataPool<DFSCoreComponent>(); hive.cores.Reset();
        sector.dfHivesByAstro[1] = hive; sector.dfHives[0] = hive;
        sector.enemyPool[1] = new EnemyData { id = 1, originAstroId = 1000001, isSpace = true };
        sector.enemyPool[2] = new EnemyData { id = 2, originAstroId = 1000001, isSpace = true };
        return (sector, hive);
    }

    private static (PlanetFactory Factory, DFGBaseComponent Base) Ground()
    {
        var factory = new PlanetFactory { enemyPool = new EnemyData[8], enemyCursor = 3 };
        // ApplyBase refuses to touch a planet whose factory is not loaded; this fixture builds one.
        var planet = new PlanetData { id = 101, factory = factory, factoryLoaded = true };
        AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, planet);
        var system = Uninitialized<EnemyDFGroundSystem>(); factory.enemySystem = system;
        system.factory = factory; system.planet = planet;
        system.builders = new DataPool<EnemyBuilderComponent>(); system.builders.Reset(); system.builders.cursor = 3;
        system.bases = new ObjectPool<DFGBaseComponent>(); system.bases.Reset(); system.bases.cursor = 2;
        var item = new DFGBaseComponent
        {
            id = 1,
            enemyId = 1,
            builderId = 1,
            groundSystem = system,
            pbuilders = new GrowthPattern_DFGround.Builder[2]
        };
        item.InitFormations(); item.InitIncomingSkills(); system.bases.buffer[1] = item;
        system.builders.buffer[1] = new EnemyBuilderComponent { id = 1, enemyId = 1, sp = 60, spMax = 60, state = 3 };
        factory.enemyPool[1] = new EnemyData { id = 1, owner = 1, dfGBaseId = 1 };
        factory.enemyPool[2] = new EnemyData { id = 2, owner = 1 };
        return (factory, item);
    }

    [TestMethod]
    public void CoreManufactureFactsReachTheBriefAndPublishWithoutPoseChanges()
    {
        var (sector, hive) = Space();
        hive.cores.cursor = 2;
        hive.cores.buffer[1] = new DFSCoreComponent { id = 1, buildRelaySp = 12, buildRelaySpMax = 60, buildTinderSp = 25, buildTinderSpMax = 100 };
        sector.enemyPool[1].dfSCoreId = 1;
        var activity = SpaceEnemySnapshotAdapter.ReadActivity(sector, sector.enemyPool[1]);
        var input = State(SpaceEnemyKind.SpaceCore, activity);
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(input, out var bytes));
        TestAssert.HasCount(SpaceEnemyStateCodec.FixedSize + 24, bytes);
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryDecode(bytes, 0, bytes.Length, out var decoded, out _));
        var info = new EnemyBriefInfo(); EnemyBriefPresentation_Patch.Apply(info, decoded);
        TestAssert.AreEqual(12, info.buildRelaySp); TestAssert.AreEqual(60, info.buildRelaySpMax);
        TestAssert.AreEqual(25, info.buildTinderSp); TestAssert.AreEqual(100, info.buildTinderSpMax);
        TestAssert.IsTrue(EnemyStatePolicy.HasMoved(State(SpaceEnemyKind.SpaceCore), decoded, false));
        TestAssert.IsFalse(EnemyStatePolicy.HasMoved(input, decoded, false));
        for (var length = 0; length < bytes.Length; length++)
            TestAssert.IsFalse(SpaceEnemyStateCodec.TryDecode(bytes, 0, length, out _, out _));
    }

    [TestMethod]
    public void PassiveRelaySurvivesTheWholeDeploymentAndReturnWithoutAllocatingBuilders()
    {
        var (sector, hive) = Space();
        var first = 0;
        foreach (var stage in new[] { -2, -1, 0, 1, 2, 1, 0, -1, -2 })
        {
            var activity = new SpaceEnemyActivity(stage: stage, direction: stage == 2 ? 0 : 1, targetAstroId: 101, baseId: 3, baseState: 2);
            var input = State(SpaceEnemyKind.SpaceRelay, activity);
            TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(input, out var bytes));
            TestAssert.HasCount(SpaceEnemyStateCodec.FixedSize + 11, bytes);
            TestAssert.IsTrue(SpaceEnemyStateCodec.TryDecode(bytes, 0, bytes.Length, out var decoded, out _));
            SpaceEnemyPresentation.Install(sector, 1, decoded);
            if (first == 0) first = sector.enemyPool[1].dfRelayId;
            TestAssert.AreEqual(first, sector.enemyPool[1].dfRelayId);
            var relay = hive.relays.buffer[first];
            TestAssert.AreEqual(stage, relay.stage); TestAssert.AreEqual(101, relay.targetAstroId);
            TestAssert.AreEqual(3, relay.baseId); TestAssert.AreEqual(2, relay.baseState);
            TestAssert.AreEqual(1, hive.relayCount); TestAssert.AreEqual(0, hive.builders.count);
            TestAssert.IsTrue(activity.Same(SpaceEnemySnapshotAdapter.ReadActivity(sector, sector.enemyPool[1])));
        }
        SpaceEnemyPresentation.Forget(sector, 1);
        TestAssert.AreEqual(0, hive.relays.buffer[first].baseId, "Native removal cannot clear a new Base stream link.");
        hive.RemoveDFRelayComponent(first); TestAssert.AreEqual(0, hive.relayCount);
    }

    [TestMethod]
    public void TinderArrivingBeforeItsHiveBindsAfterCapacityGrowthAndKeepsItsCrossStarIdentity()
    {
        var (sector, hive) = Space(); sector.dfHivesByAstro[1] = null!;
        var input = State(SpaceEnemyKind.SpaceTinder, new SpaceEnemyActivity(stage: 0, direction: 1, targetAstroId: 1000009));
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(input, out var bytes));
        TestAssert.HasCount(SpaceEnemyStateCodec.FixedSize + 6, bytes);
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryDecode(bytes, 0, bytes.Length, out var decoded, out _));
        SpaceEnemyPresentation.Install(sector, 1, decoded);
        TestAssert.AreEqual(0, sector.enemyPool[1].dfTinderId);
        sector.SetEnemyCapacity(16); sector.dfHivesByAstro[1] = hive;
        SpaceEnemyPresentation.HiveReady(sector, hive);
        var id = sector.enemyPool[1].dfTinderId;
        TestAssert.IsGreaterThan(0, id, "Vanilla picking and cross-star search require dfTinderId.");
        TestAssert.AreEqual(1, hive.tinderCount);
        TestAssert.AreEqual(0, hive.tinders.buffer[id].targetHiveAstroId, "An out-of-order target summary must not crash native tooltips.");
        var target = new EnemyDFHiveSystem { hiveAstroId = 1000009 };
        sector.dfHivesByAstro[9] = target; SpaceEnemyPresentation.HiveReady(sector, target);
        TestAssert.AreEqual(1000009, hive.tinders.buffer[id].targetHiveAstroId);
        SpaceEnemyPresentation.Install(sector, 1, decoded);
        TestAssert.AreEqual(id, sector.enemyPool[1].dfTinderId);
        TestAssert.AreEqual(0, hive.builders.count);
        SpaceEnemyPresentation.Forget(sector, 1);
        TestAssert.AreEqual(0, hive.tinders.buffer[id].targetHiveAstroId);
        hive.RemoveDFTinderComponent(id); TestAssert.AreEqual(0, hive.tinderCount);
    }

    [TestMethod]
    public void InvalidTravelAndManufactureFactsAreRejected()
    {
        foreach (var activity in new[] { new SpaceEnemyActivity(stage: 3), new SpaceEnemyActivity(direction: 2),
                     new SpaceEnemyActivity(targetAstroId: 101), new SpaceEnemyActivity(baseId: 1) })
            TestAssert.IsFalse(SpaceEnemyStateCodec.TryEncode(State(SpaceEnemyKind.SpaceTinder, activity), out _));
        TestAssert.IsFalse(SpaceEnemyStateCodec.TryEncode(State(SpaceEnemyKind.SpaceCore, new SpaceEnemyActivity(relaySp: 2, relaySpMax: 1)), out _));
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(State(SpaceEnemyKind.SpaceRelay), out var valid));
        valid[SpaceEnemyStateCodec.FixedSize - 4] = 255;
        TestAssert.IsFalse(SpaceEnemyStateCodec.TryDecode(valid, 0, valid.Length, out _, out _));
    }

    [TestMethod]
    public void RealizingAnImportedVirtualHiveCreatesItsRenderBufferExactlyOnce()
    {
        var hive = new EnemyDFHiveSystem { pbuilders = new GrowthPattern_DFSpace.Builder[3] };
        hive.pbuilders[2] = new GrowthPattern_DFSpace.Builder { protoId = 8109, length = 100 };
        DarkFogReplicaBinding.PrepareHivePresentation(hive, false); TestAssert.IsNull(hive.antSegments);
        DarkFogReplicaBinding.PrepareHivePresentation(hive, true);
        TestAssert.IsNotNull(hive.antSegments); TestAssert.IsNotEmpty(hive.antSegments);
        var original = hive.antSegments;
        DarkFogReplicaBinding.PrepareHivePresentation(hive, true); TestAssert.AreSame(original, hive.antSegments);
        var empty = new EnemyDFHiveSystem { pbuilders = new GrowthPattern_DFSpace.Builder[2] };
        DarkFogReplicaBinding.PrepareHivePresentation(empty, true); TestAssert.IsNotEmpty(empty.antSegments);
    }

    [TestMethod]
    public void ANewEmptyHiveDoesNotHideOrBreakTheExistingSiblingChain()
    {
        var (sector, first) = Space();
        var second = new EnemyDFHiveSystem { starData = first.starData, sector = sector, realized = true };
        first.nextSibling = second; second.prevSibling = first;
        var added = new EnemyDFHiveSystem { starData = first.starData, sector = sector, realized = false };
        DarkFogReplicaBinding.AppendHive(sector, added);
        TestAssert.AreSame(first, sector.dfHives[0]); TestAssert.AreSame(added, second.nextSibling);
        TestAssert.AreSame(second, added.prevSibling); TestAssert.AreSame(first, added.firstSibling);
    }

    [TestMethod]
    public void SpaceAndStarmapRenderPosesFollowTheirOwnViewOnEveryDraw()
    {
        var pos = new VectorLF3(12000000, 8000000, 4000000);
        var halfTurn = new Quaternion(0, 1, 0, 0);
        SpaceEnemyPresentation.RenderPose(pos, Quaternion.identity, true, new VectorLF3(1, 2, 3), halfTurn,
            new VectorLF3(8000000, 4000000, 0), out var map, out var mapRotation);
        TestAssert.AreEqual(1000d, map.x); TestAssert.AreEqual(1000d, map.y); TestAssert.AreEqual(1000d, map.z);
        TestAssert.AreEqual(Quaternion.identity, mapRotation);
        SpaceEnemyPresentation.RenderPose(pos, Quaternion.identity, false, pos - new VectorLF3(10, 0, 0), halfTurn,
            default, out var world, out _);
        TestAssert.AreEqual(-10d, world.x, 1e-6);
    }

    [TestMethod]
    public void OnePassRemovalSamplingMatchesNativeForEveryCoreBuildingAndFormationCombination()
    {
        var (factory, item) = Ground(); var system = factory.enemySystem;
        byte[] flags = null!;
        for (var combination = 0; combination < 8; combination++)
        {
            system.builders.buffer[1].sp = (combination & 1) != 0 ? 60 : 0;
            system.builders.buffer[2] = (combination & 2) != 0 ? new EnemyBuilderComponent { id = 2, enemyId = 2 } : default;
            item.forms[0].SetPortCount(72); if ((combination & 4) != 0) item.forms[0].AddUnit();
            var expected = system.CheckBaseCanRemoved(1);
            flags = DarkFogSnapshotAdapter.SampleRemovalFlags(system, flags);
            TestAssert.AreEqual(expected, (int)flags[1]);
            TestAssert.AreSame(flags, DarkFogSnapshotAdapter.SampleRemovalFlags(system, flags));
        }
    }

    [TestMethod]
    public void DirectNativeBaseEncodingMatchesTheWireCodecIncludingLinksAndRemovalConditions()
    {
        var (factory, item) = Ground(); item.hiveAstroId = 1000001; item.relayEnemyId = 2;
        item.pbuilders[1] = new GrowthPattern_DFGround.Builder { instBuilderId = 1 };
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        DarkFogSnapshotAdapter.WriteBase(writer, item, 1);
        var bytes = stream.ToArray(); var decoded = GroundBaseStateCodec.Decode(bytes);
        TestAssert.IsTrue(decoded.CoreAlive); TestAssert.AreEqual((byte)1, decoded.RemovalFlags);
        TestAssert.AreEqual(1000001, decoded.HiveAstroId); TestAssert.AreEqual(2, decoded.RelayEnemyId);
        CollectionAssert.AreEqual(bytes, GroundBaseStateCodec.Encode(decoded));
        var reused = DarkFogSnapshotAdapter.SampleRemovalFlags(factory.enemySystem, null!);
        TestAssert.AreSame(reused, DarkFogSnapshotAdapter.SampleRemovalFlags(factory.enemySystem, reused));
    }

    [TestMethod]
    public void ClientCorePitAndRemovalQueriesUseHostFactsAcrossDestructionAndRebuilding()
    {
        harmony.CreateClassProcessor(typeof(DarkFogActivity_Patch)).Patch();
        var (factory, item) = Ground(); item.builderId = 0;
        var facts = runtime.DarkFogReplica;
        foreach (var live in new[] { true, false, true })
        {
            facts.BaseAlive[(101, 1)] = live;
            facts.BaseRemovalFlags[(101, 1)] = live ? (byte)3 : (byte)2;
            // Invoke through reflection so the JIT cannot inline the tiny native getter before
            // Harmony is installed by this test (the game installs patches before running it).
            TestAssert.AreEqual(!live, (bool)AccessTools.Method(typeof(EnemyDFGroundSystem), "IsBasePit").Invoke(factory.enemySystem, [1])!);
            TestAssert.AreEqual(live ? 3 : 2, (int)AccessTools.Method(typeof(EnemyDFGroundSystem), "CheckBaseCanRemoved").Invoke(factory.enemySystem, [1])!);
        }
        Multiplayer.Session.Client = null; Multiplayer.Session.Server = Uninitialized<global::NebulaeNetwork.Server>();
        item.builderId = 1;
        TestAssert.IsFalse((bool)AccessTools.Method(typeof(EnemyDFGroundSystem), "IsBasePit").Invoke(factory.enemySystem, [1])!);
        TestAssert.AreEqual(1, (int)AccessTools.Method(typeof(EnemyDFGroundSystem), "CheckBaseCanRemoved").Invoke(factory.enemySystem, [1])!);
    }

    [TestMethod]
    public void BaseLinksResolveClientLocalRelayIdsAndRejectRecycledEnemySlots()
    {
        var (sector, hive) = Space(); var (_, item) = Ground();
        item.hiveAstroId = 1000001; item.relayEnemyId = 2;
        var state = State(SpaceEnemyKind.SpaceRelay, new SpaceEnemyActivity(stage: 2, targetAstroId: 101, baseId: 1, baseState: 2));
        SpaceEnemyPresentation.Bind(sector, hive, 2, state);
        TestAssert.AreEqual(1, sector.enemyPool[2].dfRelayId, "Local relay id differs from its host enemy id.");
        TestAssert.AreSame(hive.relays.buffer[1], DarkFogActivity_Patch.FindRelay(sector, item));
        hive.relays.buffer[1].baseId = 2;
        TestAssert.IsNull(DarkFogActivity_Patch.FindRelay(sector, item));
    }

    [TestMethod]
    public void VanillaInterstellarPickingFindsThePassiveTinder()
    {
        var previousRadii = SkillSystem.RoughRadiusByModelIndex;
        try
        {
            var (sector, _) = Space();
            var game = Uninitialized<GameData>(); game.spaceSector = sector; game.relativeRot = Quaternion.identity;
            GameMain.data = game;
            var model = Uninitialized<SectorModel>();
            sector.model = model;
            var logic = new SpaceColliderLogic { gameData = game, customCastAll = new RaycastData[512] };
            AccessTools.Field(typeof(SpaceColliderLogic), "<sector>k__BackingField").SetValue(logic, sector);
            sector.enemyPool[1].pos = new VectorLF3(0, 0, 1000);
            SkillSystem.RoughRadiusByModelIndex = [10];
            SpaceEnemyPresentation.Install(sector, 1, State(SpaceEnemyKind.SpaceTinder));
            logic.RaycastAllEnemyUnitsAndForms(new Ray(Vector3.zero, Vector3.forward), 2000);
            TestAssert.AreEqual(1, logic.customCastAllCount);
            TestAssert.AreEqual(1, logic.customCastAll[0].objId);
            TestAssert.AreEqual(EObjectType.Enemy, logic.customCastAll[0].objType);
        }
        finally { SkillSystem.RoughRadiusByModelIndex = previousRadii; }
    }

    [TestMethod]
    public void RenderFramesRefreshTravelersWithoutPacketsOrUnrelatedUnitUploads()
    {
        var (sector, _) = Space();
        var game = Uninitialized<GameData>(); game.spaceSector = sector; game.relativeRot = Quaternion.identity;
        GameMain.data = game;
        var model = Uninitialized<SectorModel>();
        sector.model = model;
        model.gpuiManager = Uninitialized<SSGPUInstancingManager>();
        var renderer = Uninitialized<SpaceObjectRenderer>(); renderer.instPool = new SPACEOBJECT[2];
        model.gpuiManager.objectRenderers = [renderer];
        ref var enemy = ref sector.enemyPool[1];
        enemy.modelId = 1; enemy.pos = new VectorLF3(8000000, 4000000, 0); enemy.rot = Quaternion.identity;
        SpaceEnemyPresentation.Install(sector, 1, State(SpaceEnemyKind.SpaceTinder));
        game.relativePos = enemy.pos - new VectorLF3(100, 0, 0);
        SpaceEnemyPresentation.PrepareDraw(model, false); TestAssert.AreEqual(100f, renderer.instPool[1].posx);
        game.relativePos = enemy.pos - new VectorLF3(200, 0, 0);
        SpaceEnemyPresentation.PrepareDraw(model, false); TestAssert.AreEqual(200f, renderer.instPool[1].posx);
        TestAssert.IsFalse(model.enemyUnitsDirty);
        game.starmapViewPos = new VectorLF3(4000000, 0, 0);
        SpaceEnemyReplicaPools.RefreshPose(sector, 1, true);
        TestAssert.AreEqual(1000f, renderer.instPool[1].posx); TestAssert.AreEqual(1000f, renderer.instPool[1].posy);
        TestAssert.AreEqual(8000000d, enemy.pos.x, "Rendering never rewrites canonical enemy coordinates.");
    }

    [TestMethod]
    public void HostBaseSnapshotsCacheUnchangedBytesAndRestoreClientFactsAfterRemovalAndRejoin()
    {
        var (host, core) = Ground();
        core.hiveAstroId = 1000001; core.relayEnemyId = 2;
        core.pbuilders[1] = new GrowthPattern_DFGround.Builder { instBuilderId = 1 };
        var star = new StarData { id = 1, index = 0, planets = [host.planet] };
        var game = Uninitialized<GameData>(); game.galaxy = new GalaxyData { starCount = 1, stars = [star] };
        GameMain.data = game;
        var epoch = new AuthorityEpoch(111, 222);
        var scope = new ScopeKey(PoolKind.Base, 101);
        var adapter = new DarkFogSnapshotAdapter(epoch);
        var members = new List<ObjectKey>();
        TestAssert.IsTrue(adapter.TryReadMembers(scope, members)); var key = members.Single();
        TestAssert.IsTrue(adapter.TryReadState(key, out var intact));
        members.Clear(); adapter.TryReadMembers(scope, members);
        TestAssert.IsTrue(adapter.TryReadState(key, out var unchanged)); TestAssert.AreSame(intact, unchanged);
        // Only a surviving child changes the removal condition; the core's own pose/stats stay still.
        host.enemySystem.builders.buffer[2] = new EnemyBuilderComponent { id = 2, enemyId = 2 };
        members.Clear(); adapter.TryReadMembers(scope, members);
        TestAssert.IsTrue(adapter.TryReadState(key, out var withChild)); TestAssert.AreNotSame(intact, withChild);
        TestAssert.AreEqual((byte)3, GroundBaseStateCodec.Decode(withChild).RemovalFlags);
        host.enemySystem.builders.buffer[1].sp = 0;
        members.Clear(); adapter.TryReadMembers(scope, members);
        TestAssert.IsTrue(adapter.TryReadState(key, out var destroyed));
        host.enemySystem.builders.buffer[1].sp = 60;
        members.Clear(); adapter.TryReadMembers(scope, members);
        TestAssert.IsTrue(adapter.TryReadState(key, out var rebuilt));

        var (client, _) = Ground(); star.planets = [client.planet];
        harmony.CreateClassProcessor(typeof(DarkFogActivity_Patch)).Patch();
        foreach (var bytes in new[] { withChild, destroyed, rebuilt })
        {
            runtime.ApplyContext.Run(new ApplyScope(scope), () => runtime.DarkFogReplica.OnStateApplied(scope, key, 1, bytes));
            var expected = GroundBaseStateCodec.Decode(bytes);
            TestAssert.AreEqual(0, client.enemySystem.bases.buffer[1].builderId);
            TestAssert.AreEqual(expected.HiveAstroId, client.enemySystem.bases.buffer[1].hiveAstroId);
            TestAssert.AreEqual(expected.RelayEnemyId, client.enemySystem.bases.buffer[1].relayEnemyId);
            TestAssert.AreEqual(!expected.CoreAlive, (bool)AccessTools.Method(typeof(EnemyDFGroundSystem), "IsBasePit").Invoke(client.enemySystem, [1])!);
            TestAssert.AreEqual((int)expected.RemovalFlags, (int)AccessTools.Method(typeof(EnemyDFGroundSystem), "CheckBaseCanRemoved").Invoke(client.enemySystem, [1])!);
        }
        runtime.ApplyContext.Run(new ApplyScope(scope), () => runtime.DarkFogReplica.OnMemberRemoved(scope, key));
        TestAssert.IsFalse(runtime.DarkFogReplica.BaseRemovalFlags.ContainsKey((101, 1)));
        runtime.ApplyContext.Run(new ApplyScope(scope), () => runtime.DarkFogReplica.OnBaselineInstalled(scope, [new SnapshotMemberRecord(key, 2, rebuilt)]));
        TestAssert.IsTrue(runtime.DarkFogReplica.BaseAlive[(101, 1)]);
        TestAssert.AreEqual((byte)3, runtime.DarkFogReplica.BaseRemovalFlags[(101, 1)]);
    }
}
