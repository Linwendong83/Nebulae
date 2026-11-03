using global::NebulaeModel.DataStructures;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class VegetationCollectionCompatibilityTest
{
    [TestMethod]
    public void NativeCollectionSnapshotPreservesPlayerAndOwnerVegetation()
    {
        var collection = new VegetableCollection();
        collection.AddVegeToPlayer(1101, 3);
        collection.AddVege(101, EObjectType.Entity, 7, 1102, 2);

        var copy = new VegetableCollection();
        VegetableCollectionState.Restore(copy, VegetableCollectionState.Capture(collection));

        TestAssert.AreEqual(3, copy.playerVegeDict[1101]);
        copy.TransferVegeToPlayer(101, EObjectType.Entity, 7);
        TestAssert.AreEqual(2, copy.playerVegeDict[1102]);
        TestAssert.AreEqual(2, copy.RemoveVegeFromPlayer(1102, 3));
        TestAssert.AreEqual(0, copy.RemoveVegeFromPlayer(1102, 1));
    }
}
