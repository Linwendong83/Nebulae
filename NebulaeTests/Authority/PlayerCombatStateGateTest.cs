using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// Unchanged player combat settings do not produce another report.
/// </summary>
[TestClass]
public class PlayerCombatStateGateTest
{
    [TestMethod]
    public void AnUnchangedSettingsReportIsNotSentAgain()
    {
        var report = (Flags: 20, Energy: 5000d, DebitAck: 0d, ShieldSpeed: 1d);

        TestAssert.IsTrue(CombatAuthorityManager.IsUnchangedSettingsReport(report, report));
        TestAssert.IsTrue(CombatAuthorityManager.IsUnchangedSettingsReport(
            (20, 5000d, 0d, 1d), (20, 5000d, 0d, 1d)), "Two equal reports describe the same configuration.");
    }

    [TestMethod]
    public void TheFirstSettingsReportIsAlwaysSent()
    {
        // The sentinel is what makes the first report after a world change unsuppressable. It is a
        // combination no real report can produce, because the flag word is a small non-negative
        // bitfield and can never equal int.MinValue — whatever the energy values happen to be. A
        // reconnect hands the host a fresh actor that has never been told this player's settings, so
        // losing that first report would leave the host's configuration stale until the player
        // happened to change something.
        var real = (Flags: 20, Energy: 5000d, DebitAck: 0d, ShieldSpeed: 1d);

        TestAssert.IsFalse(CombatAuthorityManager.IsUnchangedSettingsReport(
            CombatAuthorityManager.NoSettingsSent, real));
        TestAssert.IsFalse(CombatAuthorityManager.IsUnchangedSettingsReport(
            CombatAuthorityManager.NoSettingsSent, (0, 0d, 0d, 0d)),
            "Even an all-zero report must beat the sentinel, because its flag word still differs.");
    }

    [TestMethod]
    public void EachMovedSettingsFieldIsNews()
    {
        var previous = (Flags: 20, Energy: 5000d, DebitAck: 0d, ShieldSpeed: 1d);

        // Every field of the report is load-bearing: the flag word carries the player's combat
        // configuration, the energy is the value the host reconciles its own balance against, the
        // debit acknowledgement is the receipt that lets the host settle a cumulative ledger, and the
        // shield speed is a setting the host applies directly. Missing any one of them would either
        // freeze a real change on the host or leave a ledger unsettled.
        TestAssert.IsFalse(CombatAuthorityManager.IsUnchangedSettingsReport(previous, (21, 5000d, 0d, 1d)));
        TestAssert.IsFalse(CombatAuthorityManager.IsUnchangedSettingsReport(previous, (20, 5001d, 0d, 1d)));
        TestAssert.IsFalse(CombatAuthorityManager.IsUnchangedSettingsReport(previous, (20, 5000d, 7d, 1d)));
        TestAssert.IsFalse(CombatAuthorityManager.IsUnchangedSettingsReport(previous, (20, 5000d, 0d, 2d)));
    }
}
