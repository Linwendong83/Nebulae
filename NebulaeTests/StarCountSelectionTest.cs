using global::NebulaeModel.Utils;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class StarCountSelectionTest
{
    [TestMethod]
    public void OrdinaryPositionsAndFirstMoreSelection()
    {
        var selection = new StarCountSelection();
        for (var count = 32; count <= 64; count++)
        {
            TestAssert.AreEqual(count, selection.Select(count));
            TestAssert.IsFalse(selection.IsMore);
            TestAssert.IsFalse(selection.TrySubmit("1024", true, out _));
        }
        TestAssert.AreEqual(65, selection.Select(StarCountSelection.More));
        TestAssert.IsTrue(selection.IsMore);
    }

    [TestMethod]
    [DataRow("0", 65)]
    [DataRow("1", 65)]
    [DataRow("64", 65)]
    [DataRow("65", 65)]
    [DataRow("256", 256)]
    [DataRow("1024", 1024)]
    [DataRow("1025", 1025)]
    [DataRow("2048", 2048)]
    [DataRow("4096", 4096)]
    [DataRow("8191", 8191)]
    [DataRow("8192", 8191)]
    [DataRow("9999", 8191)]
    [DataRow("10000", 8191)]
    public void MoreClampsSubmittedCountToInclusiveRange(string text, int count)
    {
        var selection = new StarCountSelection();
        selection.Select(StarCountSelection.More);
        TestAssert.IsTrue(selection.TrySubmit(text, true, out var submitted));
        TestAssert.AreEqual(count, submitted);
        TestAssert.AreEqual(count, selection.CustomCount);
        // Displaying the regenerated galaxy must keep the More position.
        selection.Display(submitted);
        TestAssert.IsTrue(selection.IsMore);
        TestAssert.AreEqual(count, selection.Select(StarCountSelection.More));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("abc")]
    [DataRow("-65")]
    [DataRow("65.5")]
    [DataRow("2147483648")]
    [DataRow(" 65 ")]
    public void InvalidInputPreservesLastConfirmedCount(string text)
    {
        var selection = new StarCountSelection();
        selection.Display(256);
        TestAssert.IsFalse(selection.TrySubmit(text, true, out var count));
        TestAssert.AreEqual(256, count);
        TestAssert.AreEqual(256, selection.CustomCount);
    }

    [TestMethod]
    public void MovingLeftIgnoresPendingInputButRemembersConfirmedValue()
    {
        var selection = new StarCountSelection();
        selection.Display(256);
        TestAssert.AreEqual(64, selection.Select(64));
        TestAssert.IsFalse(selection.TrySubmit("1024", true, out _), "Late end-edit must not overwrite the ordinary selection.");
        selection.Display(64);
        TestAssert.AreEqual(256, selection.CustomCount);
        TestAssert.AreEqual(256, selection.Select(StarCountSelection.More));
        TestAssert.IsTrue(selection.TrySubmit("1024", true, out _));
        TestAssert.AreEqual(32, selection.Select(32));
        TestAssert.AreEqual(1024, selection.Select(StarCountSelection.More));
    }

    [TestMethod]
    public void ClientDisplayCannotSubmitAndReopeningResetsCustomCount()
    {
        var selection = new StarCountSelection();
        selection.Display(1024);
        TestAssert.IsTrue(selection.IsMore);
        TestAssert.AreEqual(1024, selection.CustomCount);
        TestAssert.IsFalse(selection.TrySubmit("65", false, out _));
        TestAssert.AreEqual(1024, selection.CustomCount);
        selection.Reset();
        TestAssert.IsFalse(selection.IsMore);
        TestAssert.AreEqual(65, selection.Select(StarCountSelection.More));
    }

    [TestMethod]
    public void MoreLabelUsesChineseCatalogue()
    {
        TestAssert.IsTrue(NebulaeLocalization.TryTranslate("More", true, out var text));
        TestAssert.AreEqual("更多", text);
    }
}
