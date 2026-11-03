using global::NebulaeModel.DataStructures;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class FactoryLoadLifecycleTest
{
    [TestMethod]
    public void DownloadedSnapshotDoesNotPausePacketsUntilTerrainIsReadyForInstallation()
    {
        var loads = new FactoryLoadTracker();
        loads.Begin(103, true);
        TestAssert.IsTrue(loads.Receive(103, out _));
        TestAssert.IsFalse(loads.Paused);
        TestAssert.IsFalse(loads.Receive(103, out _));
        TestAssert.IsTrue(loads.TryQueue(103));
        TestAssert.IsFalse(loads.TryQueue(103));
        loads.Pause(103);
        TestAssert.IsTrue(loads.Paused);
        loads.Cancel(103);
        TestAssert.IsFalse(loads.TryQueue(103));
        TestAssert.IsFalse(loads.Paused);
        TestAssert.IsTrue(loads.Begin(103, true), "An already buffered response needs no stale-response drain after cancellation.");
    }

    [TestMethod]
    public void DuplicateAndUnsolicitedSnapshotsCannotStartAnotherImport()
    {
        var loads = new FactoryLoadTracker();
        TestAssert.IsFalse(loads.Receive(102, out _));
        TestAssert.IsTrue(loads.Begin(102, true));
        TestAssert.IsFalse(loads.Begin(102, true));
        TestAssert.IsTrue(loads.Receive(102, out var again));
        TestAssert.IsFalse(again);
        TestAssert.IsFalse(loads.Receive(102, out _));
        loads.Complete(102);
        TestAssert.IsFalse(loads.Receive(102, out _));
    }

    [TestMethod]
    public void CachedAndDownloadedFactoriesHoldIndependentPauseOwnership()
    {
        var loads = new FactoryLoadTracker();
        loads.Begin(101, false); loads.Pause(101);
        loads.Begin(102, true);
        TestAssert.IsTrue(loads.Receive(102, out _)); loads.Pause(102);
        loads.Complete(101);
        TestAssert.IsTrue(loads.Paused);
        loads.Cancel(102); // importing/modeling failure follows the same cleanup
        TestAssert.IsFalse(loads.Paused);
        TestAssert.IsTrue(loads.Begin(102, true));
    }

    [TestMethod]
    public void LeaveAndImmediatelyReturnDrainsOldResponseBeforeRequestingFreshSnapshot()
    {
        var loads = new FactoryLoadTracker();
        loads.Begin(102, true);
        loads.Cancel(102);
        TestAssert.IsFalse(loads.Begin(102, true));
        TestAssert.IsFalse(loads.Receive(102, out var again));
        TestAssert.IsTrue(again);
        TestAssert.IsTrue(loads.Receive(102, out again));
        TestAssert.IsFalse(again);
    }

    [TestMethod]
    public void ResponseAfterLeavingDoesNotPauseOrRestoreAnAbandonedPlanet()
    {
        var loads = new FactoryLoadTracker();
        loads.Begin(102, true); loads.Cancel(102);
        TestAssert.IsFalse(loads.Receive(102, out var again));
        TestAssert.IsFalse(again);
        TestAssert.IsFalse(loads.Paused);
        TestAssert.IsFalse(loads.Contains(102));
        TestAssert.IsTrue(loads.Begin(102, true));
    }

    [TestMethod]
    public void CancellingADeferredReturnDoesNotCreateAnUnwantedThirdRequest()
    {
        var loads = new FactoryLoadTracker();
        loads.Begin(102, true); loads.Cancel(102); loads.Begin(102, true); loads.Cancel(102);
        TestAssert.IsFalse(loads.Receive(102, out var again));
        TestAssert.IsFalse(again);
        loads.Clear();
        TestAssert.IsTrue(loads.Begin(102, true));
    }
}
