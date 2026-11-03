using NebulaeModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class EnemyCheckpointCadenceTest
{
    private static ObjectKey Key(int id, long generation = 1) => ObjectKey.Create(new AuthorityEpoch(1, 2), PoolKind.GroundEnemy, 101, id, generation);

    [TestMethod]
    public void ActiveUnitSamplesAreDistributedAndDoNotSpeedUpStationaryMembers()
    {
        var index = new ReplicationCandidateIndex(); var identity = new object();
        var unit = Key(1); var building = Key(2);
        index.Rebuild(identity, 0, [unit, building]); index.SetCadence(unit, false, true);
        var changed = new List<ObjectKey>(); var removed = new List<ObjectKey>();
        var unitSamples = new List<long>(); var buildingSamples = 0;
        for (var tick = 0; tick < 120; tick++)
        {
            changed.Clear(); removed.Clear();
            TestAssert.IsTrue(index.Read(identity, 2, tick, changed, removed));
            TestAssert.AreEqual(changed.Count, changed.Distinct().Count());
            if (changed.Contains(unit)) unitSamples.Add(tick);
            if (changed.Contains(building)) buildingSamples++;
            foreach (var key in changed) index.Sampled(key);
        }
        TestAssert.HasCount(20, unitSamples);
        TestAssert.IsTrue(unitSamples.Zip(unitSamples.Skip(1), (a, b) => b - a).All(delta => delta == 6));
        TestAssert.AreEqual(1, buildingSamples);
    }

    [TestMethod]
    public void DirtyChangesStayImmediateAndRecycledSlotsLoseTheirOldSchedule()
    {
        var index = new ReplicationCandidateIndex(); var identity = new object();
        var old = Key(1); var replacement = Key(1, 2);
        index.Rebuild(identity, 0, [old]); index.SetCadence(old, false, true);
        index.Changed(1);
        var changed = new List<ObjectKey>(); var removed = new List<ObjectKey>();
        TestAssert.IsTrue(index.Read(identity, 1, 0, changed, removed));
        CollectionAssert.AreEqual(new[] { old }, changed);
        index.Sampled(old); index.Remove(old); index.Add(replacement); index.SetCadence(replacement, false, true);
        changed.Clear(); removed.Clear();
        TestAssert.IsTrue(index.Read(identity, 1, 1, changed, removed));
        CollectionAssert.AreEqual(new[] { replacement }, changed);
        CollectionAssert.AreEqual(new[] { old }, removed);
        index.Sampled(replacement);
        changed.Clear(); removed.Clear();
        TestAssert.IsTrue(index.Read(identity, 1, 7, changed, removed));
        CollectionAssert.AreEqual(new[] { replacement }, changed);
        index.Rebuild(identity, 8, [replacement]);
        changed.Clear(); removed.Clear();
        TestAssert.IsTrue(index.Read(identity, 1, 13, changed, removed));
        TestAssert.IsEmpty(changed, "A rebuilt index must not retain stale frequent schedules.");
    }
}
