using HarmonyLib;
using NebulaeModel.Packets.Factory;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority.Adapters;
using NebulaeWorld.Factory;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class BuildingDeathVisualTest
{
    private Harmony harmony = null!;
    private GameData previousGame = null!;
    private MultiplayerSession previousSession = null!;
    private PrefabDesc[] previousPrefabs = null!;
    private PlanetFactory factory = null!;
    private static ModelProtoSet models = null!;
    private static readonly List<object[]> wreckageCalls = new();
    private static int removals;

    [TestInitialize]
    public void SetUp()
    {
        previousGame = GameMain.data; previousSession = Multiplayer.Session;
        previousPrefabs = PlanetFactory.PrefabDescByModelIndex;
        models = Uninitialized<ModelProtoSet>();
        models.dataArray = [new ModelProto { ID = 1, RuinType = ERuinType.Normal, prefabDesc = new PrefabDesc() }];
        SetField(models, "dataIndices", new Dictionary<int, int> { [1] = 0 });
        var handler = Uninitialized<WreckageHandler>();
        SetStatic(typeof(PlanetFactory), nameof(PlanetFactory.PrefabDescByModelIndex), new PrefabDesc[3]);
        PlanetFactory.PrefabDescByModelIndex[1] = new PrefabDesc
        { wreckagePrefab = handler, wreckageFragments = [[], [], []] };
        harmony = new Harmony("nebulae.tests.building-death-visuals");
        harmony.Patch(AccessTools.PropertyGetter(typeof(LDB), nameof(LDB.models)),
            prefix: new HarmonyMethod(typeof(BuildingDeathVisualTest), nameof(GetModels)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(GameMain), nameof(GameMain.gpuiManager)),
            prefix: new HarmonyMethod(typeof(BuildingDeathVisualTest), nameof(NoGraphics)));
        foreach (var method in typeof(FactoryModel).GetMethods().Where(m => m.Name == nameof(FactoryModel.AddWreckage) &&
                     m.GetParameters().Any(p => p.ParameterType == typeof(ImpactData).MakeByRefType())))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(BuildingDeathVisualTest),
                method.GetParameters().Length == 5 ? nameof(RecordPlain) :
                method.GetParameters().Length == 7 ? nameof(RecordTransformed) : nameof(RecordMoving)));
        harmony.Patch(AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.RemoveEntityWithComponents)),
            prefix: new HarmonyMethod(typeof(BuildingDeathVisualTest), nameof(RemoveEntity)));
        harmony.Patch(AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.KillEntityFinally)),
            prefix: new HarmonyMethod(typeof(BuildingDeathVisualTest), nameof(RejectRuleReplay)));
        var game = Uninitialized<GameData>();
        var planet = new PlanetData { id = 101, factoryLoaded = true, factoryModel = Uninitialized<FactoryModel>() };
        factory = Uninitialized<PlanetFactory>(); planet.factory = factory;
        SetBackingField(factory, "planet", planet);
        factory.entityPool = new EntityData[4]; factory.entityCursor = 4;
        factory.entityPool[1] = new EntityData
        {
            id = 1,
            protoId = 2303,
            modelIndex = 1,
            pos = new Vector3(0, 200, 3),
            rot = Quaternion.identity
        };
        factory.entityConnPool = new int[64];
        factory.enemyPool = new EnemyData[4]; factory.enemyCursor = 4;
        factory.enemyPool[1] = new EnemyData { id = 1, modelIndex = 1, pos = new VectorLF3(0, 200, 3), rot = Quaternion.identity };
        game.galaxy = new GalaxyData { stars = [new StarData { id = 1, planets = [planet] }] };
        GameMain.data = game;
        Multiplayer.Session = Uninitialized<MultiplayerSession>();
        Multiplayer.Session.Factories = new FactoryManager();
        wreckageCalls.Clear(); removals = 0;
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchSelf(); GameMain.data = previousGame; Multiplayer.Session = previousSession;
        SetStatic(typeof(PlanetFactory), nameof(PlanetFactory.PrefabDescByModelIndex), previousPrefabs);
    }

    [TestMethod]
    public void PlayerDeathPlaysBeforeRemovalAndADuplicateCannotExplodeAReusedSlot()
    {
        var impact = new ImpactData { mass = 12, point = new Vector3(1, 2, 3), velocity = new Vector3(4, 5, 6) };
        byte[] bytes;
        using (BuildingDeathVisuals.BeginDeath(impact)) bytes = BuildingDeathVisuals.Capture(factory, 1);
        var packet = RoundTrip(new BuildingDeathPacket
        { PlanetId = 101, Sequence = 1, ObjectId = 1, ProtoId = 2303, Connections = new int[16], Prebuild = [], Wreckage = bytes });
        BuildingDeathReplication.Apply(factory, packet);
        TestAssert.AreEqual(1, removals);
        TestAssert.HasCount(1, wreckageCalls);
        TestAssert.AreEqual(impact.velocity, ((ImpactData)wreckageCalls[0][4]).velocity);
        factory.entityPool[1] = new EntityData { id = 1, protoId = 2303, modelIndex = 1 };
        BuildingDeathReplication.Apply(factory, packet);
        TestAssert.HasCount(1, wreckageCalls);
        TestAssert.AreEqual(1, factory.entityPool[1].id);
    }

    [TestMethod]
    [DataRow(-1f, 2)]
    [DataRow(0f, 0)]
    [DataRow(1f, 1)]
    public void ExchangerWreckageUsesTheHostStateEvenAfterTheClientStateChanges(float state, int expected)
    {
        factory.entityPool[1].powerExcId = 1;
        factory.powerSystem = Uninitialized<PowerSystem>();
        factory.powerSystem.excPool = new PowerExchangerComponent[2]; factory.powerSystem.excPool[1].state = state;
        var bytes = BuildingDeathVisuals.Capture(factory, 1);
        factory.powerSystem.excPool[1].state = 0;
        var visual = BuildingDeathVisuals.Decode(bytes);
        BuildingDeathVisuals.Play(factory, 1, visual);
        TestAssert.AreEqual(expected, wreckageCalls.Single()[1]);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TargetedWreckagePreservesTurretAndEjectorAim(bool turret)
    {
        PlanetFactory.PrefabDescByModelIndex[1].wreckagePrefab.transformType = WreckageHandler.ETransformType.HasTarget;
        models.dataArray[0].prefabDesc.isTurret = turret; models.dataArray[0].prefabDesc.isEjector = !turret;
        var aim = new Vector3(1, 2, 3);
        if (turret)
        {
            factory.entityPool[1].turretId = 1;
            factory.defenseSystem = Uninitialized<DefenseSystem>();
            factory.defenseSystem.turrets = new DataPool<TurretComponent>(); factory.defenseSystem.turrets.Reset();
            factory.defenseSystem.turrets.buffer[1].localDir = aim;
        }
        else
        {
            factory.entityPool[1].ejectorId = 1;
            factory.factorySystem = Uninitialized<FactorySystem>();
            factory.factorySystem.ejectorPool = new EjectorComponent[2]; factory.factorySystem.ejectorPool[1].localDir = aim;
        }
        var visual = BuildingDeathVisuals.Decode(BuildingDeathVisuals.Capture(factory, 1));
        BuildingDeathVisuals.Play(factory, 1, visual);
        TestAssert.AreEqual(aim, wreckageCalls.Single()[4]);
        TestAssert.AreEqual(WreckageHandler.ETransformType.HasTarget, wreckageCalls.Single()[6]);
    }

    [TestMethod]
    public void OrbitalSubstationPreservesItsAnimationAndServedEnergy()
    {
        PlanetFactory.PrefabDescByModelIndex[1].wreckagePrefab.transformType = WreckageHandler.ETransformType.OrbitalSubstation;
        factory.entityPool[1].powerNodeId = 1;
        factory.powerSystem = Uninitialized<PowerSystem>();
        factory.powerSystem.nodePool = new PowerNodeComponent[2]; factory.powerSystem.nodePool[1].networkId = 1;
        factory.powerSystem.netPool = [null!, Uninitialized<PowerNetwork>()]; factory.powerSystem.netPool[1].energyServed = 75;
        factory.entityAnimPool = new AnimData[2]; factory.entityAnimPool[1] = new AnimData { time = 4, working_length = .5f };
        var visual = BuildingDeathVisuals.Decode(BuildingDeathVisuals.Capture(factory, 1));
        BuildingDeathVisuals.Play(factory, 1, visual);
        TestAssert.AreEqual(1, wreckageCalls.Single()[1]);
        TestAssert.AreEqual(new Vector3(4, .5f, 75), wreckageCalls.Single()[4]);
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(120, 400)]
    [DataRow(400, 400)]
    public void DarkFogBuildingUsesTheGrowthWreckageOverloadWithoutBuilderComponents(int sp, int max)
    {
        PlanetFactory.PrefabDescByModelIndex[1].wreckagePrefab.transformType = WreckageHandler.ETransformType.DFGBuilding;
        PlanetFactory.PrefabDescByModelIndex[1].wreckageFragments = [[]];
        new GroundEnemyReplicaPools(101).PlayDeathPresentation(1, sp, max);
        TestAssert.HasCount(1, wreckageCalls);
        TestAssert.AreEqual(new Vector3(sp, max, 0), wreckageCalls.Single()[4]);
        TestAssert.AreEqual(WreckageHandler.ETransformType.DFGBuilding, wreckageCalls.Single()[6]);
        TestAssert.AreEqual(0, factory.enemyPool[1].builderId);
        TestAssert.AreEqual(1, factory.enemyPool[1].id);
        TestAssert.AreEqual(0, removals);
    }

    [TestMethod]
    public void UnloadedFactoriesAndPrebuildsDoNotSpawnWreckage()
    {
        var visual = BuildingDeathVisuals.Decode(BuildingDeathVisuals.Capture(factory, 1));
        factory.planet.factoryLoaded = false;
        BuildingDeathVisuals.Play(factory, 1, visual);
        new GroundEnemyReplicaPools(101).PlayDeathPresentation(1, 400, 400);
        TestAssert.IsEmpty(wreckageCalls);
        TestAssert.IsEmpty(BuildingDeathVisuals.Capture(factory, -1));
        TestAssert.AreEqual(visual.ModelIndex, BuildingDeathVisuals.Decode(BuildingDeathVisuals.Capture(factory, 1)).ModelIndex,
            "A headless or remote host must still capture presentation inputs for its clients.");
    }

    [TestMethod]
    public void NestedDeathsRestoreTheImpactAndMalformedVisualsFailBeforeRemoval()
    {
        using (BuildingDeathVisuals.BeginDeath(new ImpactData { mass = 12 }))
        {
            try
            {
                using var nested = BuildingDeathVisuals.BeginDeath(new ImpactData { mass = 34 });
                TestAssert.AreEqual(34f, BuildingDeathVisuals.CurrentImpact.mass);
                throw new InvalidOperationException();
            }
            catch (InvalidOperationException) { }
            TestAssert.AreEqual(12f, BuildingDeathVisuals.CurrentImpact.mass);
        }
        TestAssert.AreEqual(0f, BuildingDeathVisuals.CurrentImpact.mass);
        TestAssert.Throws<InvalidDataException>(() => BuildingDeathReplication.Apply(factory, new BuildingDeathPacket
        { PlanetId = 101, Sequence = 1, ObjectId = 1, ProtoId = 2303, Connections = new int[16], Prebuild = [], Wreckage = [1] }));
        TestAssert.AreEqual(1, factory.entityPool[1].id);
        TestAssert.IsEmpty(wreckageCalls);
        // Verify that the actual host patch captures the by-ref stat and always finalizes its scope.
        var patches = DeclaredPatches(typeof(BuildingDeath_Patch)).ToArray();
        TestAssert.IsTrue(patches.Any(p => p.Target?.Name == nameof(PlanetFactory.KillEntityFinally) && p.Patch.Name == "Begin"));
        TestAssert.IsTrue(patches.Any(p => p.Target?.Name == nameof(PlanetFactory.KillEntityFinally) && p.Patch.Name == "End"));
    }

    private static bool GetModels(ref ModelProtoSet __result) { __result = models; return false; }
    private static bool NoGraphics(ref GPUInstancingManager __result) { __result = null!; return false; }
    private static bool RecordPlain(ModelProto modelProto, int stateIndex, ref Vector3 pos, ref Quaternion rot, ref ImpactData impact)
    { wreckageCalls.Add([modelProto, stateIndex, pos, rot, impact]); return false; }
    private static bool RecordTransformed(ModelProto modelProto, int stateIndex, ref Vector3 pos, ref Quaternion rot,
        Vector3 data, ref ImpactData impact, WreckageHandler.ETransformType transformType)
    { wreckageCalls.Add([modelProto, stateIndex, pos, rot, data, impact, transformType]); return false; }
    private static bool RecordMoving(ModelProto modelProto, int stateIndex, Vector3 pos, ref Quaternion rot, ref Vector3 vel, ref ImpactData impact)
    { wreckageCalls.Add([modelProto, stateIndex, pos, rot, vel, impact]); return false; }
    private static bool RemoveEntity(PlanetFactory __instance, int id, bool returnItems)
    {
        TestAssert.IsFalse(returnItems);
        TestAssert.HasCount(1, wreckageCalls, "Wreckage must be spawned while the original building still exists.");
        removals++; __instance.entityPool[id] = default; return false;
    }
    private static bool RejectRuleReplay() => throw new AssertFailedException("The replica must never run KillEntityFinally.");
}
