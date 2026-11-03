using NebulaeModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// the host-side command dedup window.
/// </summary>
[TestClass]
public class AuthorityCommandWindowTest
{
    private static readonly AuthorityEpoch Epoch = new(0x1111222233334444, 0x5555666677778888);
    private static readonly ConnectionEpoch Connection = new(7);

    private static CommandKey Key(long sequence, ConnectionEpoch? connection = null,
        AuthorityEpoch? epoch = null) =>
        new(epoch ?? Epoch, connection ?? Connection, sequence);

    private static CommandWindow NewWindow(int capacity = 1024) => new(Epoch, Connection, capacity);

    [TestMethod]
    public void AFirstCommandIsAdmittedForExecution()
    {
        var window = NewWindow();
        TestAssert.AreEqual(CommandAdmission.Execute, window.Admit(Key(1), out _));
        TestAssert.AreEqual(1, window.PendingCount);
    }

    [TestMethod]
    public void ARepeatedCommandIsNeverExecutedTwice()
    {
        var window = NewWindow();
        var key = Key(4);
        var executions = 0;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var admission = window.Admit(key, out var cached);
            if (admission == CommandAdmission.Execute)
            {
                executions++;
                window.Complete(key, new CommandOutcome(CommandResultCode.Applied, appliedHostTick: 100));
            }
            else
            {
                // A retry must be answered from the cache, so a client that lost the reply sees the
                // same outcome as one that did not.
                TestAssert.AreEqual(CommandAdmission.Duplicate, admission);
                TestAssert.AreEqual(CommandResultCode.Applied, cached.Code);
                TestAssert.AreEqual(100L, cached.AppliedHostTick);
            }
        }

        TestAssert.AreEqual(1, executions, "A command key must commit at most once.");
    }

    [TestMethod]
    public void ACommandStillInFlightIsNotStartedASecondTime()
    {
        var window = NewWindow();
        var key = Key(9);
        TestAssert.AreEqual(CommandAdmission.Execute, window.Admit(key, out _));
        TestAssert.AreEqual(CommandAdmission.Pending, window.Admit(key, out _),
            "A retry that arrives while the command is still being applied must not start a second apply.");
        TestAssert.AreEqual(1, window.PendingCount);
    }

    [TestMethod]
    public void ARejectedCommandCachesItsRejection()
    {
        var window = NewWindow();
        var key = Key(3);
        window.Admit(key, out _);
        window.Complete(key, new CommandOutcome(CommandResultCode.RejectedResource));

        TestAssert.AreEqual(CommandAdmission.Duplicate, window.Admit(key, out var cached));
        TestAssert.IsFalse(cached.Accepted);
        TestAssert.AreEqual(CommandResultCode.RejectedResource, cached.Code);
    }

    [TestMethod]
    public void EvictedCommandsAreRejectedRatherThanReExecuted()
    {
        // The window is deliberately tiny so eviction happens immediately. A window that forgets a
        // sequence and then treats its retry as new would execute the command twice, which is the
        // failure mode the deduplication policy must reject.
        var window = NewWindow(capacity: 2);
        var executions = new Dictionary<long, int>();

        for (var sequence = 1L; sequence <= 10; sequence++)
        {
            var key = Key(sequence);
            var admission = window.Admit(key, out _);
            if (admission != CommandAdmission.Execute)
            {
                TestAssert.Fail("Sequence " + sequence + " was not admitted as new: " + admission);
            }
            executions[sequence] = executions.TryGetValue(sequence, out var count) ? count + 1 : 1;
            window.Complete(key, new CommandOutcome(CommandResultCode.Applied, sequence));
        }

        TestAssert.AreEqual(2, window.CachedResultCount);
        TestAssert.AreEqual(10L, window.HighWaterSequence);

        // Now replay the old ones, exactly as a client retrying after a lost reply would.
        for (var sequence = 1L; sequence <= 10; sequence++)
        {
            var key = Key(sequence);
            var admission = window.Admit(key, out var cached);
            if (sequence <= 8)
            {
                TestAssert.AreEqual(CommandAdmission.TooOld, admission,
                    "Sequence " + sequence + " left the window, so it must be rejected, not re-executed.");
            }
            else
            {
                TestAssert.AreEqual(CommandAdmission.Duplicate, admission);
                TestAssert.AreEqual(sequence, cached.AppliedHostTick);
            }
        }

        TestAssert.HasCount(10, executions);
        TestAssert.IsTrue(executions.Values.All(count => count == 1),
            "No sequence may be executed more than once, even after its result was evicted.");
    }

    [TestMethod]
    public void CommandsFromAnOldConnectionEpochAreRejected()
    {
        // The player reconnected: same PlayerId, same world, but the host issued a new connection
        // epoch. A command the client re-sends from before the reconnect must not execute.
        var window = NewWindow();
        var reconnected = ConnectionEpoch.Next(Connection.Value);

        TestAssert.AreEqual(CommandAdmission.WrongEpoch, window.Admit(Key(1, reconnected), out _));
        TestAssert.AreEqual(CommandAdmission.Execute, window.Admit(Key(1), out _));

        var newWindow = new CommandWindow(Epoch, reconnected);
        TestAssert.AreEqual(CommandAdmission.Execute, newWindow.Admit(Key(1, reconnected), out _),
            "The reconnect's own commands use a fresh dedup space.");
    }

    [TestMethod]
    public void CommandsFromAnotherAuthorityEpochAreRejected()
    {
        var window = NewWindow();
        var previousWorld = new AuthorityEpoch(0xAAAA, 0xBBBB);
        TestAssert.AreEqual(CommandAdmission.WrongEpoch, window.Admit(Key(1, epoch: previousWorld), out _));
    }

    [TestMethod]
    public void MalformedKeysAreRejected()
    {
        var window = NewWindow();
        TestAssert.AreEqual(CommandAdmission.Invalid, window.Admit(default, out _));
        TestAssert.AreEqual(CommandAdmission.Invalid, window.Admit(Key(0), out _));
        TestAssert.AreEqual(CommandAdmission.Invalid, window.Admit(Key(-1), out _));
        TestAssert.AreEqual(CommandAdmission.Invalid,
            window.Admit(new CommandKey(Epoch, default, 1), out _));
        TestAssert.AreEqual(CommandAdmission.Invalid,
            window.Admit(new CommandKey(default, Connection, 1), out _));
    }

    [TestMethod]
    public void SequenceOrderIsDecidedByTheHostNotByArrivalOrder()
    {
        // TCP can deliver a retry of an old command after a newer one. The window must not let the
        // late arrival reopen a sequence that is already behind the high-water mark.
        var window = NewWindow();
        TestAssert.AreEqual(CommandAdmission.Execute, window.Admit(Key(5), out _));
        window.Complete(Key(5), new CommandOutcome(CommandResultCode.Applied, 50));

        TestAssert.AreEqual(CommandAdmission.Execute, window.Admit(Key(6), out _));
        TestAssert.AreEqual(CommandAdmission.TooOld, window.Admit(Key(4), out _),
            "Sequence 4 is below the high-water mark and was never cached, so it must be rejected.");
    }

    [TestMethod]
    public void OutOfOrderArrivalDoesNotSkipACommittedSequence()
    {
        var window = NewWindow();
        TestAssert.AreEqual(CommandAdmission.Execute, window.Admit(Key(2), out _));
        // Sequence 1 arriving late is below the mark; it is rejected rather than applied after 2.
        TestAssert.AreEqual(CommandAdmission.TooOld, window.Admit(Key(1), out _));
    }

    [TestMethod]
    public void CompleteRefusesASequenceThatWasNeverAdmitted()
    {
        var window = NewWindow();
        TestAssert.IsFalse(window.Complete(Key(1), new CommandOutcome(CommandResultCode.Applied)),
            "Completing a command that was never admitted would cache a result for work never done.");
        TestAssert.IsFalse(window.Complete(Key(1, epoch: new AuthorityEpoch(9, 9)),
            new CommandOutcome(CommandResultCode.Applied)));
    }

    [TestMethod]
    public void CompleteIsIdempotentAndKeepsTheFirstOutcome()
    {
        var window = NewWindow();
        var key = Key(1);
        window.Admit(key, out _);
        TestAssert.IsTrue(window.Complete(key, new CommandOutcome(CommandResultCode.Applied, 10)));
        TestAssert.IsFalse(window.Complete(key, new CommandOutcome(CommandResultCode.RejectedTarget, 20)),
            "A second completion must not overwrite the outcome the client was already told.");
        TestAssert.AreEqual(CommandAdmission.Duplicate, window.Admit(key, out var cached));
        TestAssert.AreEqual(CommandResultCode.Applied, cached.Code);
    }

    [TestMethod]
    public void ConstructorRejectsAnUnusableIdentity()
    {
        TestAssert.ThrowsExactly<ArgumentException>(() => new CommandWindow(default, Connection));
        TestAssert.ThrowsExactly<ArgumentException>(() => new CommandWindow(Epoch, default));
        TestAssert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CommandWindow(Epoch, Connection, 0));
    }

    [TestMethod]
    public void RandomRetrySequenceExecutesEachCommandAtMostOnce()
    {
        // "命令最多执行一次" must hold for an arbitrary event sequence.
        // The client retries randomly, the network reorders, and results are evicted by a small
        // window; the executed-count map is the invariant under test.
        var random = new Random(20260930);
        var window = NewWindow(capacity: 8);
        var executed = new Dictionary<long, int>();
        var highest = 0L;

        for (var step = 0; step < 5000; step++)
        {
            // Sometimes invent a new command, sometimes replay an old one.
            long sequence;
            if (random.Next(3) == 0)
            {
                highest += random.Next(1, 3);
                sequence = highest;
            }
            else
            {
                sequence = random.Next(1, Math.Max(2, (int)highest + 1));
            }

            var key = Key(sequence);
            var admission = window.Admit(key, out var cached);
            switch (admission)
            {
                case CommandAdmission.Execute:
                    executed[sequence] = executed.TryGetValue(sequence, out var count) ? count + 1 : 1;
                    // Half the commands complete immediately, half stay pending to exercise retries
                    // that arrive while a command is in flight.
                    if (random.Next(2) == 0)
                    {
                        window.Complete(key, new CommandOutcome(CommandResultCode.Applied, sequence));
                    }
                    break;
                case CommandAdmission.Duplicate:
                    TestAssert.AreEqual(CommandResultCode.Applied, cached.Code);
                    break;
                case CommandAdmission.Pending:
                case CommandAdmission.TooOld:
                    break;
                default:
                    TestAssert.Fail("Unexpected admission " + admission + " at step " + step);
                    break;
            }

            // The high-water mark only ever moves forward.
            if (admission == CommandAdmission.Execute)
            {
                TestAssert.IsLessThanOrEqualTo(window.HighWaterSequence, sequence);
            }
        }

        TestAssert.IsGreaterThan(50, executed.Count, "The fuzz run did not create enough distinct commands.");
        var repeated = executed.Where(entry => entry.Value != 1).ToArray();
        TestAssert.IsEmpty(repeated,
            "These sequences executed more than once: " +
            string.Join(", ", repeated.Select(entry => entry.Key + "x" + entry.Value)));
    }
}
