using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class StatisticalSnapshotTest
{
    private static StatisticalSnapShot Snapshot(long[] energyStored)
    {
        var snapshot = new StatisticalSnapShot(1000L, energyStored.Length);
        Array.Copy(energyStored, snapshot.EnergyStored, energyStored.Length);
        return snapshot;
    }

    [TestMethod]
    [DataRow(500L, true, DisplayName = "AnIdleTickIsInformationFree")]
    [DataRow(400L, false, DisplayName = "AChangedAbsoluteIsNews")]
    public void StoredEnergyIsComparedWithTheLastSnapshot(long storedEnergy, bool informationFree)
    {
        TestAssert.AreEqual(informationFree,
            Snapshot([storedEnergy, 0]).IsInformationFree([500, 0]));
    }

    [TestMethod]
    public void ANonZeroAbsoluteBeforeTheFirstKeptSnapshotIsNews()
    {
        // With nothing sent yet there is no previous value, so a non-zero reading cannot be assumed
        // to be already known.
        TestAssert.IsFalse(Snapshot(new long[] { 500, 0 }).IsInformationFree(null));
        TestAssert.IsTrue(Snapshot(new long[] { 0, 0 }).IsInformationFree(null));
    }

    [TestMethod]
    public void APowerRegisterIsNewsEvenWhenNothingWasStored()
    {
        var snapshot = Snapshot(new long[] { 0, 0 });
        snapshot.PowerGenerationRegister[1] = 42;
        TestAssert.IsFalse(snapshot.IsInformationFree(new long[] { 0, 0 }));
    }

    [TestMethod]
    public void AHashRegisterIsNews()
    {
        var snapshot = Snapshot(new long[] { 0, 0 });
        snapshot.HashRegister[0] = 7;
        TestAssert.IsFalse(snapshot.IsInformationFree(new long[] { 0, 0 }));
    }

    [TestMethod]
    public void AProductionChangeIsNews()
    {
        var snapshot = Snapshot(new long[] { 0, 0 });
        snapshot.ProductionChangesPerFactory[0].Add(
            new StatisticalSnapShot.ProductionChangeStruct(true, 3, 10));
        TestAssert.IsFalse(snapshot.IsInformationFree(new long[] { 0, 0 }));
    }

    [TestMethod]
    public void ARoundTripKeepsTheAbsoluteSoADroppedTickLeavesTheReceiverCorrect()
    {
        // The receiver keeps the last EnergyStored array it was sent. Dropping an information-free
        // tick must therefore leave it holding the same values the host would have sent.
        var snapshot = Snapshot(new long[] { 900, 12 });
        var writer = new BinaryUtils.Writer();
        snapshot.Export(writer.BinaryWriter);
        var restored = new StatisticalSnapShot(new BinaryUtils.Reader(writer.CloseAndGetBytes()).BinaryReader);

        TestAssert.IsTrue(restored.IsInformationFree(new long[] { 900, 12 }));
        TestAssert.IsFalse(restored.IsInformationFree(new long[] { 900, 13 }));
    }
}
