using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// the host command inbox is bounded, at-most-once and only executes at the frame boundary.
/// </summary>
[TestClass]
public class HostCommandQueueTest
{
    private static readonly AuthorityEpoch Epoch = new(0x1122334455667788, 0x99AABBCCDDEEFF00);
    private static readonly ConnectionEpoch Connection = new(7);

    private sealed class RecordingExecutor : IHostCommandExecutor
    {
        public readonly List<CommandKey> Executed = [];
        public Func<QueuedHostCommand, CommandOutcome> Responder = _ =>
            new CommandOutcome(CommandResultCode.Applied, appliedHostTick: 500, transactionId: 1);

        public CommandOutcome Execute(in QueuedHostCommand command)
        {
            Executed.Add(command.Key);
            return Responder(command);
        }
    }

    private static AuthorityCommandPacket Command(long sequence, int payloadBytes = 0,
        ConnectionEpoch? connection = null) =>
        AuthorityCommandPacket.Create(
            new AuthorityEnvelopeHeader(AuthorityFamily.Command, Epoch,
                connection ?? Connection, sequence, hostTick: 100, claimedPlayerId: 3, payloadLength: payloadBytes),
            ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1), category: 1, payload: new byte[payloadBytes]);

    private static CommandKey Key(long sequence, ConnectionEpoch? connection = null) =>
        new(Epoch, connection ?? Connection, sequence);

    [TestMethod]
    public void AQueuedCommandExecutesExactlyOnceAtTheFrameBoundary()
    {
        var queue = new HostCommandQueue(Epoch);
        var executor = new RecordingExecutor();
        TestAssert.IsTrue(queue.TryEnqueue(Key(1), Command(1), 3, 1, 100));
        TestAssert.AreEqual(1, queue.Count);
        TestAssert.IsEmpty(executor.Executed, "Enqueue must not execute; only the frame boundary may.");

        var drained = queue.Drain(executor, null);
        TestAssert.AreEqual(1, drained);
        TestAssert.HasCount(1, executor.Executed);
        TestAssert.AreEqual(0, queue.Count);
    }

    [TestMethod]
    public void DrainingTwiceDoesNotCommitTwice()
    {
        // The frame patch must be safe if a frame notification is duplicated. The second drain has
        // nothing to do, so it must run no command and report no work.
        var queue = new HostCommandQueue(Epoch);
        var executor = new RecordingExecutor();
        queue.TryEnqueue(Key(1), Command(1), 3, 1, 100);

        TestAssert.AreEqual(1, queue.Drain(executor, null));
        TestAssert.AreEqual(0, queue.Drain(executor, null));
        TestAssert.HasCount(1, executor.Executed);
    }

    [TestMethod]
    public void ARetriedCommandIsAnsweredFromCacheAndNeverReExecuted()
    {
        var queue = new HostCommandQueue(Epoch);
        var executor = new RecordingExecutor();
        queue.TryEnqueue(Key(1), Command(1), 3, 1, 100);
        queue.Drain(executor, null);

        // The client did not see the reply and resends the same key.
        queue.TryEnqueue(Key(1), Command(1), 3, 1, 120);
        var dispositions = new List<CommandDrainDisposition>();
        queue.Drain(executor, (_, disposition, _) => dispositions.Add(disposition));

        TestAssert.HasCount(1, executor.Executed, "A retry must never run the command a second time.");
        TestAssert.HasCount(1, dispositions);
        TestAssert.AreEqual(CommandDrainDisposition.ReplayedDuplicate, dispositions[0]);
        TestAssert.AreEqual(1L, queue.ReplayedTotal);
    }

    [TestMethod]
    public void AnEvictedRetryIsRefusedRatherThanExecutedAgain()
    {
        // A small result window forces eviction, which is where "window below the high-water mark"
        // has to be refused explicitly instead of looking like a brand new command.
        var queue = new HostCommandQueue(Epoch, capacity: 64, resultCapacity: 2);
        var executor = new RecordingExecutor();
        for (long sequence = 1; sequence <= 4; sequence++)
        {
            queue.TryEnqueue(Key(sequence), Command(sequence), 3, 1, 100);
        }
        queue.Drain(executor, null);
        TestAssert.HasCount(4, executor.Executed);

        // Sequence 1's cached answer has been evicted, but its number is below the high-water mark.
        queue.TryEnqueue(Key(1), Command(1), 3, 1, 200);
        var dispositions = new List<CommandDrainDisposition>();
        queue.Drain(executor, (_, disposition, _) => dispositions.Add(disposition));

        TestAssert.HasCount(4, executor.Executed, "An evicted retry must not be executed as a new command.");
        TestAssert.HasCount(1, dispositions);
        TestAssert.AreEqual(CommandDrainDisposition.RefusedTooOld, dispositions[0]);
    }

    [TestMethod]
    public void ACommandFromAnotherConnectionEpochIsRefused()
    {
        // A reconnect moves the dedup space. A command that still names the old connection must be
        // refused rather than replayed into the new one's window.
        var queue = new HostCommandQueue(Epoch);
        var executor = new RecordingExecutor();
        queue.TryEnqueue(Key(1, new ConnectionEpoch(9)), Command(1, connection: new ConnectionEpoch(9)), 3, 1, 100);

        var dispositions = new List<CommandDrainDisposition>();
        queue.Drain(executor, (_, disposition, _) => dispositions.Add(disposition));

        // The window is created per connection epoch, so this is a legitimate first command for
        // connection 9 rather than a replay; it must execute exactly once.
        TestAssert.HasCount(1, executor.Executed);
        TestAssert.AreEqual(CommandDrainDisposition.Executed, dispositions[0]);
    }

    [TestMethod]
    public void AFullQueueRefusesRatherThanGrowing()
    {
        var queue = new HostCommandQueue(Epoch, capacity: 4);
        var executor = new RecordingExecutor();
        for (long sequence = 1; sequence <= 4; sequence++)
        {
            TestAssert.IsTrue(queue.TryEnqueue(Key(sequence), Command(sequence), 3, 1, 100));
        }

        TestAssert.IsFalse(queue.TryEnqueue(Key(5), Command(5), 3, 1, 100),
            "A full queue must apply back-pressure, never grow past its ceiling.");
        TestAssert.AreEqual(4, queue.Count);
        TestAssert.AreEqual(1L, queue.RefusedTotal);

        // Draining frees the budget, so the next command is accepted.
        queue.Drain(executor, null);
        TestAssert.IsTrue(queue.TryEnqueue(Key(5), Command(5), 3, 1, 100));
    }

    [TestMethod]
    public void APayloadCeilingRefusesEvenWhenTheCountIsLow()
    {
        var queue = new HostCommandQueue(Epoch, capacity: 1024, byteCapacity: 64);
        TestAssert.IsTrue(queue.TryEnqueue(Key(1), Command(1, payloadBytes: 64), 3, 1, 100));
        TestAssert.IsFalse(queue.TryEnqueue(Key(2), Command(2, payloadBytes: 1), 3, 1, 100),
            "The byte budget must refuse independently of the command count.");
        TestAssert.AreEqual(1L, queue.RefusedTotal);
    }

    [TestMethod]
    public void AFailedExecutorClosesTheCommandInsteadOfRetryingIt()
    {
        // A command that throws may already have produced effects, so it is closed as rejected and
        // must not run again on a later drain.
        var queue = new HostCommandQueue(Epoch);
        var executor = new RecordingExecutor { Responder = _ => throw new InvalidOperationException("boom") };
        queue.TryEnqueue(Key(1), Command(1), 3, 1, 100);

        var dispositions = new List<CommandDrainDisposition>();
        queue.Drain(executor, (_, disposition, _) => dispositions.Add(disposition));
        TestAssert.AreEqual(CommandDrainDisposition.Failed, dispositions[0]);

        queue.TryEnqueue(Key(1), Command(1), 3, 1, 140);
        dispositions.Clear();
        queue.Drain(executor, (_, disposition, _) => dispositions.Add(disposition));
        TestAssert.HasCount(1, executor.Executed, "A failed command must be closed, not retried.");
        TestAssert.AreEqual(CommandDrainDisposition.ReplayedDuplicate, dispositions[0]);
    }

    [TestMethod]
    public void AnAdmittedCommandWithoutAnExecutorIsRejectedNotSilentlyIgnored()
    {
        // This queue has no executor, so a drained command must be answered with an explicit
        // "not ready" rather than disappearing.
        var queue = new HostCommandQueue(Epoch);
        queue.TryEnqueue(Key(1), Command(1), 3, 1, 100);

        var outcomes = new List<CommandOutcome>();
        queue.Drain(null, (_, _, outcome) => outcomes.Add(outcome));
        TestAssert.HasCount(1, outcomes);
        TestAssert.AreEqual(CommandResultCode.RejectedNotReady, outcomes[0].Code);
    }

    [TestMethod]
    public void ACommandForAnotherEpochIsRefusedAtTheDoor()
    {
        var queue = new HostCommandQueue(Epoch);
        var foreign = new CommandKey(new AuthorityEpoch(1, 1), Connection, 1);
        TestAssert.IsFalse(queue.TryEnqueue(foreign, Command(1), 3, 1, 100));
        TestAssert.AreEqual(0, queue.Count);
    }

    [TestMethod]
    public void ForgettingAConnectionDropsItsWindow()
    {
        var queue = new HostCommandQueue(Epoch);
        var executor = new RecordingExecutor();
        queue.TryEnqueue(Key(1), Command(1), 3, 1, 100);
        queue.Drain(executor, null);
        TestAssert.AreEqual(1, queue.ConnectionCount);

        TestAssert.IsTrue(queue.ForgetConnection(Connection));
        TestAssert.AreEqual(0, queue.ConnectionCount);
        TestAssert.IsFalse(queue.TryGetCachedOutcome(Key(1), out _));
    }

    [TestMethod]
    public void DrainIsNotReentrant()
    {
        // A nested drain would commit the same batch twice, so it is a hard error rather than a
        // silent recursion.
        var queue = new HostCommandQueue(Epoch);
        queue.TryEnqueue(Key(1), Command(1), 3, 1, 100);

        var reentrant = new ReentrantExecutor(queue);
        queue.Drain(reentrant, null);
        TestAssert.IsNotNull(reentrant.Caught, "A nested Drain must throw.");
        TestAssert.IsInstanceOfType<InvalidOperationException>(reentrant.Caught);
    }

    private sealed class ReentrantExecutor : IHostCommandExecutor
    {
        private readonly HostCommandQueue queue;

        public ReentrantExecutor(HostCommandQueue queue)
        {
            this.queue = queue;
        }

        public Exception? Caught { get; private set; }

        public CommandOutcome Execute(in QueuedHostCommand command)
        {
            try
            {
                queue.Drain(null, null);
            }
            catch (Exception e)
            {
                Caught = e;
            }
            return new CommandOutcome(CommandResultCode.Applied);
        }
    }

    [TestMethod]
    public void TheSocketThreadCanEnqueueWhileTheFrameThreadDrains()
    {
        // The receive path and the frame path are different threads. Enqueue must be safe while a
        // drain is in progress, and no command may be lost or executed twice.
        var queue = new HostCommandQueue(Epoch, capacity: 4096);
        var executor = new RecordingExecutor();
        const int perProducer = 200;
        var producers = 3;
        var done = 0;

        var threads = new List<Thread>();
        for (var producer = 0; producer < producers; producer++)
        {
            var connection = new ConnectionEpoch((ulong)(100 + producer));
            var thread = new Thread(() =>
            {
                for (long sequence = 1; sequence <= perProducer; sequence++)
                {
                    queue.TryEnqueue(Key(sequence, connection), Command(sequence, connection: connection), 3,
                        producer, 100);
                }
                Interlocked.Increment(ref done);
            });
            threads.Add(thread);
            thread.Start();
        }

        foreach (var thread in threads) thread.Join();
        TestAssert.AreEqual(producers, done);
        queue.Drain(executor, null);
        queue.Drain(executor, null);

        TestAssert.HasCount(producers * perProducer, executor.Executed,
            "Every distinct command must execute exactly once across producers.");
        var distinct = new HashSet<CommandKey>(executor.Executed);
        TestAssert.HasCount(executor.Executed.Count, distinct, "A command must not be executed twice.");
    }

    [TestMethod]
    public void LoadingAnotherWorldDiscardsUnconfirmedCommands()
    {
        using var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        var connection = session.AssignConnectionEpoch(3);
        TestAssert.IsTrue(session.TryEnqueueHostCommand(Key(1, connection), Command(1, connection: connection),
            3, connectionId: 1, enqueuedTick: 100));
        TestAssert.AreEqual(1, session.Commands.Count);
        session.BeginAuthorityWorld(new AuthorityEpoch(11, 12), isHost: true);
        var executor = new RecordingExecutor();
        session.HostExecutor = executor;
        session.OnFrameBoundary(200);
        TestAssert.IsEmpty(executor.Executed);
        TestAssert.AreEqual(0, session.Commands.Count);
    }

}
