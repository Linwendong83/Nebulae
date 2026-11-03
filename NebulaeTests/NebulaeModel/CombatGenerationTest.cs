using global::NebulaeModel.DataStructures;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class CombatGenerationTest
{
    [TestMethod]
    public void RecycledEnemyIdRejectsOldDamageAndDeath()
    {
        var state = new CombatGenerationState();
        var old = state.Create(101, 7);
        TestAssert.AreEqual(old, state.Get(101, 7));
        var next = state.Create(101, 7);
        TestAssert.AreNotEqual(old, next);
        TestAssert.AreEqual(next, state.Get(101, 7));
    }

    [TestMethod]
    public void DelayedSnapshotCannotOverwriteNewGeneration()
    {
        var state = new CombatGenerationState();
        state.Set(101, 2, 50);
        state.Set(101, 2, 40);
        TestAssert.AreEqual(50L, state.Get(101, 2));
    }

    [TestMethod]
    public void GroundPoolsAreIsolatedButHiveEnemiesShareSpacePool()
    {
        var state = new CombatGenerationState();
        state.Set(101, 2, 10);
        state.Set(102, 2, 20);
        state.Set(1000001, 2, 30);
        TestAssert.AreEqual(10L, state.Get(101, 2));
        TestAssert.AreEqual(20L, state.Get(102, 2));
        TestAssert.AreEqual(30L, state.Get(0, 2));
        TestAssert.AreEqual(30L, state.Get(1000002, 2));
    }
}
