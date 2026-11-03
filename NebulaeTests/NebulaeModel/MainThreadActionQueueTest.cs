using System.Collections.Concurrent;
using global::NebulaeModel.Utils;
using HarmonyLib;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class MainThreadActionQueueTest
{
    [TestMethod]
    public void SocketWorkersOnlyEnqueueAndActionsRunOnTheDrainingThread()
    {
        var queue = new MainThreadActionQueue();
        var threads = new ConcurrentBag<int>();
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) queue.Enqueue(() => threads.Add(Environment.CurrentManagedThreadId));
        })).ToArray();
        Task.WaitAll(workers);
        TestAssert.IsEmpty(threads);
        var mainThread = Environment.CurrentManagedThreadId;
        queue.Drain(); TestAssert.HasCount(400, threads);
        TestAssert.IsTrue(threads.All(thread => thread == mainThread));
        queue.Drain(); TestAssert.HasCount(400, threads);
    }

    [TestMethod]
    public void NetworkDispatchHasNoUnityObjectDiscoveryOrCreationCalls()
    {
        var method = AccessTools.Method(typeof(UnityDispatchQueue), nameof(UnityDispatchQueue.RunOnMainThread));
        var instructions = PatchProcessor.GetOriginalInstructions(method);
        TestAssert.IsTrue(instructions.Any(i => i.Calls(AccessTools.Method(typeof(MainThreadActionQueue), nameof(MainThreadActionQueue.Enqueue)))));
        TestAssert.IsFalse(instructions.Any(i => i.operand is System.Reflection.MethodInfo called &&
            (typeof(UnityEngine.Object).IsAssignableFrom(called.DeclaringType) || called.DeclaringType == typeof(UnityEngine.GameObject))));
    }
}
