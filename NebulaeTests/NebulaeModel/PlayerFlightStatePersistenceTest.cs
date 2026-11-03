using global::NebulaeModel.DataStructures;
using global::NebulaeModel.Networking.Serialization;
using global::NebulaeModel.Packets.Players;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class PlayerFlightStatePersistenceTest
{
    private const int FlightTailBytes =
        1 + 1 + 3 * sizeof(double) + 3 * sizeof(float);
    private const int StarTailBytes = sizeof(int);
    private const int CurrentTailBytes = FlightTailBytes + StarTailBytes;

    [TestMethod]
    public void FlightStateSurvivesSerializationRoundTrip()
    {
        var player = SailingPlayer();
        var restored = DeserializePlayer(SerializePlayer(player));
        TestAssert.AreEqual(EMovementState.Sail, restored.AcceptedMovementState);
        TestAssert.IsTrue(restored.AcceptedWarping);
        TestAssert.AreEqual(120.5, restored.AcceptedVelocityU.x);
        TestAssert.AreEqual(-8.25, restored.AcceptedVelocityU.y);
        TestAssert.AreEqual(0.001, restored.AcceptedVelocityU.z);
        TestAssert.AreEqual(1f, restored.AcceptedVelocityL.x);
        TestAssert.AreEqual(2f, restored.AcceptedVelocityL.y);
        TestAssert.AreEqual(3f, restored.AcceptedVelocityL.z);
    }

    [TestMethod]
    public void LocalStarSurvivesSerializationRoundTrip()
    {
        var space = SailingPlayer();
        space.LocalStarId = -1;
        TestAssert.AreEqual(-1, DeserializePlayer(SerializePlayer(space)).LocalStarId);

        var onPlanet = SailingPlayer();
        onPlanet.LocalPlanetId = 102;
        onPlanet.LocalStarId = 7;
        TestAssert.AreEqual(7, DeserializePlayer(SerializePlayer(onPlanet)).LocalStarId);
    }

    [TestMethod]
    public void GroundedPlayerRoundTripsWithWalkAndZeroVelocity()
    {
        var player = new PlayerData { Mecha = new MechaData(), LocalPlanetId = 1 };
        var restored = DeserializePlayer(SerializePlayer(player));
        TestAssert.AreEqual(EMovementState.Walk, restored.AcceptedMovementState);
        TestAssert.IsFalse(restored.AcceptedWarping);
        TestAssert.AreEqual(default, restored.AcceptedVelocityU);
        TestAssert.AreEqual(default, restored.AcceptedVelocityL);
        // A seat that never reported a star keeps the "unknown" default instead of inventing one.
        TestAssert.AreEqual(0, restored.LocalStarId);
    }

    [TestMethod]
    [DataRow(10, CurrentTailBytes, EMovementState.Walk, 0d)]
    [DataRow(11, StarTailBytes, EMovementState.Sail, 120.5)]
    public void SavedPlayerRestoresOnlyItsRevisionFields(int revision, int omittedBytes,
        EMovementState movement, double velocityX)
    {
        var bytes = SerializePlayer(SailingPlayer());
        var legacy = bytes.AsSpan(0, bytes.Length - omittedBytes).ToArray();
        var restored = SailingPlayer();
        var reader = new NetDataReader(legacy);
        restored.Import(reader, revision);
        TestAssert.AreEqual(movement, restored.AcceptedMovementState);
        if (revision >= 11) TestAssert.AreEqual(velocityX, restored.AcceptedVelocityU.x);
        else TestAssert.AreEqual(default, restored.AcceptedVelocityU);
        TestAssert.AreEqual(0, restored.LocalStarId);
        TestAssert.AreEqual(0, reader.AvailableBytes);
    }

    [TestMethod]
    public void ConsecutiveEntriesInOneStreamKeepTheirOwnFlightState()
    {
        // Server saves hold every seat back to back in a single stream; a tail read that
        // guesses by remaining bytes would steal the next seat's header.
        var writer = new NetDataWriter();
        writer.Put(SerializePlayer(SailingPlayer()));
        writer.Put(SerializePlayer(new PlayerData { Mecha = new MechaData(), LocalPlanetId = 3 }));
        var reader = new NetDataReader(writer.CopyData());

        var first = new PlayerData();
        first.Deserialize(reader);
        var second = new PlayerData();
        second.Deserialize(reader);

        TestAssert.AreEqual(EMovementState.Sail, first.AcceptedMovementState);
        TestAssert.AreEqual(-1, first.LocalPlanetId);
        TestAssert.AreEqual(-1, first.LocalStarId);
        TestAssert.AreEqual(EMovementState.Walk, second.AcceptedMovementState);
        TestAssert.AreEqual(3, second.LocalPlanetId);
        TestAssert.AreEqual(0, second.LocalStarId);
    }

    [TestMethod]
    public void HasFinitePoseRejectsNonFiniteVelocities()
    {
        TestAssert.IsTrue(ValidPose().HasFinitePose());

        var nanUniversal = ValidPose();
        nanUniversal.VelocityU = new Double3(double.NaN, 0, 0);
        TestAssert.IsFalse(nanUniversal.HasFinitePose());

        var infiniteUniversal = ValidPose();
        infiniteUniversal.VelocityU = new Double3(0, double.PositiveInfinity, 0);
        TestAssert.IsFalse(infiniteUniversal.HasFinitePose());

        var nanLocal = ValidPose();
        nanLocal.VelocityL = new Float3(0f, float.NaN, 0f);
        TestAssert.IsFalse(nanLocal.HasFinitePose());
    }

    private static PlayerData SailingPlayer() => new()
    {
        Mecha = new MechaData(),
        LocalPlanetId = -1,
        LocalStarId = -1,
        AcceptedMovementState = EMovementState.Sail,
        AcceptedWarping = true,
        AcceptedVelocityU = new Double3(120.5, -8.25, 0.001),
        AcceptedVelocityL = new Float3(1f, 2f, 3f)
    };

    private static PlayerMovement ValidPose() => new()
    {
        UPosition = new Double3(1, 2, 3),
        LocalPlanetPosition = new Float3(1f, 2f, 3f),
        Rotation = new Float3(0f, 90f, 0f),
        BodyRotation = new Float3(0f, 45f, 0f),
        MovementState = EMovementState.Sail
    };

    private static PlayerData DeserializePlayer(byte[] bytes)
    {
        var player = new PlayerData();
        player.Deserialize(new NetDataReader(bytes));
        return player;
    }
}
