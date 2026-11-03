using System.Reflection;
using HarmonyLib;
using Mono.Cecil;
using NebulaeWorld;
using UnityEngine;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class SimulationOptimizationTest
{
    [TestMethod]
    public void OrbitScopeRestoresItsOwnerAfterNestedNativeExceptions()
    {
        var harmony = new Harmony("nebulae.tests.orbit-scope");
        var previousDedicated = Multiplayer.IsDedicated;
        var before = HiveOrbitCache_Patch.Enter(new SpaceSector { galaxy = new GalaxyData { starCount = 8191 } });
        var current = AccessTools.Field(typeof(HiveOrbitCache_Patch), "current");
        var outer = current.GetValue(null);
        try
        {
            Multiplayer.IsDedicated = true;
            harmony.CreateClassProcessor(typeof(HiveOrbitScope_Patch)).Patch();
            var inner = new SpaceSector { galaxy = new GalaxyData { starCount = 8191 } };
            Check.Throws<NullReferenceException>(() => inner.GameTick(1));
            Check.AreSame(outer, current.GetValue(null));
        }
        finally { harmony.UnpatchSelf(); HiveOrbitCache_Patch.Exit(before); Multiplayer.IsDedicated = previousDedicated; }
    }

    private static int realizations;
    private static bool CountRealization() { realizations++; return false; }

    [TestMethod]
    public void ReorderedRelayGuardInvokesNativeRealizationAtExactlyTheSameTicks()
    {
        var harmony = new Harmony("nebulae.tests.relay-guard");
        var oldEnabled = HiveRelayGuard_Patch.Enabled;
        var previousDedicated = Multiplayer.IsDedicated;
        try
        {
            Multiplayer.IsDedicated = true;
            harmony.CreateClassProcessor(typeof(HiveRelayGuard_Patch)).Patch();
            harmony.Patch(AccessTools.Method(typeof(DFRelayComponent), "RealizePlanetBase"),
                prefix: new HarmonyMethod(typeof(SimulationOptimizationTest), nameof(CountRealization)));
            var hive = new EnemyDFHiveSystem
            {
                gameData = Uninitialized<GameData>(),
                history = Uninitialized<GameHistoryData>(),
                starData = new StarData { id = 1 },
                sector = new SpaceSector()
            };
            hive.sector.skillSystem = Uninitialized<SkillSystem>();
            hive.sector.model = Uninitialized<SectorModel>();
            hive.sector.model.gpuiManager = Uninitialized<SSGPUInstancingManager>();
            hive.sector.model.gpuiManager.objectRenderers = new SpaceObjectRenderer[292];
            hive.sector.model.gpuiManager.objectRenderers[288] = Uninitialized<SpaceObjectRenderer>();
            hive.sector.model.gpuiManager.objectRenderers[288].instPool = new SPACEOBJECT[1];
            hive.relays = new ObjectPool<DFRelayComponent>(); hive.relays.Reset(); var relay = hive.relays.Add();
            hive.tinders = new DataPool<DFTinderComponent>(); hive.tinders.Reset();
            hive.units = new DataPool<EnemyUnitComponent>(); hive.units.Reset();
            hive.turrets = new DataPool<DFSTurretComponent>(); hive.turrets.Reset();
            var enemies = new EnemyData[1]; var anims = new AnimData[1]; var astros = new AstroData[1];
            foreach (var target in new[] { 0, 101 })
                foreach (var baseState in new[] { 0, 1, 2 })
                    foreach (var stage in new[] { -2, 0, 2 })
                        for (var tick = 0; tick < 15; tick++)
                        {
                            relay.targetAstroId = target; relay.baseState = baseState; relay.stage = stage;
                            realizations = 0; HiveRelayGuard_Patch.Enabled = false;
                            hive.GameTickLogic(tick, astros, astros, enemies, anims); var expected = realizations;
                            realizations = 0; HiveRelayGuard_Patch.Enabled = true;
                            hive.GameTickLogic(tick, astros, astros, enemies, anims);
                            Check.AreEqual(expected, realizations, $"{target}/{baseState}/{stage}/{tick}");
                            Check.AreEqual(target == 0 && baseState == 1 && stage == 2 && tick % 5 == relay.id % 5 ? 1 : 0, realizations);
                        }
        }
        finally { HiveRelayGuard_Patch.Enabled = oldEnabled; harmony.UnpatchSelf(); Multiplayer.IsDedicated = previousDedicated; }
    }

    [TestMethod]
    public void OrbitReuseRequiresConsecutiveTimeAndEveryNativeInputBit()
    {
        var orbit = new AstroOrbitData
        {
            orbitRadius = 2,
            orbitalPeriod = 300,
            orbitPhase = 17,
            orbitRotation = new Quaternion(.1f, .2f, .3f, .9f),
            orbitNormal = new VectorLF3(0, 1, 0)
        };
        var center = new VectorLF3(1e12, -1e12, 7);
        var astro = new AstroData
        {
            id = 1000001,
            uPosNext = new VectorLF3(1, 2, 3),
            uRotNext = new Quaternion(.1f, .3f, .2f, .8f)
        };
        var cache = new HiveOrbitCache_Patch.State();
        Check.IsFalse(cache.Matches(orbit, 11, center));
        cache.Capture(orbit, 10, center, astro);
        Check.IsTrue(cache.Matches(orbit, 11, center));
        foreach (var tick in new long[] { 0, 9, 10, 12, long.MaxValue }) Check.IsFalse(cache.Matches(orbit, tick, center));
        foreach (var name in new[] { "orbitRadius", "orbitalPeriod", "orbitPhase", "orbitRotation", "orbitNormal" })
        {
            var field = typeof(AstroOrbitData).GetField(name)!;
            var previous = field.GetValue(orbit);
            object changed = previous switch
            {
                float f => f + .25f,
                double d => d + .25,
                Quaternion q => new Quaternion(q.x + .1f, q.y, q.z, q.w),
                VectorLF3 v => new VectorLF3(v.x + .1, v.y, v.z),
                _ => throw new InvalidOperationException(name)
            };
            field.SetValue(orbit, changed);
            Check.IsFalse(cache.Matches(orbit, 11, center), name);
            field.SetValue(orbit, previous);
            Check.IsTrue(cache.Matches(orbit, 11, center), name);
        }
        Check.IsFalse(cache.Matches(orbit, 11, new VectorLF3(center.x, center.y, 8)));
        cache.Reuse(ref astro);
        Check.AreEqual(1000001, astro.id);
        Check.AreEqual(new VectorLF3(1, 2, 3), astro.uPos);
        Check.AreEqual(astro.uRotNext, astro.uRot);
        cache.Capture(orbit, long.MaxValue, center, astro);
        Check.IsFalse(cache.Matches(orbit, long.MinValue, center));
        orbit.orbitPhase = 0f; cache.Capture(orbit, 10, center, astro);
        orbit.orbitPhase = BitConverter.ToSingle(new byte[] { 0, 0, 0, 128 }, 0);
        Check.IsFalse(cache.Matches(orbit, 11, center), "Signed zero is an input change too.");
    }

    [TestMethod]
    public void DedicatedCursorIsClearedWithoutEnteringCameraCodeAndClientsRetainNativePath()
    {
        var previous = Multiplayer.IsDedicated;
        try
        {
            var logic = new SpaceColliderLogic
            {
                cursorCastAllCount = 2,
                castEnemyhiveIndex = 8,
                castEnemyFormId = 3,
                castEnemyPortId = 4,
                castPlanetAstroId = 101
            };
            Multiplayer.IsDedicated = true;
            Check.IsFalse(HeadlessLocalInteraction_Patch.Prefix(logic));
            Check.AreEqual(0, logic.cursorCastAllCount); Check.AreEqual(-1, logic.castEnemyhiveIndex);
            Check.AreEqual(0, logic.castEnemyFormId); Check.AreEqual(0, logic.castEnemyPortId);
            Check.AreEqual(0, logic.castPlanetAstroId);
            Check.IsFalse(HeadlessSectorPresentation_Patch.Prefix());
            Multiplayer.IsDedicated = false;
            Check.IsTrue(HeadlessLocalInteraction_Patch.Prefix(logic));
            Check.IsTrue(HeadlessSectorPresentation_Patch.Prefix());
        }
        finally { Multiplayer.IsDedicated = previous; }
    }

    [TestMethod]
    public void SailingRetainsRelativePosesAndMissingPlayersUseConservativeFallback()
    {
        var previous = Multiplayer.IsDedicated;
        try
        {
            Multiplayer.IsDedicated = true;
            var model = Uninitialized<SectorModel>(); model.gameData = Uninitialized<GameData>();
            Check.IsFalse(HeadlessLocalInteraction_Patch.SkipRelativePoses(model));
            var player = Uninitialized<Player>();
            AccessTools.Property(typeof(GameData), "mainPlayer").SetValue(model.gameData, player);
            Check.IsTrue(HeadlessLocalInteraction_Patch.SkipRelativePoses(model));
            player.movementState = EMovementState.Sail;
            Check.IsFalse(HeadlessLocalInteraction_Patch.SkipRelativePoses(model));
            Multiplayer.IsDedicated = false;
            player.movementState = EMovementState.Walk;
            Check.IsFalse(HeadlessLocalInteraction_Patch.SkipRelativePoses(model));
        }
        finally { Multiplayer.IsDedicated = previous; }
    }

    [TestMethod]
    public void RelativePoseReadersRemainConfinedToLocalInteractionAndPresentation()
    {
        var readers = new List<string>();
        using var assembly = AssemblyDefinition.ReadAssembly(typeof(SectorModel).Assembly.Location);
        foreach (var type in assembly.MainModule.Types)
            foreach (var method in type.Methods)
            {
                if (!method.HasBody || !method.Body.Instructions.Any(i => i.Operand is FieldReference f &&
                        f.DeclaringType.FullName == "SectorModel" && (f.Name == "astroArr" || f.Name == "galaxyAstroArr"))) continue;
                if (type.FullName == "SectorModel" || type.FullName == "EnemyDFHiveRenderer" || type.FullName == "SpaceColliderLogic") continue;
                readers.Add(type.FullName + "." + method.Name);
            }
        Check.IsEmpty(readers, "A new simulation consumer needs an explicit relative-pose policy: " + string.Join(", ", readers));
        var nativeCursor = AccessTools.Method(typeof(SpaceColliderLogic), "GameTick");
        var calls = PatchProcessor.GetOriginalInstructions(nativeCursor).Where(i => i.operand is MethodInfo).Select(i => ((MethodInfo)i.operand).Name).ToArray();
        CollectionAssert.AreEqual(new[] { "RaycastAllByCursorRay" }, calls);
    }
}
