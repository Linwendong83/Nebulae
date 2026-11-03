using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class CraftMotionHistoryTest
{
    [TestMethod]
    public void DelayedButCorrectFlightDoesNotJumpBackToTheSnapshotPosition()
    {
        var motion = new CraftMotionHistory();
        for (var tick = 0; tick <= 20; tick++) motion.Record(tick, new VectorLF3(tick, 200, 0));
        TestAssert.IsTrue(motion.Reconcile(14, new VectorLF3(14, 200, 0), new VectorLF3(20, 200, 0), 50));
        TestAssert.AreEqual(VectorLF3.zero, motion.Advance(21));
    }

    [TestMethod]
    public void RepeatedDelayedCheckpointsDoNotApplyTheSameErrorTwice()
    {
        var motion = new CraftMotionHistory();
        var position = new VectorLF3(2, 200, 0);
        for (var tick = 0; tick <= 30; tick++)
        {
            motion.Record(tick, position);
            if (tick >= 6) TestAssert.IsTrue(motion.Reconcile(tick - 6, new VectorLF3(tick - 6, 200, 0), position, 50));
            position += new VectorLF3(1, 0, 0) + motion.Advance(tick + 1);
            TestAssert.IsGreaterThanOrEqualTo(tick + 1 - 1e-8, position.x, "A correction already applied since the sample must be subtracted.");
        }
        TestAssert.AreEqual(31, position.x, .3);
    }

    [TestMethod]
    public void MissingHistoryAndTeleportsRequireANewAnchor()
    {
        var motion = new CraftMotionHistory();
        TestAssert.IsFalse(motion.Reconcile(5, VectorLF3.zero, VectorLF3.zero, 50));
        motion.Record(5, VectorLF3.zero);
        TestAssert.IsFalse(motion.Reconcile(5, new VectorLF3(1000, 0, 0), VectorLF3.zero, 50));
        motion.Reset();
        TestAssert.IsFalse(motion.Reconcile(5, VectorLF3.zero, VectorLF3.zero, 50));
    }
}
