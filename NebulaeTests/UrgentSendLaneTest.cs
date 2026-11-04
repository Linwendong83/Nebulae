using NebulaeModel.Networking;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

/// <summary>
///     A client that is catching up on replication can hold tens of thousands of queued frames, and
///     a quitting client waits on a single small acknowledgement. Those acknowledgements therefore
///     have their own lane ahead of the backlog, while bulk frames keep their order.
/// </summary>
[TestClass]
public class UrgentSendLaneTest
{
    private readonly Queue<Action> work = new();
    private readonly Queue<Action<bool>> completions = new();
    private readonly List<byte> sent = new();

    private OrderedSendQueue Queue() =>
        new(bytes => bytes, (bytes, done) => { sent.Add(bytes[0]); completions.Enqueue(done); }, work.Enqueue);

    [TestMethod]
    public void AnAcknowledgementLeavesBeforeTheQueuedStateBacklog()
    {
        var queue = Queue();
        for (byte value = 1; value <= 5; value++) queue.Enqueue(new byte[] { value });
        queue.EnqueueUrgent(new byte[] { 99 });

        Pump();

        TestAssert.AreEqual((byte)99, sent[0], "The acknowledgement must not wait behind queued replication frames.");
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, sent.Skip(1).ToArray(),
            "The bulk frames keep their order behind it.");
        TestAssert.IsTrue(queue.Flush(1));
    }

    [TestMethod]
    public void UrgentFramesDoNotReorderEachOtherOrTheBacklog()
    {
        var queue = Queue();
        queue.Enqueue(new byte[] { 1 });
        queue.EnqueueUrgent(new byte[] { 8 });
        queue.Enqueue(new byte[] { 2 });
        queue.EnqueueUrgent(new byte[] { 9 });

        Pump();

        CollectionAssert.AreEqual(new byte[] { 8, 9, 1, 2 }, sent.ToArray());
    }

    [TestMethod]
    public void FlushWaitsForAnUrgentFrameThatIsStillQueued()
    {
        var queue = Queue();
        queue.EnqueueUrgent(new byte[] { 7 });

        TestAssert.IsFalse(queue.Flush(1), "An urgent frame is still unsent while its worker has not run.");
        Pump();
        TestAssert.IsTrue(queue.Flush(1));
        CollectionAssert.AreEqual(new byte[] { 7 }, sent.ToArray());
    }

    [TestMethod]
    public void StopDropsQueuedUrgentFrames()
    {
        var queue = Queue();
        queue.EnqueueUrgent(new byte[] { 7 });
        queue.Stop();

        Pump();

        TestAssert.IsEmpty(sent);
        TestAssert.IsFalse(queue.Flush(1));
    }

    private void Pump()
    {
        for (var guard = 0; guard < 1000; guard++)
        {
            while (work.Count > 0) work.Dequeue()();
            if (completions.Count == 0) return;
            completions.Dequeue()(true);
        }
    }
}
