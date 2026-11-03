
using NebulaeModel.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// the host's vanilla-death capture. The vanilla commit
/// (<c>CombatStat.HandleZeroHp</c>) observes its deaths on the rule's thread; the capture resolves
/// the identity there, queues it, and the frame-boundary drain opens one ledger transaction per
/// key — tombstone, dedup of every later report, without rerunning native drops or statistics.
/// </summary>
[TestClass]
public class HostDeathCaptureTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA22DEA7A22DEA7A2, 0x22A22A22A22A22A2);
    private static readonly AuthorityEpoch ForeignEpoch = new(0xDEADBEEFDEADBEEF, 0x0BAD0BAD0BAD0BAD);

    private static ObjectKey Key(int nativeId, long generation) =>
        ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, nativeId, generation);

    /// <summary>Resolves every object type 4 (Enemy) to its own slot key; everything else is unresolved.</summary>
    private static HostDeathCapture.KeyResolver Enemies(long generation) =>
        (int astroId, int objectType, int objectId, out ObjectKey key) =>
        {
            key = objectType == 4 ? Key(objectId, generation) : default;
            return objectType == 4;
        };

    [TestMethod]
    public void ACapturedDeathOpensExactlyOneLedgerTransactionAtTheDrain()
    {
        var ledger = new HostDeathLedger(Epoch);
        var capture = new HostDeathCapture(ledger, Enemies(3));

        // Capture happens inside the rule; nothing is committed until the drain.
        TestAssert.IsTrue(capture.Capture(101, 4, 7));
        TestAssert.AreEqual(1, capture.PendingCount);
        TestAssert.AreEqual(0, ledger.DeathCount, "No transaction before the frame boundary.");
        TestAssert.IsFalse(ledger.IsDead(Key(7, 3)));

        capture.Drain(1000);
        TestAssert.AreEqual(1L, capture.CommittedTotal);
        TestAssert.AreEqual(0, capture.PendingCount);
        TestAssert.AreEqual(1, ledger.DeathCount);
        TestAssert.IsTrue(ledger.IsDead(Key(7, 3)), "The tombstone is committed.");

        // A second drain is a no-op: the queue is empty, nothing double-commits.
        capture.Drain(1001);
        TestAssert.AreEqual(1L, capture.CommittedTotal);
        TestAssert.AreEqual(1, ledger.DeathCount);
    }

    [TestMethod]
    public void ARepeatedReportOfTheSameGenerationMovesNothing()
    {
        // Two sources report the same death (e.g. a parallel worker's commit and a kill replay):
        // the first opens the transaction, the second is the dedup the ledger exists for.
        var ledger = new HostDeathLedger(Epoch);
        var capture = new HostDeathCapture(ledger, Enemies(3));

        TestAssert.IsTrue(capture.Capture(101, 4, 7));
        TestAssert.IsTrue(capture.Capture(101, 4, 7));
        capture.Drain(1000);

        TestAssert.AreEqual(2L, capture.CapturedTotal);
        TestAssert.AreEqual(1L, capture.CommittedTotal);
        TestAssert.AreEqual(1L, capture.RepeatTotal);
        TestAssert.AreEqual(1, ledger.DeathCount, "One death transaction, not two.");
    }

    [TestMethod]
    public void AnUnresolvableDeathIsCountedAndNeverOpensATransaction()
    {
        var ledger = new HostDeathLedger(Epoch);
        var capture = new HostDeathCapture(ledger, Enemies(3));

        // A vegetable death (objectType 1) has no key source yet: refused and counted, not guessed.
        TestAssert.IsFalse(capture.Capture(101, 1, 7));
        TestAssert.AreEqual(1L, capture.UnresolvedTotal);
        TestAssert.AreEqual(0L, capture.CapturedTotal);
        TestAssert.AreEqual(0, capture.PendingCount);

        capture.Drain(1000);
        TestAssert.AreEqual(0, ledger.DeathCount, "No invented identity reached the ledger.");
    }

    [TestMethod]
    public void AKeyFromAnotherEpochIsRefusedByTheLedgerAndCountedSeparately()
    {
        var ledger = new HostDeathLedger(Epoch);
        HostDeathCapture.KeyResolver foreign = (int astroId, int objectType, int objectId, out ObjectKey key) =>
        {
            key = ObjectKey.Create(ForeignEpoch, PoolKind.GroundEnemy, 101, objectId, 1);
            return true;
        };
        var capture = new HostDeathCapture(ledger, foreign);

        TestAssert.IsTrue(capture.Capture(101, 4, 7));
        capture.Drain(1000);

        TestAssert.AreEqual(1L, capture.RefusedTotal, "The ledger refused the foreign key.");
        TestAssert.AreEqual(0L, capture.CommittedTotal);
        TestAssert.AreEqual(0, ledger.DeathCount);
        TestAssert.AreEqual(0L, capture.RepeatTotal, "A refusal is not a dedup hit.");
    }

    [TestMethod]
    public void AQueueBeyondItsCapacityIsRefusedFailClosed()
    {
        var ledger = new HostDeathLedger(Epoch);
        var capture = new HostDeathCapture(ledger, Enemies(3), pendingCapacity: 2);

        TestAssert.IsTrue(capture.Capture(101, 4, 7));
        TestAssert.IsTrue(capture.Capture(101, 4, 8));
        TestAssert.IsFalse(capture.Capture(101, 4, 9));
        TestAssert.AreEqual(1L, capture.RefusedFullTotal);
        TestAssert.AreEqual(2, capture.PendingCount);

        capture.Drain(1000);
        TestAssert.AreEqual(2L, capture.CommittedTotal);
        TestAssert.AreEqual(0, capture.PendingCount);
        TestAssert.IsTrue(capture.Capture(101, 4, 9), "Draining frees capacity.");
    }

    [TestMethod]
    public void AWorldChangeDropsTheQueuedDeathsOfTheOldEpoch()
    {
        var ledger = new HostDeathLedger(Epoch);
        var capture = new HostDeathCapture(ledger, Enemies(3));
        TestAssert.IsTrue(capture.Capture(101, 4, 7));

        capture.Clear();
        TestAssert.AreEqual(0, capture.PendingCount);
        capture.Drain(1000);
        TestAssert.AreEqual(0L, capture.CommittedTotal, "The old epoch's deaths never commit.");
        TestAssert.AreEqual(0, ledger.DeathCount);
    }

    [TestMethod]
    public void ConcurrentCapturesOfDistinctSlotsAllLand()
    {
        // HandleZeroHp runs inside the parallel combat workers, so captures overlap. Every one of
        // them must land: N workers × M deaths, N×M transactions, none lost, none duplicated.
        var ledger = new HostDeathLedger(Epoch);
        HostDeathCapture.KeyResolver anyEnemy = (int astroId, int objectType, int objectId, out ObjectKey key) =>
        {
            key = objectType == 4
                ? ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, objectId, 1)
                : default;
            return objectType == 4;
        };
        var capture = new HostDeathCapture(ledger, anyEnemy, pendingCapacity: 1 << 15);

        const int workers = 8;
        const int deathsPerWorker = 200;
        var barrier = new Barrier(workers);
        var tasks = new Task[workers];
        for (var w = 0; w < workers; w++)
        {
            var worker = w;
            tasks[w] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < deathsPerWorker; i++)
                {
                    capture.Capture(101, 4, worker * deathsPerWorker + i + 1);
                }
            });
        }
        Task.WaitAll(tasks);

        capture.Drain(1000);
        TestAssert.AreEqual((long)workers * deathsPerWorker, capture.CommittedTotal,
            "Every concurrent capture committed exactly once.");
        TestAssert.AreEqual(0, capture.PendingCount);
        TestAssert.AreEqual(0L, capture.RefusedFullTotal);
        TestAssert.AreEqual(0L, capture.UnresolvedTotal);
    }

    [TestMethod]
    public void ConcurrentObservationsOfOneSlotAgreeOnOneGeneration()
    {
        // The tracker is the shared mint between the frame-boundary scan and the worker-thread
        // capture: concurrent observations of one slot must return one generation, not a race.
        var tracker = new SlotGenerationTracker();
        var results = new long[64];
        Parallel.For(0, results.Length, i =>
        {
            results[i] = tracker.ObserveOccupied(7);
        });
        foreach (var generation in results)
        {
            TestAssert.AreEqual(1L, generation, "One live slot, one generation, whatever the thread.");
        }

        // Distinct slots mint independently under the same contention.
        Parallel.For(1, 33, slot => tracker.ObserveOccupied(slot));
        TestAssert.AreEqual(32, tracker.TrackedSlots);
    }

    // --- Session wiring: the drain runs at the frame boundary, before the frame's capture. ---

    [TestMethod]
    public void TheSessionDrainsTheCaptureAtTheFrameBoundaryBeforeItsScan()
    {
        var identity = new AuthoritySessionState();
        identity.ConfirmHandshake();
        var session = new AuthoritySession(identity);
        session.BeginAuthorityWorld(Epoch, isHost: true);

        // Without adapters there is no capture: the session stays fail-closed.
        TestAssert.IsNull(session.HostDeathCapture);
        session.OnFrameComplete(100);

        var ledger = session.HostDeaths;
        var capture = new HostDeathCapture(ledger, Enemies(3));
        session.SetHostDeathCapture(capture);
        TestAssert.IsTrue(capture.Capture(101, 4, 7));

        // The frame boundary commits the death into the session's own ledger: the tombstone and
        // its task release exist before the frame's published despawn would go out.
        session.OnFrameComplete(200);
        TestAssert.AreEqual(1L, capture.CommittedTotal);
        TestAssert.AreEqual(1, ledger.DeathCount);
        TestAssert.IsTrue(ledger.IsDead(Key(7, 3)));

        // A world change drops the queue and the capture itself, and replaces the ledger (fail-
        // closed: nothing of the old epoch survives, the new world starts with an empty ledger).
        session.BeginAuthorityWorld(new AuthorityEpoch(0x2222333344445555, 0x6666777788889999), isHost: true);
        TestAssert.IsNull(session.HostDeathCapture);
        TestAssert.IsNotNull(session.HostDeaths);
        TestAssert.AreNotEqual(ledger, session.HostDeaths, "The new world does not inherit the old ledger.");
        TestAssert.AreEqual(0, session.HostDeaths.DeathCount);
    }
}
