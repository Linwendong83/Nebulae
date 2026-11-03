using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Factory.Assembler;
using NebulaeModel.Packets.Factory.Belt;
using NebulaeModel.Packets.Logistics;
using NebulaeModel.Packets.Players;
using NebulaeModel.Packets.Routers;
using NebulaeModel.Packets.Statistics;
using NebulaeModel.Packets.Universe;
using NebulaeModel.Packets.Universe.Editor;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

/// <summary>
/// Covers the packets that bundle several updates into one message: their wire layout, their
/// self-check, and the envelope that lets one client message carry several inner packets.
/// </summary>
[TestClass]
[DoNotParallelize]
internal class PacketBatchingTest
{
    private static NebulaeNetPacketProcessor NewProcessor()
    {
        var processor = new NebulaeNetPacketProcessor();
        PacketUtils.RegisterNestedTypes(processor);
        return processor;
    }

    private static T RoundTrip<T>(T packet) where T : class, new() =>
        TestSupport.RoundTrip(packet, PacketUtils.RegisterNestedTypes);

    [TestMethod]
    public void StationStorageBatchRoundTripsEveryArray()
    {
        var sent = new StationStorageUIBatchPacket(101, 7, 42,
            new[] { 0, 3, 9 }, new[] { 120, 0, 5000 }, new[] { 0, 100, 2500 },
            new byte[] { (byte)StationUI.EUISettings.SetDroneCount, (byte)StationUI.EUISettings.SetWarperCount },
            new[] { 12, 3 });

        var received = RoundTrip(sent);

        TestAssert.IsNotNull(received);
        TestAssert.AreEqual(101, received.PlanetId);
        TestAssert.AreEqual(7, received.StationId);
        TestAssert.AreEqual(42, received.StationGId);
        CollectionAssert.AreEqual(new[] { 0, 3, 9 }, received.StorageIdx);
        CollectionAssert.AreEqual(new[] { 120, 0, 5000 }, received.ItemCount);
        CollectionAssert.AreEqual(new[] { 0, 100, 2500 }, received.ItemInc);
        CollectionAssert.AreEqual(sent.SettingIndex, received.SettingIndex);
        CollectionAssert.AreEqual(new[] { 12, 3 }, received.SettingValue);
        TestAssert.IsFalse(received.ShouldRefund);
    }

    [TestMethod]
    public void EveryBatchPacketRefusesArraysThatDoNotLineUp()
    {
        var mismatched = new StationStorageUIBatchPacket(101, 7, 42,
            new[] { 0, 3 }, new[] { 120 }, new[] { 0, 100 }, Array.Empty<byte>(), Array.Empty<int>());
        TestAssert.IsFalse(mismatched.IsConsistent());
        var tooManyStorage = new StationStorageUIBatchPacket(101, 7, 42,
            Enumerable.Range(0, StationStorageUIBatchPacket.MaxStorageEntries + 1).ToArray(),
            new int[StationStorageUIBatchPacket.MaxStorageEntries + 1],
            new int[StationStorageUIBatchPacket.MaxStorageEntries + 1], null, null);
        TestAssert.IsFalse(tooManyStorage.IsConsistent());
        var settingsOnlyOnOneSide = new StationStorageUIBatchPacket(101, 7, 42,
            Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), new byte[] { 1 }, null);
        TestAssert.IsFalse(settingsOnlyOnOneSide.IsConsistent());
        var empty = new StationStorageUIBatchPacket(101, 7, 42,
            Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), null, null);
        TestAssert.IsTrue(empty.IsConsistent());

        // A colour blob that does not hold four bytes per entry describes no colour at all.
        TestAssert.IsFalse(new DysonSphereColorChangeBatchPacket(3, 12, new byte[] { 0 }, new[] { 1 },
            new byte[] { 1, 2, 3 }).IsConsistent());
        // A type per entry is required, so a colour without one is refused rather than defaulted.
        TestAssert.IsFalse(new DysonSphereColorChangeBatchPacket(3, 12, null, new[] { 1 },
            new byte[] { 1, 2, 3, 4 }).IsConsistent());
        TestAssert.IsFalse(new DysonSphereColorChangeBatchPacket(3, 12, new byte[] { 0 }, Array.Empty<int>(),
            Array.Empty<byte>()).IsConsistent());

        var shortSailBatch = new DysonSailBatchPacket(5,
            new[] { 2, 2 }, new long[] { 1000L },
            new[] { 1f, 2f }, new[] { 3f, 4f }, new[] { 5f, 6f }, new[] { 7f, 8f },
            new[] { 9f, 10f }, new[] { 11f, 12f }, new[] { 13f, 14f }, new[] { 15f, 16f });
        TestAssert.IsFalse(shortSailBatch.IsConsistent());
        var tooManySails = new DysonSailBatchPacket(5,
            Enumerable.Repeat(1, DysonSailBatchPacket.MaxSails + 1).ToArray(),
            new long[DysonSailBatchPacket.MaxSails + 1],
            new float[DysonSailBatchPacket.MaxSails + 1], new float[DysonSailBatchPacket.MaxSails + 1],
            new float[DysonSailBatchPacket.MaxSails + 1], new float[DysonSailBatchPacket.MaxSails + 1],
            new float[DysonSailBatchPacket.MaxSails + 1], new float[DysonSailBatchPacket.MaxSails + 1],
            new float[DysonSailBatchPacket.MaxSails + 1], new float[DysonSailBatchPacket.MaxSails + 1]);
        TestAssert.IsFalse(tooManySails.IsConsistent());

        var mismatchedBelt = new BeltUpdatePutItemsPacket(101, new[] { 3, 4 }, new[] { 1001 },
            new byte[] { 1, 20 }, new byte[] { 0, 1 });
        TestAssert.IsFalse(mismatchedBelt.IsConsistent());
        var tooManyBelt = new BeltUpdatePutItemsPacket(101,
            new int[BeltUpdatePutItemsPacket.MaxEntries + 1], new int[BeltUpdatePutItemsPacket.MaxEntries + 1],
            new byte[BeltUpdatePutItemsPacket.MaxEntries + 1], new byte[BeltUpdatePutItemsPacket.MaxEntries + 1]);
        TestAssert.IsFalse(tooManyBelt.IsConsistent());
    }

    [TestMethod]
    public void StarEnvelopeCarriesSeveralInnerPacketsInOneMessage()
    {
        var processor = NewProcessor();
        var inner = new[]
        {
            processor.Write(new FactoryProductPacket { Recipe = 1, Count = 2, Products = new[] { 1001, 1002 } }),
            processor.Write(new FactoryProductPacket { Recipe = -1, Count = 3600, Inputs = new[] { 6001 }, Served = new[] { 7200 }, IncServed = new[] { 0 } }),
            processor.Write(new StationStorageUIBatchPacket(101, 3, 9, new[] { 1 }, new[] { 2 }, new[] { 0 }, null, null))
        };

        StarBroadcastPacket? received = null;
        processor.SubscribeReusable<StarBroadcastPacket>(p => received = p);
        processor.ReadPacket(new NetDataReader(processor.Write(new StarBroadcastPacket(inner, 77))));

        TestAssert.IsNotNull(received);
        TestAssert.AreEqual(77, received.StarId);
        TestAssert.HasCount(3, received.PacketObjects);
        // Each blob keeps its own packet id, so the host re-enqueues them as the packets they were.
        foreach (var blob in received.PacketObjects)
        {
            var reader = new NetDataReader(blob);
            TestAssert.IsGreaterThan(8, reader.AvailableBytes);
        }
    }

    [TestMethod]
    public void SinglePacketEnvelopeStillCarriesOneBlob()
    {
        var processor = NewProcessor();
        StarBroadcastPacket? received = null;
        processor.SubscribeReusable<StarBroadcastPacket>(p => received = p);
        var blob = processor.Write(new FactoryProductPacket { Recipe = 1, Count = 1, Products = new[] { 1001 } });
        processor.ReadPacket(new NetDataReader(processor.Write(new StarBroadcastPacket(blob, 5))));

        TestAssert.IsNotNull(received);
        TestAssert.HasCount(1, received.PacketObjects);
        CollectionAssert.AreEqual(blob, received.PacketObjects[0]);
    }

    [TestMethod]
    public void PlanetEnvelopeRoundTripsItsPlanetIdAndBlobs()
    {
        var processor = NewProcessor();
        PlanetBroadcastPacket? received = null;
        processor.SubscribeReusable<PlanetBroadcastPacket>(p => received = p);
        processor.ReadPacket(new NetDataReader(processor.Write(new PlanetBroadcastPacket(
            new[] { processor.Write(new FactoryProductPacket { Recipe = 1, Count = 1, Products = new[] { 1001 } }) }, 303))));

        TestAssert.IsNotNull(received);
        TestAssert.AreEqual(303, received.PlanetId);
        TestAssert.HasCount(1, received.PacketObjects);
    }

    [TestMethod]
    public void DysonColorBatchRoundTripsFourByteColours()
    {
        var sent = new DysonSphereColorChangeBatchPacket(3, 12,
            new byte[] { (byte)DysonSphereComponentType.Node, (byte)DysonSphereComponentType.Shell },
            new[] { 40, 7 },
            new byte[] { 255, 128, 0, 255, 10, 20, 30, 40 });

        var received = RoundTrip(sent);

        TestAssert.AreEqual(3, received.StarIndex);
        TestAssert.AreEqual(12, received.LayerId);
        CollectionAssert.AreEqual(new[] { 40, 7 }, received.Index);
        CollectionAssert.AreEqual(sent.Rgba, received.Rgba);
        TestAssert.IsTrue(received.IsConsistent());
    }

    [TestMethod]
    public void DysonSailBatchRoundTripsEverySail()
    {
        var sent = new DysonSailBatchPacket(5,
            new[] { 2, 2 }, new long[] { 1000L, 2000L },
            new[] { 1f, 2f }, new[] { 3f, 4f }, new[] { 5f, 6f }, new[] { 7f, 8f },
            new[] { 9f, 10f }, new[] { 11f, 12f }, new[] { 13f, 14f }, new[] { 15f, 16f });

        var received = RoundTrip(sent);

        TestAssert.AreEqual(5, received.StarIndex);
        CollectionAssert.AreEqual(new[] { 2, 2 }, received.OrbitId);
        CollectionAssert.AreEqual(new long[] { 1000L, 2000L }, received.ExpiryTime);
        CollectionAssert.AreEqual(new[] { 1f, 2f }, received.st);
        CollectionAssert.AreEqual(new[] { 15f, 16f }, received.gs);
        TestAssert.IsTrue(received.IsConsistent());
    }

    [TestMethod]
    public void BeltPutItemsBatchRoundTripsEveryInsertion()
    {
        var sent = new BeltUpdatePutItemsPacket(101,
            new[] { 3, 4, 5 }, new[] { 1001, 1002, 1003 },
            new byte[] { 1, 20, 100 }, new byte[] { 0, 1, 2 });

        var received = RoundTrip(sent);

        TestAssert.AreEqual(101, received.PlanetId);
        CollectionAssert.AreEqual(new[] { 3, 4, 5 }, received.BeltId);
        CollectionAssert.AreEqual(new[] { 1001, 1002, 1003 }, received.ItemId);
        CollectionAssert.AreEqual(new byte[] { 1, 20, 100 }, received.ItemCount);
        CollectionAssert.AreEqual(new byte[] { 0, 1, 2 }, received.ItemInc);
        TestAssert.IsTrue(received.IsConsistent());
    }

    [TestMethod]
    public void MechaStatBatchRoundTripsNettedProductionAndConsumption()
    {
        var sent = new PlayerMechaStatBatchPacket(new[] { 1001, 1002 }, new[] { 40, -7 });

        var received = RoundTrip(sent);

        CollectionAssert.AreEqual(new[] { 1001, 1002 }, received.ItemId);
        CollectionAssert.AreEqual(new[] { 40, -7 }, received.ItemCount);
        TestAssert.IsTrue(received.IsConsistent());
        TestAssert.IsFalse(new PlayerMechaStatBatchPacket(new[] { 1 }, new[] { 1, 2 }).IsConsistent());
    }

    [TestMethod]
    public void MovementDisplayDropsTheFieldsOnlyTheHostReads()
    {
        var display = new PlayerMovementDisplay
        {
            PlayerId = 7,
            LocalPlanetId = 101,
            MovementState = (byte)EMovementState.Fly,
            Flags = PlayerMovement.EFlags.warping
        };

        var properties = typeof(PlayerMovementDisplay).GetProperties();
        foreach (var forbidden in new[] { "VelocityU", "BuildArea", "ConstructionDroneCount", "ConstructionDronesEnabled", "CanLaunchConstructionDrone" })
        {
            TestAssert.IsNull(properties.FirstOrDefault(p => p.Name == forbidden),
                forbidden + " is only ever read by the host, so relaying it spends bytes nobody consumes.");
        }
        TestAssert.AreEqual(7, display.PlayerId);
        TestAssert.AreEqual(101, display.LocalPlanetId);
    }

    [TestMethod]
    public void QuantisedAnglesRoundTripWithinAStepOfAFullTurn()
    {
        // The largest error any angle can carry is one step of the range it is stored against.
        var maxError = 360f / (PlayerMovementDisplay.EulerTurns + 1f);
        foreach (var angle in new float[] { 0f, 0.25f, 45f, 90f, 179.9f, 180f, 270f, 359.99f })
        {
            var decoded = PlayerMovementDisplay.DecodeEuler(PlayerMovementDisplay.EncodeEuler(angle));
            TestAssert.IsLessThanOrEqualTo(maxError,
                Math.Abs(decoded - angle), $"angle {angle} decoded as {decoded}, error {Math.Abs(decoded - angle)} exceeds {maxError}");
        }
    }

    [TestMethod]
    public void QuantisedAnglesClampInsteadOfWrapping()
    {
        // Unity reports euler angles in [0, 360). A value outside that must not wrap around to the other
        // side of the turn, which would spin the remote body the long way round.
        TestAssert.AreEqual(0, PlayerMovementDisplay.EncodeEuler(-30f));
        TestAssert.AreEqual(ushort.MaxValue, PlayerMovementDisplay.EncodeEuler(720f));
        TestAssert.IsFalse(float.IsNaN(PlayerMovementDisplay.DecodeEuler(PlayerMovementDisplay.EncodeEuler(float.NaN))));
    }

    [TestMethod]
    public void QuantisedSpeedsCoverTheGamesRangeAndStayFineEnoughForAnimationThresholds()
    {
        // The game caps sail speed at 1000, and the animation reads a 0.15 threshold, so the encoding has
        // to reach the cap and resolve well below the threshold.
        var maxSpeed = ushort.MaxValue / PlayerMovementDisplay.SpeedScale;
        TestAssert.IsGreaterThanOrEqualTo(1000f, maxSpeed, "the speed encoding must reach the game's sail speed cap");

        var step = 1f / PlayerMovementDisplay.SpeedScale;
        TestAssert.IsLessThan(0.05f, step, "one speed step must be finer than the animation's 0.15 threshold");

        foreach (var speed in new float[] { 0f, 0.15f, 5f, 12.5f, 100f, 999.9f, 1000f, 5000f })
        {
            var decoded = PlayerMovementDisplay.DecodeSpeed(PlayerMovementDisplay.EncodeSpeed(speed));
            TestAssert.IsLessThanOrEqualTo(step,
                Math.Abs(decoded - Math.Min(speed, maxSpeed)), $"speed {speed} decoded as {decoded}");
        }
    }

    [TestMethod]
    public void QuantisedVerticalSpeedKeepsTheSign()
    {
        // A falling player has a negative vertical speed, and the trail length reads it through a Max, so
        // clamping it to zero would freeze the effect rather than shorten it.
        var limit = PlayerMovementDisplay.VerticalSpeedMax;
        foreach (var speed in new float[] { 0f, -0.5f, -3f, -100f, 0.5f, 3f, 100f })
        {
            var decoded = PlayerMovementDisplay.DecodeVerticalSpeed(PlayerMovementDisplay.EncodeVerticalSpeed(speed));
            TestAssert.IsLessThanOrEqualTo(1f / PlayerMovementDisplay.VerticalSpeedScale + 1e-4f,
                Math.Abs(decoded - speed), $"vertical speed {speed} decoded as {decoded}");
            TestAssert.IsTrue(Math.Sign(decoded) == Math.Sign(speed) || speed == 0f,
                $"vertical speed {speed} decoded as {decoded}, losing the sign");
        }

        // Past the representable range the value clamps instead of wrapping to the other sign.
        TestAssert.IsLessThanOrEqualTo(-limit, PlayerMovementDisplay.DecodeVerticalSpeed(
            PlayerMovementDisplay.EncodeVerticalSpeed(-100000f)));
        TestAssert.IsGreaterThanOrEqualTo(limit, PlayerMovementDisplay.DecodeVerticalSpeed(
            PlayerMovementDisplay.EncodeVerticalSpeed(100000f)));
    }

    [TestMethod]
    public void QuantisedWeightsAndTurningStayInsideTheirRange()
    {
        foreach (var weight in new float[] { 0f, 0.001f, 0.5f, 0.999f, 1f })
        {
            var decoded = PlayerMovementDisplay.DecodeWeight(PlayerMovementDisplay.EncodeWeight(weight));
            TestAssert.IsTrue(decoded >= 0f && decoded <= 1f, $"weight {weight} decoded as {decoded}");
            TestAssert.IsLessThanOrEqualTo(1f / byte.MaxValue + 1e-6f,
                Math.Abs(decoded - weight), $"weight {weight} decoded as {decoded}");
        }

        foreach (var turning in new float[] { -1f, -0.45f, 0f, 0.05f, 0.45f, 1f })
        {
            var decoded = PlayerMovementDisplay.DecodeTurning(PlayerMovementDisplay.EncodeTurning(turning));
            TestAssert.IsTrue(decoded >= -1f && decoded <= 1f, $"turning {turning} decoded as {decoded}");
            TestAssert.IsLessThanOrEqualTo(2f / (PlayerMovementDisplay.EulerTurns + 1f) + 1e-6f,
                Math.Abs(decoded - turning), $"turning {turning} decoded as {decoded}");
        }

        // Out-of-range values must clamp, not wrap into the opposite direction.
        TestAssert.AreEqual(-1f, PlayerMovementDisplay.DecodeTurning(PlayerMovementDisplay.EncodeTurning(-5f)));
        TestAssert.AreEqual(1f, PlayerMovementDisplay.DecodeTurning(PlayerMovementDisplay.EncodeTurning(5f)));
    }

    [TestMethod]
    public void MovementDisplayRoundTripsFromAFullPrecisionPose()
    {
        var processor = NewProcessor();
        // Built through the parameterless constructor: the animator-taking one reads a live PlayerAnimator,
        // and this test has no game running.
        var source = new PlayerMovement
        {
            PlayerId = 3,
            LocalPlanetId = 101,
            LocalPlanetPosition = new Float3(1.5f, -2.25f, 30f),
            UPosition = new Double3(12345.5, -6789.25, 100.125),
            Rotation = new Float3(10f, 180f, 350f),
            BodyRotation = new Float3(0f, 90f, 270f),
            MovementState = EMovementState.Sail,
            HorzSpeed = 12.5f,
            VertSpeed = -3f,
            Turning = 0.45f,
            JumpWeight = 0.75f,
            JumpNormalizedTime = 0.25f,
            IdleAnimIndex = 3,
            MiningAnimIndex = 5,
            MiningWeight = 1f,
            Flags = PlayerMovement.EFlags.isGrounded | PlayerMovement.EFlags.chargeShieldBurst
        };

        PlayerMovementDisplay? received = null;
        processor.SubscribeReusable<PlayerMovementDisplay>(p => received = p);
        processor.ReadPacket(new NetDataReader(processor.Write(new PlayerMovementDisplay(source))));

        TestAssert.IsNotNull(received);
        TestAssert.AreEqual(3, received.PlayerId);
        TestAssert.AreEqual(101, received.LocalPlanetId);
        CollectionAssert.AreEqual(new[] { 1.5f, -2.25f, 30f }, new[] { received.LocalPlanetPosition.x, received.LocalPlanetPosition.y, received.LocalPlanetPosition.z });
        TestAssert.AreEqual(12345.5, received.UPosition.x);
        TestAssert.AreEqual(-6789.25, received.UPosition.y);
        TestAssert.AreEqual(100.125, received.UPosition.z);
        TestAssert.AreEqual((byte)EMovementState.Sail, received.MovementState);
        TestAssert.AreEqual(3, received.IdleAnimIndex);
        TestAssert.AreEqual(5, received.MiningAnimIndex);
        TestAssert.AreEqual(PlayerMovement.EFlags.isGrounded | PlayerMovement.EFlags.chargeShieldBurst, received.Flags);

        // The quantised fields carry the values the animation reads, within one step.
        TestAssert.IsLessThan(0.01f, Math.Abs(received.Rotation.x - 10f));
        TestAssert.IsLessThan(0.01f, Math.Abs(received.Rotation.y - 180f));
        TestAssert.IsLessThan(0.01f, Math.Abs(received.BodyRotation.y - 90f));
        TestAssert.IsLessThan(0.05f, Math.Abs(received.HorzSpeedValue - 12.5f));
        TestAssert.IsLessThan(0.05f, Math.Abs(received.VertSpeedValue + 3f));
        TestAssert.IsLessThan(0.001f, Math.Abs(received.TurningValue - 0.45f));
        TestAssert.IsLessThan(0.01f, Math.Abs(received.JumpWeightValue - 0.75f));
        TestAssert.IsLessThan(0.01f, Math.Abs(received.MiningWeightValue - 1f));
    }

    [TestMethod]
    public void MapPositionRoundTripsItsPlanetAndUniversePosition()
    {
        var received = RoundTrip(new PlayerMapPosition(9, 202, new Double3(1.5, -2.5, 3.5), 2));

        TestAssert.AreEqual(9, received.PlayerId);
        TestAssert.AreEqual(202, received.LocalPlanetId);
        TestAssert.AreEqual(2, received.LocalStarId);
        TestAssert.AreEqual(1.5, received.UPosition.x);
        TestAssert.AreEqual(-2.5, received.UPosition.y);
        TestAssert.AreEqual(3.5, received.UPosition.z);
    }

    [TestMethod]
    public void SandGainedRoundTripsTheAmountCollected()
    {
        var received = RoundTrip(new PlayerSandGained(1234));

        TestAssert.AreEqual(1234, received.Gained);
    }

    [TestMethod]
    public void MovementDisplayRefusesAPoseItCannotPlace()
    {
        // A movement state the game does not define, or a planet that does not exist, describes a body the
        // receiver has nowhere to put.
        TestAssert.IsFalse(new PlayerMovementDisplay { MovementState = 200 }.HasFinitePose());
        TestAssert.IsFalse(new PlayerMovementDisplay
        {
            UPosition = new Double3(double.NaN, 0, 0)
        }.HasFinitePose());
        TestAssert.IsFalse(new PlayerMovementDisplay
        {
            UPosition = new Double3(1e13, 0, 0)
        }.HasFinitePose());

        var ordinary = new PlayerMovementDisplay { LocalPlanetId = 101, MovementState = 1 };
        TestAssert.IsTrue(ordinary.HasFinitePose());
        // A planet that does not exist is the processor's question, not the wire check's.
        TestAssert.IsTrue(new PlayerMovementDisplay { LocalPlanetId = 9999999, MovementState = 1 }.HasFinitePose());
    }
}
