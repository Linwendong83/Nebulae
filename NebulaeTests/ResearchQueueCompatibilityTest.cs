using System.Reflection;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class ResearchQueueCompatibilityTest
{
    [TestMethod]
    public void QueueCapacityChecksOccupiedSlotsWithinTheNewStorageArray()
    {
        var type = typeof(global::NebulaeNetwork.Server).Assembly.GetType(
            "NebulaeNetwork.PacketProcessors.GameHistory.GameHistoryTechQueueSyncProcessor", true)!;
        var method = type.GetMethod("ValidQueue", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Valid(int[] queue, int max) => (bool)method.Invoke(null, new object[] { queue, max })!;

        var queue = new int[32];
        for (var i = 0; i < 12; i++) queue[i] = 1000 + i;
        TestAssert.IsTrue(Valid(queue, 16));
        TestAssert.IsFalse(Valid(queue, 10));
        TestAssert.IsTrue(Valid(queue, 32));
        queue[13] = 2000;
        TestAssert.IsFalse(Valid(queue, 32));
        TestAssert.IsFalse(Valid(new int[33], 32));
    }
}
