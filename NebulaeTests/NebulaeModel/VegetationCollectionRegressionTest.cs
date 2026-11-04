using global::NebulaeModel.DataStructures;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class VegetationCollectionRegressionTest
{
    [TestMethod]
    public void OperationDeduplicationIsPerPlayerAndResetsOnReconnect()
    {
        var manager = new NebulaeWorld.Player.VegetationManager();
        TestAssert.IsTrue(manager.AcceptCommand(2, 1));
        TestAssert.IsFalse(manager.AcceptCommand(2, 1));
        TestAssert.IsTrue(manager.AcceptCommand(3, 1));
        TestAssert.IsTrue(manager.AcceptCommand(2, 3));
        TestAssert.IsFalse(manager.AcceptCommand(2, 2));
        manager.ForgetRemote(2);
        TestAssert.IsTrue(manager.AcceptCommand(2, 1));
    }


    [TestMethod]
    public void ASnapshotThatOnlyAddsWhatTheHostMissedIsAdoptedInsteadOfRolledBack()
    {
        var known = new VegetableCollection(); known.AddVegeToPlayer(1101, 3);
        var missed = new VegetableCollection(); missed.AddVegeToPlayer(1101, 5);
        TestAssert.IsTrue(VegetableCollectionState.OnlyAdds(
            VegetableCollectionState.Capture(known), VegetableCollectionState.Capture(missed), out var difference));
        TestAssert.Contains("2x1101", difference);

        // A snapshot that would drop an item is not an addition: the host keeps its own record.
        var dropped = new VegetableCollection(); dropped.AddVegeToPlayer(1101, 2);
        TestAssert.IsFalse(VegetableCollectionState.OnlyAdds(
            VegetableCollectionState.Capture(known), VegetableCollectionState.Capture(dropped), out _));

        // Equivalent snapshots carry nothing to adopt.
        TestAssert.IsFalse(VegetableCollectionState.OnlyAdds(
            VegetableCollectionState.Capture(known), VegetableCollectionState.Capture(known), out _));

        // Unreadable payloads are refused rather than trusted.
        TestAssert.IsFalse(VegetableCollectionState.OnlyAdds(
            VegetableCollectionState.Capture(known), new byte[] { 9, 9, 9 }, out _));
    }

    [TestMethod]
    public void NestedReplayRestoresTheEnclosingCollection()
    {
        var manager = new NebulaeWorld.Player.VegetationManager();
        var outer = new VegetableCollection(); outer.AddVegeToPlayer(1101, 3);
        manager.BeginReplay(VegetableCollectionState.Capture(outer));
        var enclosing = manager.ReplayCollection;
        var inner = new VegetableCollection(); inner.AddVegeToPlayer(1101, 7);
        manager.BeginReplay(VegetableCollectionState.Capture(inner));
        TestAssert.AreEqual(7, manager.ReplayCollection.playerVegeDict[1101]);
        manager.EndReplay(); TestAssert.AreSame(enclosing, manager.ReplayCollection);
        manager.EndReplay(); TestAssert.IsNull(manager.ReplayCollection);
    }
    [TestMethod]
    public void NativeOwnerListsMayReverseOnImportWithoutChangingTheirContents()
    {
        var source = new VegetableCollection();
        source.AddVegeToPlayer(1101, 3);
        source.AddVege(101, EObjectType.Entity, 7, 1101, 2);
        source.AddVege(101, EObjectType.Entity, 7, 1102, 4);
        var bytes = VegetableCollectionState.Capture(source);
        var restored = new VegetableCollection(); VegetableCollectionState.Restore(restored, bytes);
        var roundTrip = VegetableCollectionState.Capture(restored);
        TestAssert.IsFalse(bytes.SequenceEqual(roundTrip), "Exercise the native linked-list reversal.");
        TestAssert.IsTrue(VegetableCollectionState.Equivalent(bytes, roundTrip));
        restored.AddVegeToPlayer(1101, 1);
        TestAssert.IsFalse(VegetableCollectionState.Equivalent(bytes, VegetableCollectionState.Capture(restored)));
    }

    [TestMethod]
    public void EmptyOwnerSlotsAndDictionaryOrderAreNotDesyncs()
    {
        var a = new VegetableCollection(); var b = new VegetableCollection();
        a.AddVegeToPlayer(1101, 2); a.AddVegeToPlayer(1102, 3);
        b.AddVegeToPlayer(1102, 3); b.AddVegeToPlayer(1101, 2);
        a.AddVege(101, EObjectType.Entity, 7, 1103, 4); a.TransferVegeToPlayer(101, EObjectType.Entity, 7);
        b.AddVegeToPlayer(1103, 4);
        TestAssert.IsTrue(VegetableCollectionState.Equivalent(VegetableCollectionState.Capture(a), VegetableCollectionState.Capture(b)));
        TestAssert.IsFalse(VegetableCollectionState.Equivalent(new byte[] { 0 }, Array.Empty<byte>()));
    }

    [TestMethod]
    public void DelayedAcknowledgementsPreserveLaterFoundationAndPlantingPredictions()
    {
        var collection = new VegetableCollection(); collection.AddVegeToPlayer(1101, 5);
        var journal = new VegetationCollectionJournal();
        journal.ObserveBefore(VegetableCollectionState.Capture(collection));
        collection.AddVegeToPlayer(1101, 2); journal.ObserveAfter(VegetableCollectionState.Capture(collection));
        var first = journal.Sequence;
        // A planting operation follows the first request while its receipt is in flight.
        collection.RemoveVegeFromPlayer(1101, 1); journal.ObserveAfter(VegetableCollectionState.Capture(collection));
        var second = journal.Sequence;
        var canonical = new VegetableCollection(); canonical.AddVegeToPlayer(1101, 6); // Server collected one, not two.
        TestAssert.IsTrue(journal.TryReconcile(VegetableCollectionState.Capture(canonical), first, 1, out var merged));
        var result = new VegetableCollection(); VegetableCollectionState.Restore(result, merged);
        TestAssert.AreEqual(5, result.playerVegeDict[1101]);
        TestAssert.IsFalse(journal.TryReconcile(VegetableCollectionState.Capture(canonical), first, 1, out _));
        canonical.RemoveVegeFromPlayer(1101, 1);
        TestAssert.IsTrue(journal.TryReconcile(VegetableCollectionState.Capture(canonical), second, 2, out merged));
        TestAssert.IsTrue(VegetableCollectionState.Equivalent(VegetableCollectionState.Capture(canonical), merged));
        TestAssert.IsFalse(journal.TryReconcile(merged, second + 1, 3, out _));
        TestAssert.IsFalse(journal.TryReconcile(merged, first, 3, out _));
    }
}
