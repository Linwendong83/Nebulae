using global::NebulaeModel.DataStructures;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class MetadataPaymentTest
{
    [TestMethod]
    public void RetryAfterDebitUsesSameAbsoluteConsumption()
    {
        int[] before = [100, 20, 0, 0, 0, 0];
        int[] cost = [30, 6, 0, 0, 0, 0];
        var after = MetadataPayment.Prepare(before, cost, [1000, 1000, 0, 0, 0, 0]);
        CollectionAssert.AreEqual(new[] { 130, 26, 0, 0, 0, 0 }, after);
        CollectionAssert.AreEqual(after, MetadataPayment.RecoverDebit(before, before, after));
        CollectionAssert.AreEqual(after, MetadataPayment.RecoverDebit(after, before, after));
    }

    [TestMethod]
    public void OneInsufficientMatrixRejectsWholePaymentBeforeMutation()
    {
        int[] before = [10, 20, 0, 0, 0, 0];
        TestAssert.Throws<InvalidOperationException>(() => MetadataPayment.Prepare(before, [30, 6, 0, 0, 0, 0], [100, 5, 0, 0, 0, 0]));
        CollectionAssert.AreEqual(new[] { 10, 20, 0, 0, 0, 0 }, before);
    }

    [TestMethod]
    public void ConflictingWalletAndVanillaSaturationFailWithoutSilentlyLosingPayment()
    {
        TestAssert.Throws<System.IO.InvalidDataException>(() => MetadataPayment.RecoverDebit(
            [50, 0, 0, 0, 0, 0], [10, 0, 0, 0, 0, 0], [30, 0, 0, 0, 0, 0]));
        TestAssert.Throws<InvalidOperationException>(() => MetadataPayment.Prepare(
            [1999999999, 0, 0, 0, 0, 0], [2, 0, 0, 0, 0, 0], [100, 0, 0, 0, 0, 0]));
    }

    [TestMethod]
    public void TransactionReceiptPreservesIdentityPriceAndRecoveryImages()
    {
        var tx = new MetadataTransaction
        {
            Id = Guid.NewGuid().ToString("N"),
            Owner = "owner",
            Operation = MetadataOperation.Respawn,
            State = MetadataTransactionState.Committed,
            Target = 3,
            Sequence = 42,
            Cost = [6, 0, 0, 0, 0, 0],
            BeforePlayer = [1, 2],
            AfterPlayer = [3, 4]
        };
        var copy = MetadataTransaction.Import(tx.Export());
        TestAssert.AreEqual(tx.Id, copy.Id);
        TestAssert.AreEqual(tx.State, copy.State);
        TestAssert.AreEqual(42L, copy.Sequence);
        CollectionAssert.AreEqual(tx.Cost, copy.Cost);
        CollectionAssert.AreEqual(tx.AfterPlayer, copy.AfterPlayer);
    }
}
