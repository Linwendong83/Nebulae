using global::NebulaeModel.DataStructures;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class MetadataLedgerTest
{
    private static int[] Blue(int value) => [value, 0, 0, 0, 0, 0];

    [TestMethod]
    public void OnlineRecordIncreasesAwardFourHundredAndSkipOfflineGrowth()
    {
        var ledger = new MetadataLedger();
        ledger.Advance(Blue(1000), []);
        ledger.GetAccount("player");
        ledger.Advance(Blue(1200), ["player"]);
        ledger.Advance(Blue(2000), []);
        ledger.Advance(Blue(2000), ["player"]);
        ledger.Advance(Blue(2200), ["player"]);
        TestAssert.AreEqual(400, ledger.Accounts["player"].Earned[0]);
        TestAssert.AreEqual(2L, ledger.Accounts["player"].Sequence);
    }

    [TestMethod]
    public void MatureWorldAndProductionRecoveryNeverAwardAgain()
    {
        var ledger = new MetadataLedger();
        ledger.Advance(Blue(10000), []);
        ledger.GetAccount("late");
        foreach (var current in new[] { 1000, 5000, 0, 10000, 10000 })
            ledger.Advance(Blue(current), ["late"]);
        TestAssert.AreEqual(0, ledger.Accounts["late"].Earned[0]);
        TestAssert.AreEqual(10000, ledger.Peak[0]);
    }

    [TestMethod]
    public void EachOnlineMemberGetsWholeIncreaseButDuplicateSessionsDoNotMultiplyIt()
    {
        var ledger = new MetadataLedger();
        ledger.Advance(Blue(100), []);
        ledger.Advance(Blue(160), ["host", "a", "a", "b"]);
        foreach (var account in ledger.Accounts.Values) TestAssert.AreEqual(60, account.Earned[0]);
        TestAssert.HasCount(3, ledger.Accounts);
    }

    [TestMethod]
    public void DurableLedgerSurvivesSaveRollbackWithoutReissuingEarnings()
    {
        var ledger = new MetadataLedger();
        ledger.Advance(Blue(1000), []);
        ledger.Advance(Blue(1200), ["a"]);
        ledger = MetadataLedger.Import(ledger.Export());
        ledger.Advance(Blue(1000), []); // Old save loaded; online membership is not restored.
        ledger.Advance(Blue(1200), ["a"]);
        TestAssert.AreEqual(200, ledger.Accounts["a"].Earned[0]);
        ledger.Advance(Blue(1300), ["a"]);
        TestAssert.AreEqual(300, ledger.Accounts["a"].Earned[0]);
    }

    [TestMethod]
    public void MatrixPeaksAreIndependentAndUnacknowledgedAwardsRemainDurable()
    {
        var ledger = new MetadataLedger();
        ledger.Advance([100, 200, 0, 0, 0, 0], []);
        ledger.Advance([150, 100, 50, 0, 0, 0], ["a"]);
        var restored = MetadataLedger.Import(ledger.Export());
        CollectionAssert.AreEqual(new[] { 50, 0, 50, 0, 0, 0 }, restored.Accounts["a"].Earned);
        TestAssert.AreEqual(0L, restored.Accounts["a"].Acknowledged);
        TestAssert.AreEqual(1L, restored.Accounts["a"].Sequence);
    }
}
