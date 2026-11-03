using System.Reflection.Emit;
using HarmonyLib;
using global::NebulaeModel.DataStructures;
using NebulaeWorld;
using NebulaeWorld.Combat;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

/// <summary>
///     A seat that comes back dead must come back exactly where it died. The save a client loads
///     belongs to the seat that wrote it, so its relative frame and its wreckage both have to be
///     re-anchored on the returning seat; otherwise the first tick drags the player to the save
///     writer's position, which is the middle of a star when that seat was sitting at one.
/// </summary>
[TestClass, DoNotParallelize]
public class DeadSeatRejoinTest
{
    private const string HarmonyId = "nebulae.tests.dead-seat-rejoin";

    // The seat comes back where it died: in space, next to the star it died at.
    private static readonly VectorLF3 DeathPosition = new VectorLF3(1200, -800, 400);
    private static readonly VectorLF3 DeathVelocity = new VectorLF3(5, 0, -5);
    // Where the seat that wrote the save was.
    private static readonly VectorLF3 SaveWriterPosition = new VectorLF3(-40000, 12000, 9000);

    private GameData? previousData;
    private List<ArmorWreckage>? previousWreckages;
    private MultiplayerSession? previousSession;
    private Harmony harmony = null!; // Assigned by TestInitialize.

    private Player player = null!; // Assigned by TestInitialize.
    private MechaArmorModel armor = null!; // Assigned by TestInitialize.

    [TestInitialize]
    public void SetUp()
    {
        previousData = GameMain.data;
        previousWreckages = MechaArmorModel.all_wreckages;
        previousSession = Multiplayer.Session;

        var game = Uninitialized<GameData>();
        player = Uninitialized<Player>();
        armor = Uninitialized<MechaArmorModel>();
        var controller = Uninitialized<PlayerController>();
        SetBackingField(player, "mecha", Uninitialized<Mecha>());
        SetBackingField(player, "controller", controller);
        SetBackingField(player, "mechaArmorModel", armor);
        SetBackingField(game, "mainPlayer", player);
        GameMain.data = game;
        controller.actionDeath = new PlayerAction_Death();
        player.uPosition = DeathPosition;

        // The wreckage explosion needs a Unity scene, so its call is dropped from the restore's
        // body; everything the restore does with the wreckage's position still runs.
        harmony = new Harmony(HarmonyId);
        harmony.Patch(AccessTools.Method(typeof(PlayerLifeManager), "RestoreLocal"),
            transpiler: new HarmonyMethod(typeof(DeadSeatRejoinTest), nameof(WithoutWreckageRendering)));
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchAll(HarmonyId);
        MechaArmorModel.all_wreckages = previousWreckages;
        Multiplayer.Session = previousSession;
        GameMain.data = previousData;
    }

    [TestMethod]
    public void DeadRejoinKeepsItsWreckageWhereTheSeatDied()
    {
        // The wreckage the restore explodes is positioned in the frame the load left active, which
        // is the save writer's frame - here the origin, where the home star sits.
        GameMain.data.relativePos = SaveWriterPosition;
        var wreckages = new List<ArmorWreckage> { Wreckage(VectorLF3.zero), Wreckage(VectorLF3.zero) };
        MechaArmorModel.all_wreckages = wreckages;

        new PlayerLifeManager().RestoreLocal(DeadSeat());

        foreach (var wreckage in wreckages)
        {
            TestAssert.AreEqual(DeathPosition, wreckage.uPos,
                "the wreckage must stay with the seat, not with the frame the save load left active");
            TestAssert.AreEqual(DeathVelocity, wreckage.uVel,
                "the wreckage must drift with the seat's own velocity, not the save writer's");
        }
        TestAssert.AreEqual(DeathPosition, armor.wreckagesCenterUPos);
    }

    [TestMethod]
    public void ANonFiniteVelocitySampleIsNotInjectedIntoTheWreckage()
    {
        // A velocity the seat never really had would integrate straight into the wreckage position,
        // and the dead seat follows the wreckage.
        GameMain.data.relativePos = SaveWriterPosition;
        var wreckages = new List<ArmorWreckage> { Wreckage(VectorLF3.zero) };
        MechaArmorModel.all_wreckages = wreckages;
        var seat = DeadSeat();
        seat.AcceptedVelocityU = new Double3(double.NaN, 0, 0);

        new PlayerLifeManager().RestoreLocal(seat);

        TestAssert.AreEqual(DeathPosition, wreckages[0].uPos);
        TestAssert.AreEqual(VectorLF3.zero, wreckages[0].uVel);
    }

    [TestMethod]
    public void AbsentEmptyAndDeadOnlyWreckageListsAreReportedAsMissing()
    {
        MechaArmorModel.all_wreckages = null;
        TestAssert.IsTrue(PlayerLifeManager.WreckageIsMissing());

        // A seat restored before its armor pieces exist produces no wreckage at all. Vanilla then
        // leaves the center at the origin, which is the middle of the home star.
        MechaArmorModel.all_wreckages = new List<ArmorWreckage>();
        TestAssert.IsTrue(PlayerLifeManager.WreckageIsMissing());

        // Entries whose native side is gone are not wreckage either, so the fallback still has to
        // keep the center off the origin.
        MechaArmorModel.all_wreckages = new List<ArmorWreckage> { null!, null! };
        TestAssert.IsTrue(PlayerLifeManager.WreckageIsMissing());
    }

    [TestMethod]
    public void ASeatInSpaceTakesItsOwnRelativeFrame()
    {
        // Until the first UniverseGameTick, the frame still names the seat that wrote the save.
        GameMain.data.relativePos = SaveWriterPosition;
        SetBackingField(GameMain.data, "localPlanet", null);

        SimulatedWorld.RestoreRelativeFrame(null);

        TestAssert.AreEqual(DeathPosition, GameMain.data.relativePos);
    }

    [TestMethod]
    public void ASpaceSeatIgnoresAStaleBirthPlanetFrame()
    {
        SetBackingField(GameMain.data, "localPlanet", Planet(SaveWriterPosition, Yaw(90f)));
        SimulatedWorld.RestoreRelativeFrame(null);
        TestAssert.AreEqual(DeathPosition, GameMain.data.relativePos);
        AssertSameRotation(Quaternion.identity, GameMain.data.relativeRot);
    }

    [TestMethod]
    public void ASeatOnAnotherPlanetTakesThatPlanetsFrame()
    {
        var seatPlanet = Planet(new VectorLF3(500, 600, 700), Yaw(90f));
        var writerPlanet = Planet(SaveWriterPosition, Yaw(30f));
        SetBackingField(GameMain.data, "localPlanet", writerPlanet);

        SimulatedWorld.RestoreRelativeFrame(seatPlanet);

        TestAssert.AreEqual(seatPlanet.uPosition, GameMain.data.relativePos);
        AssertSameRotation(seatPlanet.runtimeRotation, GameMain.data.relativeRot);
    }

    [TestMethod]
    public void TheSeatsOwnPlanetIsResolvedEvenWhenAnotherPlanetIsActive()
    {
        var seatPlanet = Planet(new VectorLF3(500, 600, 700), Yaw(90f));
        var writerPlanet = Planet(SaveWriterPosition, Quaternion.identity);
        SetField(GameMain.data, "galaxy", GalaxyWith(seatPlanet));
        // The planet the load left active belongs to the seat that wrote the save.
        SetBackingField(GameMain.data, "localPlanet", writerPlanet);

        TestAssert.IsNull(SimulatedWorld.SeatPlanet(-1), "a seat in space has no planet to restore against");
        TestAssert.IsNull(SimulatedWorld.SeatPlanet(0));
        TestAssert.IsNull(SimulatedWorld.SeatPlanet(9999), "an id the galaxy does not know means the same as -1");
        TestAssert.AreEqual(seatPlanet, SimulatedWorld.SeatPlanet(seatPlanet.id));
    }

    private static PlayerData DeadSeat() => new PlayerData(2, 103)
    {
        Life = new PlayerLifeData { IsAlive = false, DeathCount = 1, Revision = 100 },
        UPosition = new Double3(DeathPosition.x, DeathPosition.y, DeathPosition.z),
        AcceptedVelocityU = new Double3(DeathVelocity.x, DeathVelocity.y, DeathVelocity.z)
    };

    private static ArmorWreckage Wreckage(VectorLF3 uPosition)
    {
        var wreckage = Uninitialized<ArmorWreckage>();
        wreckage.uPos = uPosition;
        wreckage.uVel = VectorLF3.zero;
        return wreckage;
    }

    private static PlanetData Planet(VectorLF3 uPosition, Quaternion rotation)
    {
        var planet = Uninitialized<PlanetData>();
        planet.id = 102;
        planet.uPosition = uPosition;
        planet.runtimeRotation = rotation;
        return planet;
    }

    // GalaxyData resolves a planet id as stars[id / 100 - 1].planets[id % 100 - 1].
    private static GalaxyData GalaxyWith(PlanetData planet)
    {
        var galaxy = Uninitialized<GalaxyData>();
        var planets = new PlanetData[planet.id % 100];
        planets[planet.id % 100 - 1] = planet;
        var star = Uninitialized<StarData>();
        star.planets = planets;
        galaxy.stars = new[] { star };
        return galaxy;
    }

    // The wreckage explosion needs a Unity scene, so its call is dropped from the restore's
    // body; everything the restore does with the wreckage's position still runs.
    private static IEnumerable<CodeInstruction> WithoutWreckageRendering(IEnumerable<CodeInstruction> instructions)
    {
        var explode = AccessTools.Method(typeof(MechaArmorModel), "Kill");
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(explode))
            {
                instruction.opcode = OpCodes.Pop;
                instruction.operand = null;
            }
            yield return instruction;
        }
    }

}
