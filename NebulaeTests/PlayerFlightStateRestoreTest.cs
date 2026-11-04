using HarmonyLib;
using global::NebulaeModel.DataStructures;
using NebulaeWorld;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class PlayerFlightStateRestoreTest
{
    private const string HarmonyId = "nebulae.tests.flight-restore";

    // Values the patched game members hand back. The assembly runs its tests one at a time, so
    // static hand-off state is safe here.
    private static Vector3 localPosition;
    private static VectorLF3 surfaceVelocity;
    private static Vector3 controllerVelocity;

    private GameData? previousData;
    private GameCamera? previousCamera;
    private Harmony harmony = null!; // Assigned by TestInitialize.

    private Player player = null!; // Assigned by TestInitialize.
    private PlayerController controller = null!; // Assigned by TestInitialize.
    private GameCamera camera = null!; // Assigned by TestInitialize.

    [TestInitialize]
    public void SetUp()
    {
        previousData = GameMain.data;
        previousCamera = GameCamera.instance;

        var game = Uninitialized<GameData>();
        player = Uninitialized<Player>();
        controller = Uninitialized<PlayerController>();
        SetBackingField(game, "mainPlayer", player);
        SetBackingField(player, "transform", Uninitialized<Transform>());
        SetBackingField(player, "controller", controller);
        SetField(controller, "player", player);
        GameMain.data = game;

        camera = Uninitialized<GameCamera>();
        camera.sailPoser = new SailPoser();
        SetStatic(typeof(GameCamera), "<instance>k__BackingField", camera);

        // The restore reads the player's planet-local position and the planet's surface velocity.
        // Neither can be faked on an uninitialized object, so pin them to values the test controls.
        harmony = new Harmony(HarmonyId);
        harmony.Patch(AccessTools.Method(typeof(Player), "get_position"),
            prefix: new HarmonyMethod(typeof(PlayerFlightStateRestoreTest), nameof(FixedLocalPosition)));
        harmony.Patch(AccessTools.Method(typeof(PlanetData), nameof(PlanetData.GetUniversalVelocityAtLocalPoint)),
            prefix: new HarmonyMethod(typeof(PlayerFlightStateRestoreTest), nameof(FixedSurfaceVelocity)));
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchAll(HarmonyId);
        SetStatic(typeof(GameCamera), "<instance>k__BackingField", previousCamera);
        GameMain.data = previousData;
    }

    [TestMethod]
    public void UniversalVelocityFromLocalKeepsThePlanetRelativeHeading()
    {
        var planetRotation = Yaw(90f);
        var localVelocity = new Vector3(10f, 0f, 0f);
        var surface = new VectorLF3(0, 5, 0);

        var universal = SimulatedWorld.UniversalVelocityFromLocal(planetRotation, localVelocity, surface);

        // Whatever the planet spun to, undoing its current rotation must give the planet-relative
        // sample back — that is what keeps the mecha's heading over the ground unchanged.
        // Quaternion.Inverse is an internal call too, so the unit quaternion is conjugated by hand.
        var conjugate = new Quaternion(-planetRotation.x, -planetRotation.y, -planetRotation.z, planetRotation.w);
        var relative = conjugate * (Vector3)universal - (Vector3)surface;
        TestAssert.AreEqual(10f, relative.x, 1e-4);
        TestAssert.AreEqual(0f, relative.y, 1e-4);
        TestAssert.AreEqual(0f, relative.z, 1e-4);
    }

    [TestMethod]
    public void SailRejoinInSpaceRestoresVelocityAndAlignsTheSailCamera()
    {
        player.uRotation = Yaw(45f);
        var data = Seat(-1, EMovementState.Sail, new Double3(10, 20, 30), new Float3(1f, 2f, 3f));

        SimulatedWorld.RestoreSavedFlightState(data);

        TestAssert.AreEqual(EMovementState.Sail, player.movementState);
        TestAssert.AreEqual(new VectorLF3(10, 20, 30), player.uVelocity);
        TestAssert.AreEqual(new Vector3(10f, 20f, 30f), controller.velocity);
        // Sail mode steers by the camera, so it has to be aimed at the facing the client left with
        // instead of the identity the load happened to leave in the sail poser.
        AssertSameRotation(player.uRotation, camera.sailPoser.targetURot);
        AssertSameRotation(player.uRotation, camera.sailPoser.targetURotWanted);
    }

    [TestMethod]
    public void WalkSeatRejoiningInSpaceIsPromotedToFly()
    {
        var data = Seat(-1, EMovementState.Walk, default, default);

        // The promoted state is no longer sailing, so the controller hands the sample to the
        // rigidbody — capture that hand-off instead of letting it reach a fake rigidbody.
        var velocitySetter = AccessTools.Method(typeof(PlayerController), "set_velocity");
        harmony.Patch(velocitySetter, prefix: new HarmonyMethod(typeof(PlayerFlightStateRestoreTest), nameof(CaptureControllerVelocity)));
        try
        {
            SimulatedWorld.RestoreSavedFlightState(data);
        }
        finally
        {
            harmony.Unpatch(velocitySetter, HarmonyPatchType.All, HarmonyId);
        }

        TestAssert.AreEqual(EMovementState.Fly, player.movementState);
        TestAssert.AreEqual(VectorLF3.zero, player.uVelocity);
        TestAssert.AreEqual(Vector3.zero, controllerVelocity);
    }

    [TestMethod]
    public void NonFiniteVelocitySampleIsNotInjected()
    {
        player.uVelocity = new VectorLF3(7, 8, 9);
        var data = Seat(-1, EMovementState.Sail, new Double3(double.NaN, 0, 0), new Float3(0f, 0f, 0f));

        SimulatedWorld.RestoreSavedFlightState(data);

        TestAssert.AreEqual(EMovementState.Sail, player.movementState);
        TestAssert.AreEqual(new VectorLF3(7, 8, 9), player.uVelocity);
    }

    [TestMethod]
    public void SailRejoinOnAPlanetRebuildsTheUniversalVelocityFromTheLocalSample()
    {
        localPosition = new Vector3(0f, 0f, 220f); // 20 m above a 200 m radius planet
        surfaceVelocity = new VectorLF3(0, 5, 0);
        var planetRotation = Yaw(90f); // the planet kept spinning while the client was away
        SetUpPlanet(planetRotation);
        player.uRotation = Yaw(45f);
        var data = Seat(102, EMovementState.Sail, new Double3(999, 999, 999), new Float3(10f, 0f, 0f));

        SimulatedWorld.RestoreSavedFlightState(data);

        TestAssert.AreEqual(EMovementState.Sail, player.movementState);
        // The frozen universal sample is discarded in favour of planetRotation * velocityL + surface.
        TestAssert.AreEqual(0.0, player.uVelocity.x, 1e-3);
        TestAssert.AreEqual(5.0, player.uVelocity.y, 1e-3);
        TestAssert.AreEqual(-10.0, player.uVelocity.z, 1e-3);
        TestAssert.AreEqual(new Vector3(10f, 0f, 0f), controller.velocity);
        AssertSameRotation(player.uRotation, camera.sailPoser.targetURot);
    }

    [TestMethod]
    public void GroundedSeatOnAPlanetKeepsItsState()
    {
        localPosition = new Vector3(0f, 0f, 200.5f); // half a metre above the surface
        SetUpPlanet(Quaternion.identity);
        var data = Seat(102, EMovementState.Walk, new Double3(1, 2, 3), new Float3(4f, 5f, 6f));

        SimulatedWorld.RestoreSavedFlightState(data);

        TestAssert.AreEqual(EMovementState.Walk, player.movementState);
        TestAssert.AreEqual(VectorLF3.zero, player.uVelocity);
    }

    [TestMethod]
    public void FlyRejoinOnAPlanetHoldsTheAltitudeItCameBackTo()
    {
        localPosition = new Vector3(0f, 0f, 320f); // 120 m above the surface
        surfaceVelocity = new VectorLF3(0, 5, 0);
        SetUpPlanet(Quaternion.identity);
        controller.actionFly = Uninitialized<PlayerMove_Fly>();
        var data = Seat(102, EMovementState.Fly, new Double3(1, 2, 3), new Float3(4f, 5f, 6f));

        // Flying is not sailing, so the controller hands the sample to the rigidbody — capture that
        // hand-off instead of letting it reach a fake rigidbody.
        var velocitySetter = AccessTools.Method(typeof(PlayerController), "set_velocity");
        harmony.Patch(velocitySetter, prefix: new HarmonyMethod(typeof(PlayerFlightStateRestoreTest), nameof(CaptureControllerVelocity)));
        try
        {
            SimulatedWorld.RestoreSavedFlightState(data);
        }
        finally
        {
            harmony.Unpatch(velocitySetter, HarmonyPatchType.All, HarmonyId);
        }

        TestAssert.AreEqual(EMovementState.Fly, player.movementState);
        TestAssert.AreEqual(120f, controller.actionFly.targetAltitude, 1e-4);
        // The planet-relative sample goes to the rigidbody, and the controller derives the
        // universal velocity from it on the next tick.
        TestAssert.AreEqual(new Vector3(4f, 5f, 6f), controllerVelocity);
    }

    private static PlayerData Seat(int localPlanetId, EMovementState movementState, Double3 velocityU, Float3 velocityL)
    {
        return new PlayerData
        {
            Mecha = new MechaData(),
            LocalPlanetId = localPlanetId,
            LocalStarId = localPlanetId > 0 ? localPlanetId / 100 : -1,
            LocalPlanetPosition = new Float3(localPosition),
            AcceptedMovementState = movementState,
            AcceptedVelocityU = velocityU,
            AcceptedVelocityL = velocityL
        };
    }

    private void SetUpPlanet(Quaternion planetRotation, float radius = 200f)
    {
        var planet = Uninitialized<PlanetData>();
        planet.runtimeRotation = planetRotation;
        planet.radius = radius;
        planet.scale = 1f;
        planet.type = EPlanetType.Ocean;
        var star = Uninitialized<StarData>();
        star.planets = new[] { null, planet };
        var galaxy = Uninitialized<GalaxyData>();
        galaxy.stars = new[] { star };
        SetField(GameMain.data, "galaxy", galaxy);
    }

    private static bool FixedLocalPosition(ref Vector3 __result)
    {
        __result = localPosition;
        return false;
    }

    private static bool FixedSurfaceVelocity(ref VectorLF3 __result)
    {
        __result = surfaceVelocity;
        return false;
    }

    private static bool CaptureControllerVelocity(Vector3 value)
    {
        controllerVelocity = value;
        return false;
    }

}
