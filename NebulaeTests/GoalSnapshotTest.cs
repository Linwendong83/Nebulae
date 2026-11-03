using NebulaeModel.DataStructures;
using NebulaeModel.Networking.Serialization;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class GoalSnapshotTest
{
    private static byte[] Snapshot()
    {
        var writer = new NetDataWriter();
        writer.Put(true); writer.Put(false); writer.Put((int)EGoalLevel.Full);
        writer.Put(1);
        writer.Put(101); writer.Put((int)EGoalStage.Enabled); writer.Put(17L); writer.Put(99L);
        writer.Put(false); writer.Put(true); writer.Put(2);
        writer.Put(1); writer.Put(101);
        writer.Put((int)EGoalLevel.Key); writer.Put(1); writer.Put(101);
        return writer.CopyData();
    }

    [TestMethod]
    public void SnapshotReadsEveryFieldIncludingDefaults()
    {
        var snapshot = GoalSnapshot.Read(Snapshot(), 1, 1);
        TestAssert.IsTrue(snapshot.SandboxMode);
        TestAssert.IsFalse(snapshot.SandboxToolsEnabled);
        TestAssert.AreEqual(EGoalLevel.Full, snapshot.Level);
        TestAssert.HasCount(1, snapshot.Goals);
        var goal = snapshot.Goals[0];
        TestAssert.AreEqual(101, goal.Id);
        TestAssert.AreEqual(EGoalStage.Enabled, goal.Stage);
        TestAssert.AreEqual(17L, goal.Current);
        TestAssert.AreEqual(99L, goal.Target);
        TestAssert.IsFalse(goal.Ignored);
        TestAssert.IsTrue(goal.Patched);
        TestAssert.AreEqual(2, goal.IgnoreLevel);
        CollectionAssert.AreEqual(new[] { 101 }, snapshot.Queue);
        TestAssert.AreEqual(EGoalLevel.Key, snapshot.DefaultLevel);
        CollectionAssert.AreEqual(new[] { 101 }, snapshot.IgnoredGoals);
    }

    [TestMethod]
    public void ExactlyOneCompleteSnapshotIsRequired()
    {
        var bytes = Snapshot();
        for (var length = 0; length < bytes.Length; length++)
            TestAssert.Throws<ParseException>(() => GoalSnapshot.Read(bytes.Take(length).ToArray(), 1, 1));
        TestAssert.Throws<ParseException>(() => GoalSnapshot.Read(bytes.Concat(new byte[] { 0 }).ToArray(), 1, 1));
        TestAssert.Throws<ParseException>(() => GoalSnapshot.Read(bytes.Concat(bytes).ToArray(), 2, 2));
    }

    [TestMethod]
    public void CountsBooleansLevelsAndStagesAreValidated()
    {
        foreach (var offset in new[] { 0, 1, 34, 35 })
        {
            var bytes = Snapshot(); bytes[offset] = 2;
            TestAssert.Throws<ParseException>(() => GoalSnapshot.Read(bytes, 1, 1));
        }
        foreach (var offset in new[] { 2, 14, 48 })
        {
            var bytes = Snapshot(); BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, offset);
            TestAssert.Throws<ParseException>(() => GoalSnapshot.Read(bytes, 1, 1));
        }
        foreach (var offset in new[] { 6, 40, 52 })
            foreach (var count in new[] { -1, 2, int.MaxValue })
            {
                var bytes = Snapshot(); BitConverter.GetBytes(count).CopyTo(bytes, offset);
                TestAssert.Throws<ParseException>(() => GoalSnapshot.Read(bytes, 1, 1));
            }
    }
}
