using global::NebulaeModel.DataStructures;
using NebulaeWorld.GameStates;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class MetadataSnapshotFormatTest
{
    private static PlayerData Sample() => new(2, 103, "Lin")
    {
        LocalStarId = 7,
        Life = new PlayerLifeData { IsAlive = true, DeathCount = 1, Revision = 41 },
        AcceptedMovementState = EMovementState.Sail,
        AcceptedWarping = true,
        AcceptedVelocityU = new Double3(1.5, -2.25, 3.75),
        AcceptedVelocityL = new Float3(4.5f, 5.5f, 6.5f)
    };

    [TestMethod]
    public void CurrentSnapshotRoundTripsThroughReadPlayer()
    {
        var restored = MetadataTransactionManager.ReadPlayer(SerializePlayer(Sample()));

        TestAssert.IsNotNull(restored);
        TestAssert.AreEqual("Lin", restored.Username);
        TestAssert.AreEqual(41, restored.Life.Revision);
        TestAssert.AreEqual(EMovementState.Sail, restored.AcceptedMovementState);
        TestAssert.IsTrue(restored.AcceptedWarping);
        TestAssert.AreEqual(new Double3(1.5, -2.25, 3.75), restored.AcceptedVelocityU);
        TestAssert.AreEqual(new Float3(4.5f, 5.5f, 6.5f), restored.AcceptedVelocityL);
        TestAssert.AreEqual(7, restored.LocalStarId);
    }

    [TestMethod]
    public void JoinCopyKeepsLocalStarId()
    {
        // Every other client builds its model of a joining player from this copy, and the star
        // decides name tag visibility and battle visual routing on those clients.
        var copy = (PlayerData)Sample().CreateCopyWithoutMechaData();

        TestAssert.AreEqual(7, copy.LocalStarId);
    }

    [TestMethod]
    public void UnreadableSnapshotIsReportedAsAbsent()
    {
        var current = SerializePlayer(Sample());
        for (var length = 1; length < current.Length; length++)
            TestAssert.IsNull(MetadataTransactionManager.ReadPlayer(current.Take(length).ToArray()), $"Truncated at {length}");
        TestAssert.IsNull(MetadataTransactionManager.ReadPlayer(current.Concat(new byte[] { 1 }).ToArray()));
        TestAssert.IsNull(MetadataTransactionManager.ReadPlayer(Array.Empty<byte>()));
    }

    [TestMethod]
    public void CurrentTransactionSnapshotsRoundTripWithoutReencoding()
    {
        var current = SerializePlayer(Sample());
        var transaction = new global::NebulaeModel.DataStructures.MetadataTransaction
        {
            Id = Guid.NewGuid().ToString("N"),
            BeforePlayer = current,
            AfterPlayer = current
        };
        var disk = transaction.Export();
        TestAssert.AreEqual(1, BitConverter.ToInt32(disk, 0));
        var wire = transaction.ExportWire();
        var received = global::NebulaeModel.DataStructures.MetadataTransaction.ImportWire(wire);
        TestAssert.AreEqual(transaction.Id, received.Id);
        CollectionAssert.AreEqual(current, received.BeforePlayer);
        CollectionAssert.AreEqual(current, received.AfterPlayer);
        CollectionAssert.AreEqual(disk, transaction.Export());
        CollectionAssert.AreEqual(current, global::NebulaeModel.DataStructures.MetadataTransaction.Import(disk).BeforePlayer);
    }

    [TestMethod]
    public void TransactionTransferKeepsSnapshotBytesOpaqueUntilRestoration()
    {
        var transaction = new global::NebulaeModel.DataStructures.MetadataTransaction
        {
            Id = Guid.NewGuid().ToString("N"),
            BeforePlayer = new byte[] { 1, 2 },
            AfterPlayer = new byte[] { 3 }
        };
        var received = global::NebulaeModel.DataStructures.MetadataTransaction.ImportWire(transaction.ExportWire());
        CollectionAssert.AreEqual(transaction.BeforePlayer, received.BeforePlayer);
        CollectionAssert.AreEqual(transaction.AfterPlayer, received.AfterPlayer);
        TestAssert.IsNull(MetadataTransactionManager.ReadPlayer(received.BeforePlayer));
        TestAssert.IsNull(MetadataTransactionManager.ReadPlayer(received.AfterPlayer));
    }
}
