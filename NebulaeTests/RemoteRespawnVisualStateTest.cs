using global::NebulaeModel.DataStructures;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class RemoteRespawnVisualStateTest
{
    [TestMethod]
    public void VisualTicksDoNotModifyAuthoritativeLifeData()
    {
        var visual = new RemoteRespawnVisualState();
        var previous = new PlayerLifeData { IsAlive = false, DeathCount = 1 };
        var sharedWithHost = new PlayerLifeData
        { IsAlive = false, DeathCount = 1, RespawnMode = 2, RespawnTick = 40 };

        visual.Observe(previous);
        TestAssert.IsTrue(visual.Observe(sharedWithHost));
        TestAssert.AreEqual(40, visual.Advance());
        TestAssert.AreEqual(41, visual.Advance());
        TestAssert.AreEqual(40, sharedWithHost.RespawnTick);

        var next = new PlayerLifeData
        { IsAlive = false, DeathCount = 1, RespawnMode = 2, RespawnTick = 41 };
        TestAssert.IsFalse(visual.Observe(next));
        TestAssert.AreEqual(42, visual.Advance());
        TestAssert.AreEqual(41, next.RespawnTick);
    }

    [TestMethod]
    public void OnlyADeadPlayersNewRespawnCycleStartsTheVisual()
    {
        var visual = new RemoteRespawnVisualState();
        var alive = new PlayerLifeData { IsAlive = true, DeathCount = 1 };
        var first = new PlayerLifeData
        { IsAlive = false, DeathCount = 1, RespawnMode = 2, RespawnTick = 7 };
        visual.Observe(alive);
        TestAssert.IsTrue(visual.Observe(first));

        var finished = new PlayerLifeData
        { IsAlive = true, DeathCount = 1, RespawnMode = 2, RespawnTick = 70 };
        TestAssert.IsFalse(visual.Observe(finished));
        TestAssert.AreEqual(0, visual.Tick);

        // A missed alive packet must not suppress a later death with a new count.
        var second = new PlayerLifeData
        { IsAlive = false, DeathCount = 2, RespawnMode = 2, RespawnTick = 3 };
        visual.Observe(first);
        TestAssert.IsTrue(visual.Observe(second));
        TestAssert.AreEqual(3, visual.Tick);
    }

    [TestMethod]
    public void MutatingTheHostsLifeObjectDoesNotEraseThePreviousVisualState()
    {
        var visual = new RemoteRespawnVisualState();
        var life = new PlayerLifeData { IsAlive = false, DeathCount = 1 };
        visual.Observe(life);
        life.RespawnMode = 2;
        TestAssert.IsTrue(visual.Observe(life));
        life.IsAlive = true;
        TestAssert.IsFalse(visual.IsAlive);
        visual.Observe(life);
        TestAssert.IsTrue(visual.IsAlive);
        TestAssert.AreEqual(1, visual.DeathCount);
        TestAssert.AreEqual(0, visual.Tick);
    }
}
