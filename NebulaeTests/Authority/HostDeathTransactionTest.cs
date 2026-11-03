using NebulaeModel.Authority;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// one death transaction per object generation, deduplicating native deaths,
/// with tombstones that belong to the exact object generation.
/// </summary>
[TestClass]
public class HostDeathTransactionTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA19DEA7A19DEA7A, 0x19C19C19C19C19);

    private static ObjectKey GroundEnemy(int nativeId, long generation, int planet = 101) =>
        ObjectKey.Create(Epoch, PoolKind.GroundEnemy, planet, nativeId, generation);

    [TestMethod]
    public void MultiWeaponKillOpensExactlyOneTransaction()
    {
        var ledger = new HostDeathLedger(Epoch);
        var target = GroundEnemy(7, 3);

        var first = ledger.OpenDeath(target, 1000);
        var second = ledger.OpenDeath(target, 1000);
        var third = ledger.OpenDeath(target, 1001);

        TestAssert.IsTrue(first.IsFirst, "the first report opens the transaction.");
        TestAssert.IsFalse(second.IsFirst, "a second killing blow in the same frame is a cached no-op.");
        TestAssert.IsFalse(third.IsFirst, "a later replay is a cached no-op too.");
        TestAssert.AreEqual(first.TransactionId, second.TransactionId);
        TestAssert.AreEqual(first.DeathTick, second.DeathTick);
        TestAssert.AreEqual(1, ledger.DeathCount);
    }

    [TestMethod]
    public void DeathTombstonesBelongToTheExactGeneration()
    {
        var ledger = new HostDeathLedger(Epoch);
        var target = GroundEnemy(9, 5);
        TestAssert.IsFalse(ledger.IsDead(target));

        ledger.OpenDeath(target, 2000);

        TestAssert.IsTrue(ledger.IsDead(target));
        // A recycled slot is a different key.
        var nextGeneration = GroundEnemy(9, 6);
        TestAssert.IsFalse(ledger.IsDead(nextGeneration));
    }

    [TestMethod]
    public void WrongEpochAndNonDeathPoolAreRefused()
    {
        var ledger = new HostDeathLedger(Epoch);
        var otherEpoch = new AuthorityEpoch(0x123, 0x456);
        var foreign = ObjectKey.Create(otherEpoch, PoolKind.GroundEnemy, 101, 1, 1);

        TestAssert.IsTrue(foreign.IsValid, "sanity: the foreign key itself is well formed.");
        TestAssert.IsFalse(ledger.OpenDeath(foreign, 1).HasTombstone, "wrong epoch is refused.");
        var prebuild = ObjectKey.Create(Epoch, PoolKind.Prebuild, 101, 4, 1);
        TestAssert.IsFalse(ledger.OpenDeath(prebuild, 1).HasTombstone,
            "prebuilds are build-domain records, not deaths.");
        TestAssert.AreEqual(0, ledger.DeathCount);
    }

    [TestMethod]
    public void SaturatedLedgerRefusesFailClosedInsteadOfEvicting()
    {
        var ledger = new HostDeathLedger(Epoch, capacity: 1);
        var first = ledger.OpenDeath(GroundEnemy(1, 1), 10);
        var second = ledger.OpenDeath(GroundEnemy(2, 1), 10);

        TestAssert.IsTrue(first.IsFirst);
        TestAssert.IsTrue(second.Saturated, "the second death is refused, not tracked over the first.");
        TestAssert.IsFalse(second.IsFirst);
        TestAssert.IsFalse(second.HasTombstone);
        TestAssert.IsTrue(ledger.IsSaturated);
        TestAssert.IsTrue(ledger.IsDead(first.Target), "the tracked death keeps its tombstone — no eviction.");
    }

    [TestMethod]
    public void TheSessionClearsItsNativeDeathObservationsWhenTheWorldEnds()
    {
        using var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        TestAssert.IsNotNull(session.HostDeaths);
        var target = GroundEnemy(7, 1);
        session.HostDeaths.OpenDeath(target, 10);
        TestAssert.IsTrue(session.HostDeaths.IsDead(target));
        session.Reset();
        TestAssert.IsNull(session.HostDeaths);
    }

}
