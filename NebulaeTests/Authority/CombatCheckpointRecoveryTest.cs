using HarmonyLib;
using global::NebulaeModel.Authority;
using NebulaeModel.Packets.Combat.Mecha;
using NebulaeWorld;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class CombatCheckpointRecoveryTest
{
    [TestMethod]
    public void ReplacingAnActorBodyKeepsItsConnectionLedgerButANewConnectionStartsFresh()
    {
        var authority = new CombatAuthorityManager();
        var retain = AccessTools.Method(typeof(CombatAuthorityManager), "RetainActor");
        var actor = retain.Invoke(authority, new object[] { (ushort)2, new ConnectionEpoch(1) })!;
        AccessTools.Field(actor.GetType(), "LastCommand").SetValue(actor, 2121L);
        AccessTools.Field(actor.GetType(), "CoreDebit").SetValue(actor, 50d);
        var debits = (CombatConsumptionLedger)AccessTools.Field(actor.GetType(), "Debits").GetValue(actor);
        debits.Debit(1601, 8);
        TestAssert.AreSame(actor, retain.Invoke(authority, new object[] { (ushort)2, new ConnectionEpoch(1) }));
        TestAssert.IsTrue(authority.ValidateCheckpoint(2, 2121, 50, new[] { 1601 }, new[] { 8 }));
        TestAssert.AreNotSame(actor, retain.Invoke(authority, new object[] { (ushort)2, new ConnectionEpoch(2) }));
        TestAssert.IsTrue(authority.ValidateCheckpoint(2, 0, 0, Array.Empty<int>(), Array.Empty<int>()));
        TestAssert.IsFalse(authority.ValidateCheckpoint(2, 2121, 50, new[] { 1601 }, new[] { 8 }));
    }

    [TestMethod]
    public void AStaleHighCommandWatermarkIsClampedInsteadOfBlockingExit()
    {
        var authority = new CombatAuthorityManager();
        var actor = AccessTools.Method(typeof(CombatAuthorityManager), "RetainActor")
            .Invoke(authority, new object[] { (ushort)2, new ConnectionEpoch(1) })!;
        AccessTools.Field(actor.GetType(), "LastCommand").SetValue(actor, 20L);
        AccessTools.Field(actor.GetType(), "CoreDebit").SetValue(actor, 0d);

        TestAssert.IsTrue(authority.ValidateCheckpoint(2, 147, 0, Array.Empty<int>(), Array.Empty<int>(),
            out var accepted));
        TestAssert.AreEqual(20L, accepted);
    }

    [TestMethod]
    [DataRow((byte)CheckpointRejectReason.InvalidLedger)]
    [DataRow((byte)CheckpointRejectReason.CombatLedger)]
    public void RefusedLedgerWaitsForFreshCombatFactsAndBacksOffWithoutCompletingTheCheckpoint(byte reason)
    {
        var previous = Multiplayer.Session;
        try
        {
            var session = Uninitialized<MultiplayerSession>();
            session.IsGameLoaded = true;
            session.CombatAuthority = new CombatAuthorityManager();
            SetBackingField(session.CombatAuthority, nameof(CombatAuthorityManager.LastAppliedRevision), 7L);
            Multiplayer.Session = session;
            var life = new PlayerLifeManager();
            AccessTools.Field(typeof(PlayerLifeManager), "pending").SetValue(life, new PlayerLifePacket
            { Life = new global::NebulaeModel.DataStructures.PlayerLifeData { Revision = 10 } });
            AccessTools.Field(typeof(PlayerLifeManager), "pendingTicket").SetValue(life, 2L);
            life.Acknowledge(PlayerLifePacket.CreateReceipt(2, 10, CheckpointReceiptStatus.Retry, 0, (CheckpointRejectReason)reason));
            TestAssert.IsGreaterThan(DateTime.UtcNow.Ticks, (long)AccessTools.Field(typeof(PlayerLifeManager), "retryAfter").GetValue(life));
            TestAssert.AreEqual(7L, AccessTools.Field(typeof(PlayerLifeManager), "requiredCombatRevision").GetValue(life));
            TestAssert.IsTrue(life.BlocksInventory);
            TestAssert.AreEqual(0L, life.AcceptedTicket);
            AccessTools.Field(typeof(PlayerLifeManager), "retryAfter").SetValue(life, 0L);
            // No inventory capture or network exists. Reaching either would fail: the unchanged
            // combat revision must keep the checkpoint queued instead of retrying stale facts.
            life.ServiceCheckpoint();
            TestAssert.AreEqual(2L, AccessTools.Field(typeof(PlayerLifeManager), "requestedTicket").GetValue(life));
        }
        finally { Multiplayer.Session = previous; }
    }
}
