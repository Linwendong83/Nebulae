using HarmonyLib;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;
using NebulaeWorld.Combat;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class EnemyTargetResolutionPatchTest
{
    private const int PlayerType = (int)ETargetType.Player << 26;
    private Harmony harmony = null!;
    private MultiplayerSession previousSession = null!;
    private GameData previousData = null!;
    private MultiplayerSession session = null!;
    private PlanetFactory factory = null!;
    private SpaceSector sector = null!;

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session;
        previousData = GameMain.data;
        harmony = new Harmony("nebulae.tests.enemy-target-resolution");
        harmony.CreateClassProcessor(typeof(EnemyGroundTargetResolution_Patch)).Patch();
        harmony.CreateClassProcessor(typeof(EnemySpaceTargetResolution_Patch)).Patch();
        session = Uninitialized<MultiplayerSession>();
        session.Combat = Uninitialized<CombatManager>();
        session.Combat.Players =
        [
            new CombatManager.PlayerPosition
            {
                id = 7, isAlive = true, planetId = 101, starId = 1,
                position = new Vector3(2, 200, 0), uPosition = new VectorLF3(2000, 0, 0),
                mecha = new Mecha { energyShieldRadius = 5f }
            },
            new CombatManager.PlayerPosition { id = 9, isAlive = false, planetId = 101 }
        ];
        Multiplayer.Session = session;
        factory = Uninitialized<PlanetFactory>();
        AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, new PlanetData { id = 101 });
        factory.enemySystem = Uninitialized<EnemyDFGroundSystem>();
        factory.enemySystem.local_player_exist_alive = true;
        factory.skillSystem = Uninitialized<SkillSystem>();
        factory.skillSystem.playerSkillTargetL = new Vector3(99, 200, 0);
        factory.skillSystem.playerEnergyShieldRadius = 12f;
        factory.entityPool = new EntityData[4];
        factory.entityPool[3] = new EntityData { id = 3, pos = new Vector3(10, 20, 30) };
        sector = Uninitialized<SpaceSector>();
        sector.craftPool = new CraftData[4];
        sector.craftPool[3] = new CraftData { id = 3, astroId = 0, pos = new VectorLF3(3000, 4000, 5000) };
        var data = Uninitialized<GameData>();
        data.spaceSector = sector;
        var localPlayer = new Player { isAlive = true, uPosition = new VectorLF3(99000, 0, 0) };
        AccessTools.Field(typeof(Player), "<mecha>k__BackingField").SetValue(localPlayer, new Mecha { energyShieldRadius = 17f });
        AccessTools.Field(typeof(GameData), "<mainPlayer>k__BackingField").SetValue(data, localPlayer);
        GameMain.data = data;
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony?.UnpatchSelf();
        Multiplayer.Session = previousSession;
        GameMain.data = previousData;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PatchedMethodsReturnSuccessForTheNamedPlayerOnBothPeers(bool client)
    {
        session.Client = client ? Uninitialized<global::NebulaeNetwork.Client>() : null;
        var unit = new EnemyUnitComponent();
        TestAssert.IsTrue(unit.GetTargetPosition_Ground(factory, PlayerType | 7, out var ground, out var groundRadius));
        TestAssert.AreEqual(new Vector3(2, 200, 0), ground);
        TestAssert.AreEqual(5f, groundRadius);
        TestAssert.IsTrue(EnemyUnitComponent.GetTargetPosition_Space(sector, PlayerType | 7, out var space, out var spaceRadius));
        TestAssert.AreEqual(new VectorLF3(2000, 0, 0), space);
        TestAssert.AreEqual(5f, spaceRadius);
    }

    [TestMethod]
    public void UnresolvablePlayersAndUnavailableTablesNeverFallBackToTheLocalPlayer()
    {
        foreach (var id in new[] { 0, 1, 9, 55 }) AssertMissingPlayer(id);

        session.Combat.Players[0].id = 1;
        session.Combat.Players[0].isAlive = false;
        AssertMissingPlayer(1);

        var players = session.Combat.Players;
        session.Combat.Players = [];
        AssertMissingPlayer(1);
        session.Combat.Players = null!;
        AssertMissingPlayer(1);
        session.Combat.Players = players;
    }

    [TestMethod]
    public void GroundRejectsAnotherPlanetWhileSpaceKeepsTheUniversalTarget()
    {
        session.Combat.Players[0].planetId = 202;
        var unit = new EnemyUnitComponent();
        TestAssert.IsFalse(unit.GetTargetPosition_Ground(factory, PlayerType | 7, out var ground, out var radius));
        TestAssert.AreEqual(Vector3.zero, ground);
        TestAssert.AreEqual(0f, radius);
        TestAssert.IsTrue(EnemyUnitComponent.GetTargetPosition_Space(sector, PlayerType | 7, out var space, out _));
        TestAssert.AreEqual(new VectorLF3(2000, 0, 0), space);
    }

    [TestMethod]
    public void SinglePlayerStillUsesTheVanillaPlayer()
    {
        Multiplayer.Session = null;
        var unit = new EnemyUnitComponent();
        TestAssert.IsTrue(unit.GetTargetPosition_Ground(factory, PlayerType | 1, out var ground, out var groundRadius));
        TestAssert.AreEqual(new Vector3(99, 200, 0), ground);
        TestAssert.AreEqual(12f, groundRadius);
        TestAssert.IsTrue(EnemyUnitComponent.GetTargetPosition_Space(sector, PlayerType | 1, out var space, out var spaceRadius));
        TestAssert.AreEqual(new VectorLF3(99000, 0, 0), space);
        TestAssert.AreEqual(17f, spaceRadius);
    }

    [TestMethod]
    public void NonPlayerTargetsStillUseVanillaResolution()
    {
        var unit = new EnemyUnitComponent();
        TestAssert.IsTrue(unit.GetTargetPosition_Ground(factory, ((int)ETargetType.Entity << 26) | 3, out var ground, out _));
        TestAssert.AreEqual(new Vector3(10, 20, 30), ground);
        TestAssert.IsTrue(EnemyUnitComponent.GetTargetPosition_Space(sector, ((int)ETargetType.Craft << 26) | 3, out var space, out _));
        TestAssert.AreEqual(new VectorLF3(3000, 4000, 5000), space);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void InstallingEveryTargetPatchKeepsResolutionIndependentOfPatchOrder(bool reverseOrder, bool spaceTarget)
    {
        harmony.UnpatchSelf();
        session.Combat.IndexByPlayerId = new Dictionary<int, int> { [7] = 0, [9] = 1 };
        session.Combat.Players[0].skillTargetL = new Vector3(80, 210, 0);
        var names = new[] { nameof(EnemyUnitComponent.GetTargetPosition_Ground), nameof(EnemyUnitComponent.GetTargetPosition_Space) };
        var patches = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetTypes().Where(type =>
        {
            var attributes = type.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>().ToArray();
            return attributes.Any(a => a.info.declaringType == typeof(EnemyUnitComponent)) &&
                (attributes.Any(a => names.Contains(a.info.methodName)) || AccessTools.GetDeclaredMethods(type).Any(method =>
                    method.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>().Any(a => names.Contains(a.info.methodName))));
        }).ToArray();
        TestAssert.IsGreaterThanOrEqualTo(2, patches.Length);
        foreach (var patch in reverseOrder ? patches.Reverse() : patches) harmony.CreateClassProcessor(patch).Patch();

        if (spaceTarget)
        {
            TestAssert.IsTrue(EnemyUnitComponent.GetTargetPosition_Space(sector, PlayerType | 7, out var space, out var radius));
            TestAssert.AreEqual(session.Combat.Players[0].uPosition.x, space.x);
            TestAssert.AreEqual(5f, radius, "Use the target player's radius, independent of patch order.");
        }
        else
        {
            var unit = new EnemyUnitComponent();
            TestAssert.IsTrue(unit.GetTargetPosition_Ground(factory, PlayerType | 7, out var ground, out var radius));
            TestAssert.AreEqual(session.Combat.Players[0].position.x, ground.x, "Ground target x must not depend on patch order.");
            TestAssert.AreEqual(session.Combat.Players[0].position.y, ground.y);
            TestAssert.AreEqual(session.Combat.Players[0].position.z, ground.z);
            TestAssert.AreEqual(5f, radius);
        }
    }

    private void AssertMissingPlayer(int id)
    {
        var unit = new EnemyUnitComponent();
        TestAssert.IsFalse(unit.GetTargetPosition_Ground(factory, PlayerType | id, out var ground, out var groundRadius));
        TestAssert.AreEqual(Vector3.zero, ground);
        TestAssert.AreEqual(0f, groundRadius);
        TestAssert.IsFalse(EnemyUnitComponent.GetTargetPosition_Space(sector, PlayerType | id, out var space, out var spaceRadius));
        TestAssert.AreEqual(default(VectorLF3), space);
        TestAssert.AreEqual(0f, spaceRadius);
    }
}
