using HarmonyLib;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;
using NebulaeWorld.Combat;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class HiveThreatAndAlertTest
{
    private static (GameData Game, EnemyDFHiveSystem Hive) Fixture()
    {
        var star = new StarData { id = 1, index = 0, uPosition = new VectorLF3(100000000, 0, 0) };
        var game = Uninitialized<GameData>();
        game.history = new GameHistoryData();
        game.history.combatSettings.aggressiveness = CombatSettings.AGG_VALUE_3_NORMAL;
        AccessTools.PropertySetter(typeof(GameData), nameof(GameData.mainPlayer)).Invoke(game, [Uninitialized<Player>()]);
        game.mainPlayer.isAlive = true;
        AccessTools.PropertySetter(typeof(GameData), nameof(GameData.localStar)).Invoke(game, [star]);
        var sector = Uninitialized<SpaceSector>();
        game.spaceSector = sector; sector.gameData = game;
        sector.astros = new AstroData[2]; sector.astros[1].uPos = star.uPosition + new VectorLF3(40000, 0, 0);
        sector.model = Uninitialized<SectorModel>(); sector.model.sector = sector;
        AccessTools.Field(typeof(SectorModel), "m_CachedPtr").SetValue(sector.model, new IntPtr(1));
        sector.skillSystem = Uninitialized<SkillSystem>(); sector.skillSystem.maxHatredSpaceTmp = 1000000;
        var hive = new EnemyDFHiveSystem
        {
            gameData = game,
            history = game.history,
            sector = sector,
            starData = star,
            realized = true,
            rootEnemyId = 1,
            hiveAstroId = 1000001,
            orbitRadius = 40000,
            pbuilders = new GrowthPattern_DFSpace.Builder[2],
            ticks = 1
        };
        hive.evolve.rankBase = 127; hive.evolve.maxThreat = 1000000;
        hive.builders = new DataPool<EnemyBuilderComponent>(); hive.builders.Reset();
        var builder = hive.builders.Add().id;
        hive.builders.buffer[builder].state = 3;
        hive.pbuilders[1].instId = 1; hive.pbuilders[1].instBuilderId = builder;
        hive.units = new DataPool<EnemyUnitComponent>(); hive.units.Reset();
        hive.gammas = new DataPool<DFSGammaComponent>(); hive.gammas.Reset();
        hive.InitFormations(); sector.dfHives = [hive];
        game.mainPlayer.uPosition = sector.astros[1].uPos;
        var sphere = Uninitialized<DysonSphere>();
        sphere.swarm = Uninitialized<DysonSwarm>(); sphere.swarm.sailCursor = 50000;
        sphere.energyGenPerSail = 600; sphere.layersSorted = new DysonSphereLayer[10];
        sphere.gameData = game; sphere.starData = star;
        sphere.swarm.energyGenCurrentTick = sphere.swarm.sailCount * sphere.energyGenPerSail;
        sphere.energyGenOriginalCurrentTick = sphere.swarm.energyGenCurrentTick;
        game.dysonSpheres = [sphere];
        return (game, hive);
    }

    [TestMethod]
    public void ClientAlertFollowsLocalApproachDepartureAndTruceWithoutMutatingHatred()
    {
        var previous = Multiplayer.Session;
        try
        {
            Multiplayer.Session = Uninitialized<MultiplayerSession>();
            Multiplayer.Session.Client = Uninitialized<global::NebulaeNetwork.Client>();
            var (game, hive) = Fixture();
            Check.IsFalse(EnemyDFHiveSystem_Patch.DecisionAI_Prefix());
            DarkFogActivity_Patch.UpdateAlert(game.spaceSector.model);
            Check.AreEqual(hive.hiveAstroId, game.spaceSector.model.aggressiveHiveAstroId);
            Check.AreEqual(0, hive.hatred.max.value);
            Check.AreEqual(0, hive.evolve.threat);
            game.mainPlayer.uPosition += new VectorLF3(10000, 0, 0);
            DarkFogActivity_Patch.UpdateAlert(game.spaceSector.model);
            Check.AreEqual(0, game.spaceSector.model.aggressiveHiveAstroId);
            game.mainPlayer.uPosition -= new VectorLF3(10000, 0, 0);
            game.history.dfTruceTimer = 60;
            DarkFogActivity_Patch.UpdateAlert(game.spaceSector.model);
            Check.AreEqual(0, game.spaceSector.model.aggressiveHiveAstroId);
        }
        finally { Multiplayer.Session = previous; }
    }

    [TestMethod]
    public void DetectionRejectsOtherSideOfOrbitAndInvalidPositions()
    {
        var (_, hive) = Fixture();
        Check.IsTrue(HiveSensor.Detect(hive, hive.sector.astros[1].uPos, EAggressiveLevel.Normal, true, out _));
        Check.IsFalse(HiveSensor.Detect(hive, hive.starData.uPosition - new VectorLF3(40000, 0, 0), EAggressiveLevel.Normal, true, out _));
        Check.IsFalse(HiveSensor.Detect(hive, hive.starData.uPosition, EAggressiveLevel.Normal, true, out _));
        Check.IsFalse(HiveSensor.Detect(hive, new VectorLF3(double.NaN, 0, 0), EAggressiveLevel.Normal, true, out _));
        var edge = hive.starData.uPosition + new VectorLF3(Math.Cos(.36) * 40000, Math.Sin(.36) * 40000, 0);
        Check.IsFalse(HiveSensor.Detect(hive, edge, EAggressiveLevel.Normal, true, out _));
        Check.IsTrue(HiveSensor.Detect(hive, edge, EAggressiveLevel.Normal, true, out _, alerted: true));
    }

    [TestMethod]
    public void HostHiveIgnoresLandedPlayersAndClientsNeverAccumulateHiveHatred()
    {
        var previous = Multiplayer.Session;
        try
        {
            var (_, hive) = Fixture();
            var session = Multiplayer.Session = Uninitialized<MultiplayerSession>();
            session.Server = Uninitialized<global::NebulaeNetwork.Server>();
            session.Combat = new CombatManager();
            session.Combat.Players = [new CombatManager.PlayerPosition { id = 7, starId = 1, planetId = 101,
                isAlive = true, uPosition = hive.sector.astros[1].uPos }];
            hive.sector.skillSystem.enemyAggressiveHatredCoefTmp = 1;
            hive.sector.skillSystem.maxHatredSpaceHiveTmp = 10000000;
            EnemyDFHiveSystem_Patch.SensorLogic_Prefix(hive, EAggressiveLevel.Normal);
            Check.AreEqual(0, hive.hatred.max.value);
            session.Combat.Players[0].planetId = -1;
            EnemyDFHiveSystem_Patch.SensorLogic_Prefix(hive, EAggressiveLevel.Normal);
            Check.AreEqual(7, hive.hatred.max.objectId);
            Check.IsGreaterThan(0, hive.hatred.max.value);
            hive.hatred = default;
            session.Server = null; session.Client = Uninitialized<global::NebulaeNetwork.Client>();
            EnemyDFHiveSystem_Patch.SensorLogic_Prefix(hive, EAggressiveLevel.Normal);
            Check.AreEqual(0, hive.hatred.max.value);
        }
        finally { Multiplayer.Session = previous; }
    }

    [TestMethod]
    public void NativeThreatGrowsFromFiftyThousandSailsWithFiveStandbyLancers()
    {
        WithNativeDecision((game, hive) =>
        {
            for (var i = 0; i < 5; i++) hive.forms[0].AddUnit();
            var initial = hive.evolve.threat = 10000;
            for (var i = 0; i < 60; i++) hive.DecisionAI(i);
            Check.IsGreaterThan(initial, hive.evolve.threat);
            Check.AreEqual(30000000L, game.dysonSpheres[0].energyGenCurrentTick_Swarm);
        });
    }

    [TestMethod]
    public void NativeThreatDecaysWhenLancersAreUnavailableOrCoreIsUnfinished()
    {
        WithNativeDecision((_, hive) =>
        {
            hive.evolve.threat = 10000;
            hive.DecisionAI(1);
            Check.AreEqual(9950, hive.evolve.threat);
            for (var i = 0; i < 5; i++) hive.forms[0].AddUnit();
            hive.builders.buffer[hive.pbuilders[1].instBuilderId].state = 2;
            hive.evolve.threat = 10000;
            hive.DecisionAI(2);
            Check.IsLessThan(8500, hive.evolve.threat);
        });
    }

    private static void WithNativeDecision(Action<GameData, EnemyDFHiveSystem> action)
    {
        var previous = Multiplayer.Session;
        var harmony = new Harmony("nebulae.tests.hive-native-threat");
        try
        {
            Multiplayer.Session = null;
            foreach (var name in new[] { "SensorLogic", "UpdateHatred", "HatredAstroSanityCheck", "AssaultingWavesDetermineAI" })
                harmony.Patch(AccessTools.Method(typeof(EnemyDFHiveSystem), name), prefix: new HarmonyMethod(typeof(HiveThreatAndAlertTest), nameof(Skip)));
            var (game, hive) = Fixture(); action(game, hive);
        }
        finally { harmony.UnpatchSelf(); Multiplayer.Session = previous; }
    }
    private static bool Skip() => false;
}
