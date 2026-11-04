using HarmonyLib;
using NebulaeWorld.GameStates;
using NebulaeWorld;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class SpaceSectorBootstrapTest
{
    private static int subsystemInitializations;
    private static bool CountSubsystemInitialization() { subsystemInitializations++; return false; }
    private static bool SkipEngineLog() => false;

    [TestMethod]
    public void ClientNewGameKeepsNativeSubsystemsAndAstroSlotsWithoutGeneratingHives()
    {
        var previous = Multiplayer.Session;
        var harmony = new Harmony("nebulae.tests.skip-generated-hives");
        try
        {
            var sector = Sector();
            foreach (var star in sector.galaxy.stars) star.initialHiveCount = 3;
            sector.isCombatMode = true;
            sector.creationSystem = Uninitialized<CreationSystem>();
            sector.blackboxSystem = Uninitialized<BlackboxSystem>();
            sector.blackboxSystem.Init();
            Multiplayer.Session = Uninitialized<MultiplayerSession>();
            Multiplayer.Session.Client = Uninitialized<global::NebulaeNetwork.Client>();
            harmony.Patch(AccessTools.Method(typeof(SpaceSector), nameof(SpaceSector.SetForNewGame)),
                transpiler: new HarmonyMethod(typeof(SpaceSector_Patch), nameof(SpaceSector_Patch.SetForNewGame_Transpiler)));
            harmony.CreateClassProcessor(typeof(ExtendedGalaxyHives_Patch)).Patch();
            harmony.Patch(AccessTools.Method(typeof(UnityEngine.Debug), nameof(UnityEngine.Debug.Log), new[] { typeof(object) }),
                prefix: new HarmonyMethod(typeof(SpaceSectorBootstrapTest), nameof(SkipEngineLog)));
            subsystemInitializations = 0;
            foreach (var type in new[] { typeof(SkillSystem), typeof(CreationSystem) })
                harmony.Patch(AccessTools.Method(type, "SetForNewGame"),
                    prefix: new HarmonyMethod(typeof(SpaceSectorBootstrapTest), nameof(CountSubsystemInitialization)));
            sector.SetForNewGame();
            TestAssert.AreEqual(2, subsystemInitializations);
            TestAssert.AreEqual(0, sector.blackboxSystem.blackboxSources.count);
            TestAssert.IsTrue(sector.isCombatMode);
            TestAssert.AreEqual(19, sector.astroCursor);
            TestAssert.AreEqual(1000001, sector.astros[1].id);
            TestAssert.AreEqual(1000018, sector.astros[18].id);
            TestAssert.AreEqual(1, sector.enemyCursor);
            TestAssert.AreEqual(1, sector.craftCursor);
            TestAssert.IsNotNull(sector.combatSpaceSystem);
            TestAssert.IsTrue(sector.dfHives.All(hive => hive == null));
            TestAssert.IsTrue(sector.dfHivesByAstro.All(hive => hive == null));
            TestAssert.AreEqual(3, sector.galaxy.stars[0].initialHiveCount);
            Multiplayer.Session = null;
            TestAssert.AreEqual(3, SpaceSector_Patch.InitialHiveCount(sector.galaxy.stars[0]));
            Multiplayer.Session = Uninitialized<MultiplayerSession>();
            Multiplayer.Session.Server = Uninitialized<global::NebulaeNetwork.Server>();
            TestAssert.AreEqual(3, SpaceSector_Patch.InitialHiveCount(sector.galaxy.stars[0]));
        }
        finally { harmony.UnpatchSelf(); Multiplayer.Session = previous; }
    }

    private static DataPool<T> Pool<T>() where T : struct, IPoolElement
    { var pool = new DataPool<T>(); pool.Reset(); return pool; }

    private static SpaceSector Sector()
    {
        var data = Uninitialized<GameData>();
        data.galaxy = new GalaxyData { starCount = 2, stars = [new StarData { id = 1 }, new StarData { id = 2 }] };
        var player = Uninitialized<Player>();
        AccessTools.Field(typeof(Player), "<mecha>k__BackingField").SetValue(player, Uninitialized<Mecha>());
        AccessTools.Field(typeof(GameData), "<mainPlayer>k__BackingField").SetValue(data, player);
        player.mecha.player = player;
        player.mecha.spaceCombatModule = new CombatModuleComponent { moduleFleets = [new ModuleFleet { fleetId = 1 }] };
        data.patch = 20;
        var sector = new SpaceSector
        {
            gameData = data,
            galaxy = data.galaxy,
            astros = new AstroData[32],
            astroCursor = 18,
            spaceRuins = Pool<SpaceRuinData>(),
            dfHives = new EnemyDFHiveSystem[2],
            dfHivesByAstro = new EnemyDFHiveSystem[18]
        };
        sector.astros[1] = new AstroData
        {
            id = 1000001,
            type = EAstroType.EnemyHive,
            parentId = 100,
            uPos = new VectorLF3(123, 456, 789),
            uRadius = 800
        };
        sector.SetEnemyCapacity(32); sector.SetCraftCapacity(8);
        sector.skillSystem = Uninitialized<SkillSystem>();
        sector.skillSystem.combatStats = Pool<CombatStat>(); sector.skillSystem.removedSkillTargets = new();
        sector.combatSpaceSystem = new CombatSpaceSystem(sector);
        data.spaceSector = sector;
        return sector;
    }

    private static byte[] Export(SpaceSector sector)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        SpaceSectorBootstrap.Export(sector, writer);
        return stream.ToArray();
    }

    [TestMethod]
    public void NativeImportRestoresAstrosRuinsAndFleetLinksWithoutAnyEnemyOrHiveComponents()
    {
        var sector = Sector();
        sector.enemyPool[1] = new EnemyData { id = 1, originAstroId = 1000001 }; sector.enemyCursor = 2;
        sector.dfHives[0] = new EnemyDFHiveSystem { rootEnemyId = 1 };
        for (var id = 1; id <= 80; id++)
        {
            ref var stat = ref sector.skillSystem.combatStats.Add();
            stat.objectType = (int)EObjectType.Enemy; stat.objectId = id;
        }
        ref var craftStat = ref sector.skillSystem.combatStats.Add();
        craftStat.objectType = (int)EObjectType.Craft; craftStat.objectId = 1; craftStat.astroId = 100;
        craftStat.hp = 321; craftStat.hpMax = 500; craftStat.lastCaster = new SkillTarget { type = ETargetType.Enemy, id = 1 };
        sector.craftPool[1] = new CraftData
        {
            id = 1,
            astroId = 100,
            protoId = 5100,
            fleetId = 1,
            isSpace = true,
            owner = -1,
            combatStatId = craftStat.id
        };
        sector.craftPool[2] = new CraftData { id = 2, astroId = 100, protoId = 5101, unitId = 1, isSpace = true, owner = 1 };
        sector.craftCursor = 3;
        ref var ruin = ref sector.spaceRuins.Add(); ruin.astroId = 100; ruin.lifeTime = 123;
        ref var fleet = ref sector.combatSpaceSystem.fleets.Add(); fleet.craftId = 1; fleet.owner = -1; fleet.dispatch = true;
        fleet.target = new SkillTarget { type = ETargetType.Enemy, id = 1, astroId = 1000001 };
        fleet.hatred.max = new HatredTarget { target = 1, value = 100 };
        ref var unit = ref sector.combatSpaceSystem.units.Add(); unit.craftId = 2; unit.fire0 = 12;
        sector.skillSystem.removedSkillTargets.Add(fleet.target);
        sector.skillSystem.removedSkillTargets.Add(new SkillTarget { type = ETargetType.Craft, id = 2, astroId = 100 });
        var bytes = Export(sector);
        var destination = Sector();
        var harmony = new Harmony("nebulae.tests.sector-bootstrap");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(SkillSystem), nameof(SkillSystem.Import)),
                prefix: new HarmonyMethod(typeof(SkillSystem_Patch), nameof(SkillSystem_Patch.Import_Prefix)));
            using var stream = new MemoryStream(bytes); using var reader = new BinaryReader(stream);
            SpaceSectorBootstrap.Import(destination, reader);
            TestAssert.AreEqual(stream.Length, stream.Position);
        }
        finally { harmony.UnpatchSelf(); }
        TestAssert.IsFalse(SpaceSectorBootstrap.IsImporting);
        TestAssert.AreEqual(0, destination.enemyCount);
        TestAssert.AreEqual(1, destination.enemyCursor);
        TestAssert.IsTrue(destination.dfHives.All(hive => hive == null));
        TestAssert.AreEqual(sector.astros[1].uPos, destination.astros[1].uPos);
        TestAssert.AreEqual(sector.spaceRuins.cursor, destination.spaceRuins.cursor);
        TestAssert.AreEqual(1, destination.craftPool[1].fleetId);
        TestAssert.AreEqual(1, destination.craftPool[1].combatStatId);
        TestAssert.AreEqual(1, destination.skillSystem.combatStats.count);
        TestAssert.AreEqual(321, destination.skillSystem.combatStats.buffer[1].hp);
        TestAssert.AreEqual(0, destination.skillSystem.combatStats.buffer[1].lastCaster.id);
        TestAssert.AreEqual(1, destination.combatSpaceSystem.fleets.buffer[1].craftId);
        TestAssert.IsTrue(destination.combatSpaceSystem.fleets.buffer[1].dispatch);
        TestAssert.AreEqual(0, destination.combatSpaceSystem.fleets.buffer[1].target.id);
        TestAssert.AreEqual(0, destination.combatSpaceSystem.fleets.buffer[1].hatred.max.target);
        TestAssert.AreEqual(12, destination.combatSpaceSystem.units.buffer[1].fire0);
        TestAssert.IsTrue(destination.skillSystem.removedSkillTargets.All(target => target.type != ETargetType.Enemy));
        // Projection must not rewrite even temporary ids or targets in the running host.
        TestAssert.AreEqual(81, sector.craftPool[1].combatStatId);
        TestAssert.AreEqual(1, sector.combatSpaceSystem.fleets.buffer[1].target.id);
        TestAssert.AreEqual(1, sector.combatSpaceSystem.fleets.buffer[1].hatred.max.target);
        TestAssert.AreEqual(1, sector.enemyCount);
        TestAssert.AreEqual(1, sector.dfHives[0].rootEnemyId);
        var withoutMoreEnemies = Export(sector);
        sector.SetEnemyCapacity(4096); sector.enemyCursor = 4000;
        for (var id = 0; id < 500; id++)
        { ref var stat = ref sector.skillSystem.combatStats.Add(); stat.objectType = (int)EObjectType.Enemy; }
        CollectionAssert.AreEqual(withoutMoreEnemies, Export(sector), "Remote enemy count cannot inflate the bootstrap.");
    }

    [TestMethod]
    public void ATruncatedBootstrapCannotLeaveNormalSaveImportsInBootstrapMode()
    {
        var sector = Sector();
        using var reader = new BinaryReader(new MemoryStream(new byte[4]));
        TestAssert.Throws<EndOfStreamException>(() => SpaceSectorBootstrap.Import(sector, reader));
        TestAssert.IsFalse(SpaceSectorBootstrap.IsImporting);
    }

    [TestMethod]
    public void GeneratedEnemiesAndHiveIndicesAreClearedBeforeImport()
    {
        var sector = Sector();
        var hive = new EnemyDFHiveSystem { forms = Array.Empty<EnemyFormation>() };
        foreach (var name in new[] { "builders", "cores", "nodes", "connectors", "replicators", "gammas", "turrets", "relays", "tinders", "units" })
        {
            var field = AccessTools.Field(typeof(EnemyDFHiveSystem), name);
            var pool = Activator.CreateInstance(field.FieldType)!;
            AccessTools.Method(field.FieldType, "Reset").Invoke(pool, null);
            field.SetValue(hive, pool);
        }
        sector.dfHives[0] = hive; sector.dfHivesByAstro[1] = hive;
        sector.enemyPool[1] = new EnemyData { id = 1 }; sector.enemyCursor = 2;
        SpaceSectorBootstrap.ClearGeneratedEnemies(sector);
        TestAssert.AreEqual(0, sector.enemyCount);
        TestAssert.IsTrue(sector.enemyPool.All(enemy => enemy.id == 0));
        TestAssert.IsTrue(sector.dfHives.All(item => item == null));
        TestAssert.IsTrue(sector.dfHivesByAstro.All(item => item == null));
        TestAssert.HasCount(0, sector.skillSystem.removedSkillTargets);
    }
}
