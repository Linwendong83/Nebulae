using System.Reflection;
using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.GameHistory;
using NebulaeWorld.GameDataHistory;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class ResearchContributionTest
{
    [TestMethod]
    public void ContinuousNativeContributionsBatchAndCheckpointReservationsAreConserved()
    {
        var buffer = new ResearchContributionBuffer(); var sent = 0;
        var points = new Dictionary<int, int> { [1001] = 7200, [1002] = 10800 };
        for (var tick = 1; tick <= 600; tick++)
        {
            points[1001] -= 2; points[1002] -= 3;
            buffer.Record(17, 1, 1, [1001, 1002], [2, 3]);
            if (tick % 30 != 0) continue;
            var escrow = new Dictionary<int, int>(points); buffer.Overlay(escrow);
            TestAssert.AreEqual(7200 - (sent * 60), escrow[1001]);
            buffer.Begin(); var hashes = buffer.Active.Hashes;
            TestAssert.AreEqual(30L, hashes);
            TestAssert.IsEmpty(buffer.Complete(hashes)); sent++;
        }
        TestAssert.AreEqual(20, sent, "600 native ticks need 20 contributions.");
        TestAssert.AreEqual(0L, buffer.Hashes); TestAssert.AreEqual(6000, points[1001]);
        TestAssert.IsFalse(buffer.CanRecord(17, 1, 1_000_001, [1001], [1]));
    }

    [TestMethod]
    public void LateTechAndLevelBatchesRefundOnlyUnacceptedMaterialPoints()
    {
        var buffer = new ResearchContributionBuffer();
        buffer.Record(17, 1, 10, [1001], [12]); buffer.Begin();
        buffer.Record(17, 2, 5, [1001], [24]);
        TestAssert.AreEqual(10L, buffer.Outstanding(17, 1)); TestAssert.AreEqual(5L, buffer.Outstanding(17, 2));
        var refund = buffer.Complete(4);
        TestAssert.AreEqual(72, refund[1001]);
        var checkpoint = new Dictionary<int, int> { [1001] = 72 }; buffer.Overlay(checkpoint);
        TestAssert.AreEqual(192, checkpoint[1001]);
        buffer.Begin(); TestAssert.AreEqual(2, buffer.Active.Level);
        TestAssert.AreEqual(120, buffer.Complete(0)[1001]);
        TestAssert.AreEqual(0L, buffer.Hashes);
        TestAssert.Throws<InvalidOperationException>(() => buffer.Complete(0));
    }

    [TestMethod]
    public void GlobalCommitClipsRemainingProgressAndCannotCrossTechOrLevel()
    {
        var intent = new ResearchContributionIntent(17, 3, 40);
        var state = new TechState { curLevel = 3, hashUploaded = 990, hashNeeded = 1000 };
        TestAssert.AreEqual(10L, ResearchContributionManager.AcceptedHashes(intent, 17, state));
        TestAssert.AreEqual(0L, ResearchContributionManager.AcceptedHashes(intent, 18, state));
        state.curLevel = 4;
        TestAssert.AreEqual(0L, ResearchContributionManager.AcceptedHashes(intent, 17, state));
        state.curLevel = 3; state.unlocked = true;
        TestAssert.AreEqual(0L, ResearchContributionManager.AcceptedHashes(intent, 17, state));
        for (var length = 0; length < 16; length++) TestAssert.IsFalse(ResearchContributionIntent.TryDecode(intent.Encode().Take(length).ToArray(), out _));
        TestAssert.IsTrue(ResearchContributionIntent.TryDecode(intent.Encode(), out var decoded)); TestAssert.AreEqual(intent.Hashes, decoded.Hashes);
        TestAssert.IsTrue(ResearchContributionIntent.TryDecode(new ResearchContributionIntent(1001, 0, 1).Encode(), out _), "Native single-level techs start at level zero.");
        TestAssert.AreEqual(1L, ResearchContributionManager.AcceptedHashes(new ResearchContributionIntent(1001, 0, 1), 1001,
            new TechState { curLevel = 0, hashUploaded = 0, hashNeeded = 100 }));
    }

    [TestMethod]
    public void HostMaterialDebitIsAtomicAndUsesResearchCacheInsteadOfPackageItems()
    {
        var personal = new MechaData(); personal.ResearchPoints.Add(1001, 100); personal.ResearchPoints.Add(1002, 9);
        TestAssert.IsFalse(ResearchContributionManager.TryDebit(personal, [1001, 1002], [2, 3], 4, out _));
        TestAssert.AreEqual(100, personal.ResearchPoints[1001]);
        TestAssert.IsTrue(ResearchContributionManager.TryDebit(personal, [1001, 1002], [2, 3], 3, out var transfers));
        TestAssert.AreEqual(94, personal.ResearchPoints[1001]); TestAssert.AreEqual(0, personal.ResearchPoints[1002]);
        TestAssert.IsTrue(transfers.All(transfer => transfer.Research && !transfer.Hand && transfer.Inc == 0));
        var ledger = new PlayerInventoryTransferLedger(); ledger.Record(8, transfers);
        TestAssert.AreEqual(-6, ledger.PendingAfter(0).First().Count);
        ledger.Acknowledge(8); TestAssert.AreEqual(0, ledger.PendingCount);
    }

    [TestMethod]
    public void ResearchCacheRoundTripsAndRejectsInvalidItems()
    {
        var read = AccessTools.Method(typeof(MechaData), "ReadResearch");
        var personal = new MechaData();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(0x4C414250); writer.Write(2); writer.Write(1001); writer.Write(3599); writer.Write(1002); writer.Write(0);
        }
        stream.Position = 0;
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true)) read.Invoke(personal, [reader]);
        TestAssert.AreEqual(3599, personal.ResearchPoints[1001]);
        TestAssert.AreEqual(0, personal.ResearchPoints[1002]);
        stream.Position = 8;
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) writer.Write(0);
        stream.Position = 0;
        using var invalid = new BinaryReader(stream);
        TestAssert.Throws<TargetInvocationException>(() => read.Invoke(personal, [invalid]));
    }
}
