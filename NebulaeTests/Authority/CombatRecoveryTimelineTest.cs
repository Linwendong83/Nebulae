using NebulaeModel.Packets.Combat;
using NebulaeModel.Utils;
using NebulaeWorld.Combat;
using NebulaeWorld;
using NebulaeModel.Authority;
using HarmonyLib;
using NebulaePatcher.Patches.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class CombatRecoveryTimelineTest
{
    private Harmony shieldAudio = null!;
    private bool previousLoaded;
    private int[] previousStacks = null!;

    [TestInitialize]
    public void Setup()
    {
        shieldAudio = new Harmony("nebulae.tests.recovery-shield-audio");
        shieldAudio.Patch(AccessTools.Method(typeof(CombatRecoveryView), nameof(CombatRecoveryView.Absorb)),
            transpiler: new HarmonyMethod(typeof(CombatRecoveryTimelineTest), nameof(WithoutShieldAudio)));
        previousLoaded = StorageComponent.staticLoaded;
        previousStacks = StorageComponent.itemStackCount;
        StorageComponent.staticLoaded = true;
        StorageComponent.itemStackCount = Enumerable.Repeat(100, 12000).ToArray();
    }

    [TestCleanup]
    public void Cleanup()
    {
        shieldAudio.UnpatchSelf();
        StorageComponent.staticLoaded = previousLoaded;
        StorageComponent.itemStackCount = previousStacks;
    }
    private static bool ResistWithoutAudio(Mecha actual, ref int damage) => actual.EnergyShieldResist(ref damage, out _);
    private static IEnumerable<CodeInstruction> WithoutShieldAudio(IEnumerable<CodeInstruction> codes)
    {
        var native = AccessTools.Method(typeof(Mecha), nameof(Mecha.EnergyShieldResist), [typeof(int).MakeByRefType()]);
        foreach (var code in codes)
        {
            if (code.Calls(native)) { code.opcode = System.Reflection.Emit.OpCodes.Call; code.operand = AccessTools.Method(typeof(CombatRecoveryTimelineTest), nameof(ResistWithoutAudio)); }
            yield return code;
        }
    }

    private static Mecha Create(bool laserActive = true)
    {
        var player = Uninitialized<Player>();
        player.isAlive = true;
        player.SetHiddenProperty(nameof(Player.package), new StorageComponent(2));
        return new Mecha
        {
            player = player,
            history = new GameHistoryData(),
            hp = 1000,
            hpMax = 1000,
            coreEnergy = 1000000,
            coreEnergyCap = 1000000,
            reactorPowerGen = 120000,
            laserEnergy = 10000,
            laserEnergyCapacity = 100000,
            laserActive = laserActive,
            laserRecharging = true,
            energyChanges = new double[32],
            energyShieldResistHistory = new long[60],
            ammoStorage = new StorageComponent(1),
            bombStorage = new StorageComponent(1),
            groundCombatModule = new CombatModuleComponent(),
            spaceCombatModule = new CombatModuleComponent()
        };
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(6)]
    [DataRow(24)]
    public void DelayedSnapshotRecoversToItsDisplayTickUsingNativeResults(int age)
    {
        var checkpoint = Create();
        var native = Create();
        long now = 100 + age;
        var view = new CombatRecoveryView(() => now);
        for (var i = 0; i < age; i++) native.UpdateCombatStats(1.0 / 60);

        view.Rebase(checkpoint, 100);
        var predicted = view.Get(checkpoint);
        TestAssert.AreEqual(native.laserEnergy, predicted.laserEnergy);
        TestAssert.AreEqual(native.laserRecharging, predicted.laserRecharging);
        TestAssert.AreEqual(native.coreEnergy, predicted.coreEnergy, .01);
        TestAssert.AreEqual(10000L, checkpoint.laserEnergy, "Presentation must not become a checkpoint write.");
        view.Tick(checkpoint, 1.0 / 60);
        TestAssert.AreEqual(native.laserEnergy, view.Get(checkpoint).laserEnergy, "Do not recover the receipt tick twice.");
    }

    [TestMethod]
    public void UnacknowledgedLaserConsumptionIsReplayedBeforeSubsequentRecovery()
    {
        var checkpoint = Create();
        var native = Create();
        long now = 108;
        for (var tick = 101; tick <= now; tick++)
        {
            native.UpdateCombatStats(1.0 / 60);
            if (tick == 102) { native.laserEnergy -= 6000; native.laserRecharging = true; }
        }
        var view = new CombatRecoveryView(() => now);
        view.Rebase(checkpoint, 100, new[] { new CombatRecoveryView.Expenditure(102, 6000, 0, 0, true) });
        TestAssert.AreEqual(native.laserEnergy, view.Get(checkpoint).laserEnergy);
        TestAssert.AreEqual(native.coreEnergy, view.Get(checkpoint).coreEnergy, .01);
        view.Rebase(checkpoint, 100, new[] { new CombatRecoveryView.Expenditure(102, 6000, 0, 0, true) });
        TestAssert.AreEqual(native.laserEnergy, view.Get(checkpoint).laserEnergy, "Duplicate reconciliation must not spend or recover twice.");
    }

    [TestMethod]
    public void BulletScopesAndRejectionLeaveLaserEnergyAndChargingStateAlone()
    {
        var mecha = Create(false);
        var resources = new PlayerWeaponResources();
        var shot = new PlayerShotId(2, 1, 1);
        using (resources.Enter(mecha))
        {
            resources.Reserve(shot, EAmmoType.Bullet, false, 1601);
            mecha.coreEnergy -= 100;
        }
        TestAssert.AreEqual(10000L, resources.Recovery.Get(mecha).laserEnergy);
        TestAssert.IsTrue(resources.Recovery.Get(mecha).laserRecharging);
        resources.Reject(shot);
        TestAssert.AreEqual(10000L, resources.Recovery.Get(mecha).laserEnergy);
        TestAssert.IsTrue(resources.Recovery.Get(mecha).laserRecharging, "A rejected bullet is not a laser recharge event.");
        TestAssert.AreEqual(1000000d, resources.Recovery.Get(mecha).coreEnergy);
    }

    [TestMethod]
    public void DisabledLaserStaysConstantWhileDelayedBulletCostsAreReconciled()
    {
        var mecha = Create(false);
        long now = 120;
        var view = new CombatRecoveryView(() => now);
        view.Rebase(mecha, 100, new[] { new CombatRecoveryView.Expenditure(105, 0, 0, 100, null) });
        for (; now < 130; now++) view.Tick(mecha, 1.0 / 60);
        TestAssert.AreEqual(10000L, view.Get(mecha).laserEnergy);
        TestAssert.IsTrue(view.Get(mecha).laserRecharging);
    }

    [TestMethod]
    public void StaleSnapshotCannotManufactureRecovery()
    {
        var mecha = Create();
        var view = new CombatRecoveryView(() => 300);
        view.Rebase(mecha, 100, new[] { new CombatRecoveryView.Expenditure(250, 1000, 0, 0, null) });
        view.Tick(mecha, 1.0 / 60);
        TestAssert.AreEqual(9000L, view.Get(mecha).laserEnergy);
    }

    [TestMethod]
    public void NativeFleetCoreConsumptionDoesNotResetPredictedLaserRecharge()
    {
        var previousProbe = AuthorityRuleGuard.Probe;
        var previousSession = Multiplayer.Session;
        try
        {
            AuthorityRuleGuard.Probe = () => new AuthorityGuardSession(true, false, false);
            var session = Uninitialized<MultiplayerSession>();
            session.PlayerWeapons = new PlayerWeaponManager();
            Multiplayer.Session = session;
            var mecha = Create();
            mecha.laserRecharging = false;
            session.PlayerWeapons.Resources.Recovery.Spend(mecha, 0, 0, 0, true);
            NativeCraftEnergy_Patch.Before(mecha, out var state);
            mecha.coreEnergy -= 100;
            TestAssert.IsNull(NativeCraftEnergy_Patch.After(state, null));
            TestAssert.IsTrue(session.PlayerWeapons.Resources.Recovery.Get(mecha).laserRecharging);
            TestAssert.AreEqual(10000L, session.PlayerWeapons.Resources.Recovery.Get(mecha).laserEnergy);
            TestAssert.AreEqual(999900d, session.PlayerWeapons.Resources.Recovery.Get(mecha).coreEnergy);
            TestAssert.AreEqual(1000000d, mecha.coreEnergy);
        }
        finally { AuthorityRuleGuard.Probe = previousProbe; Multiplayer.Session = previousSession; }
    }

    [TestMethod]
    public void NativeFleetBudgetRestoresAuthoritativeEnergyAndSettlesPartialConsumptionOnError()
    {
        var previousProbe = AuthorityRuleGuard.Probe; var previousSession = Multiplayer.Session;
        try
        {
            AuthorityRuleGuard.Probe = () => new AuthorityGuardSession(true, false, false);
            var session = Uninitialized<MultiplayerSession>(); session.PlayerWeapons = new PlayerWeaponManager(); Multiplayer.Session = session;
            var mecha = Create();
            session.PlayerWeapons.Resources.Recovery.Spend(mecha, 0, 0, 100, true);
            NativeCraftEnergy_Patch.Before(mecha, out var outer);
            TestAssert.AreEqual(999900d, mecha.coreEnergy);
            TestAssert.IsFalse(NativeCraftEnergy_Patch.Accounting(mecha));
            NativeCraftEnergy_Patch.Before(mecha, out var nested);
            mecha.coreEnergy -= 40;
            NativeCraftEnergy_Patch.After(nested, null);
            TestAssert.AreEqual(999860d, mecha.coreEnergy, "A nested native call must share the borrowed budget.");
            var error = new InvalidOperationException("native failure");
            TestAssert.AreSame(error, NativeCraftEnergy_Patch.After(outer, error));
            TestAssert.AreEqual(1000000d, mecha.coreEnergy, "Native prediction must restore the actual snapshot even on an exception.");
            TestAssert.AreEqual(999860d, session.PlayerWeapons.Resources.Recovery.Get(mecha).coreEnergy);
            TestAssert.IsTrue(session.PlayerWeapons.Resources.Recovery.Get(mecha).laserRecharging);
            TestAssert.IsTrue(NativeCraftEnergy_Patch.Accounting(mecha));
        }
        finally { AuthorityRuleGuard.Probe = previousProbe; Multiplayer.Session = previousSession; }
    }

    [TestMethod]
    public void NativeFleetBudgetDoesNotChangeSinglePlayerOrHostEnergy()
    {
        var previous = AuthorityRuleGuard.Probe;
        try
        {
            foreach (var session in new[] { AuthorityGuardSession.Inactive, new AuthorityGuardSession(true, true, false) })
            {
                AuthorityRuleGuard.Probe = () => session;
                var mecha = Create();
                NativeCraftEnergy_Patch.Before(mecha, out var state); mecha.coreEnergy -= 40;
                NativeCraftEnergy_Patch.After(state, null);
                TestAssert.AreEqual(999960d, mecha.coreEnergy);
            }
        }
        finally { AuthorityRuleGuard.Probe = previous; }
    }

    [TestMethod]
    public void SnapshotCorrectionIsSmoothedOnlyForTheHud()
    {
        var actual = Create(false);
        actual.laserEnergy = 50000;
        long now = 100;
        var view = new CombatRecoveryView(() => now);
        TestAssert.AreEqual(50000L, view.GetDisplay(actual).laserEnergy);
        actual.laserEnergy = 30000;
        view.Rebase(actual, 100);
        TestAssert.AreEqual(30000L, view.Get(actual).laserEnergy, "Firing must use the corrected budget immediately.");
        TestAssert.AreEqual(50000L, view.GetDisplay(actual).laserEnergy, "A network correction must not jump the bar.");
        now = 104;
        TestAssert.AreEqual(40000L, view.GetDisplay(actual).laserEnergy);
        now = 108;
        TestAssert.AreEqual(30000L, view.GetDisplay(actual).laserEnergy);
        TestAssert.AreEqual(30000L, actual.laserEnergy);
    }

    [TestMethod]
    public void LocalLaserSpendingStaysImmediateWhileNetworkCorrectionIsSmoothed()
    {
        var actual = Create(false);
        actual.laserEnergy = 50000;
        var view = new CombatRecoveryView(() => 100);
        view.GetDisplay(actual);
        actual.laserEnergy = 30000;
        view.Rebase(actual, 100);
        view.Spend(actual, 10000, 0, 0);
        TestAssert.AreEqual(20000L, view.Get(actual).laserEnergy);
        TestAssert.AreEqual(40000L, view.GetDisplay(actual).laserEnergy, "Local fire removes the full 10000 immediately.");
        TestAssert.AreNotSame(view.Get(actual), view.GetDisplay(actual));
    }

    [TestMethod]
    public void SnapshotRechargeColorWaitsForAStableStateButNativeChangesAreImmediate()
    {
        var actual = Create(false);
        actual.laserRecharging = false;
        long now = 100;
        var view = new CombatRecoveryView(() => now);
        view.GetDisplay(actual);
        actual.laserRecharging = true;
        view.Rebase(actual, 100);
        TestAssert.IsTrue(view.Get(actual).laserRecharging);
        TestAssert.IsFalse(view.GetDisplay(actual).laserRecharging);
        now = 108;
        TestAssert.IsTrue(view.GetDisplay(actual).laserRecharging);
        view.Spend(actual, 1000, 0, 0, false);
        TestAssert.IsFalse(view.GetDisplay(actual).laserRecharging);
    }

    [TestMethod]
    public void FailedNativeLaserAttemptKeepsItsRechargeLockAcrossHostSnapshots()
    {
        var actual = Create(false);
        actual.laserRecharging = false;
        long now = 100;
        var view = new CombatRecoveryView(() => now);
        view.Spend(actual, 0, 0, 0, true);
        now = 106;
        view.Rebase(actual, 100);
        TestAssert.IsTrue(view.Get(actual).laserRecharging, "The host never received a shot for the failed local attempt.");
        var calls = 0;
        view.Get(actual).TickLaserFireCondition(() => { calls++; return default; });
        TestAssert.AreEqual(0, calls, "Use the real native fire gate while recharge is locked.");
        actual.laserEnergy = actual.laserEnergyCapacity;
        now = 108;
        view.Rebase(actual, 102);
        TestAssert.IsFalse(view.Get(actual).laserRecharging);
        view.Get(actual).TickLaserFireCondition(() => { calls++; return default; });
        TestAssert.AreEqual(1, calls, "Full charge releases the native fire gate.");
    }

    [TestMethod]
    public void RejectedShotRefundsTheBudgetImmediatelyWithoutFlashingTheBar()
    {
        var actual = Create(false);
        actual.laserEnergy = actual.laserEnergyCapacity;
        long now = 100;
        var view = new CombatRecoveryView(() => now);
        view.Spend(actual, 30000, 0, 100, true);
        TestAssert.AreEqual(70000L, view.GetDisplay(actual).laserEnergy);
        view.Refund(actual, 30000, 0, 100);
        TestAssert.AreEqual(100000L, view.Get(actual).laserEnergy);
        TestAssert.IsFalse(view.Get(actual).laserRecharging);
        TestAssert.AreEqual(70000L, view.GetDisplay(actual).laserEnergy);
        TestAssert.IsTrue(view.GetDisplay(actual).laserRecharging);
        now = 108;
        TestAssert.AreEqual(100000L, view.GetDisplay(actual).laserEnergy);
        TestAssert.IsFalse(view.GetDisplay(actual).laserRecharging);
        TestAssert.AreEqual(1000000d, view.Get(actual).coreEnergy);
    }

    [TestMethod]
    [DataRow(50000L)]
    [DataRow(-50000L)]
    public void RechargeNeverReversesWhileDelayedAndDuplicateSnapshotsCorrectTheBudget(long initialError)
    {
        var actual = Create();
        var native = Create();
        actual.laserEnergyCapacity = native.laserEnergyCapacity = 1000000;
        actual.laserEnergy = initialError > 0 ? 70000 : 20000;
        native.laserEnergy = actual.laserEnergy - initialError;
        long now = 100;
        var view = new CombatRecoveryView(() => now);
        var previous = view.GetDisplay(actual).laserEnergy;
        var history = new Dictionary<long, (long Laser, double Core)> { [now] = (native.laserEnergy, native.coreEnergy) };
        var ages = new[] { 0, 3, 1, 6 };
        for (now = 101; now <= 172; now++)
        {
            native.UpdateCombatStats(1.0 / 60);
            history[now] = (native.laserEnergy, native.coreEnergy);
            var before = view.Get(actual).laserEnergy;
            view.Tick(actual, 1.0 / 60);
            var gain = view.Get(actual).laserEnergy - before;
            var shown = view.GetDisplay(actual).laserEnergy;
            TestAssert.IsGreaterThanOrEqualTo(previous, shown, $"Recharge reversed at tick {now}: {previous} -> {shown}.");
            TestAssert.IsGreaterThanOrEqualTo(gain * .5 - 1, shown - previous, "Corrections may consume at most half the recovered energy.");
            TestAssert.IsLessThanOrEqualTo(gain * 1.5 + 1, shown - previous, "Upward corrections must also respect the recharge rate.");
            if ((now - 100) % 6 == 0)
            {
                var hostTick = now - ages[(int)((now - 100) / 6) % ages.Length];
                actual.laserEnergy = history[hostTick].Laser;
                actual.coreEnergy = history[hostTick].Core;
                view.Rebase(actual, hostTick);
                view.Rebase(actual, hostTick);
                TestAssert.AreEqual(native.laserEnergy, view.Get(actual).laserEnergy, "Only the HUD may retain a correction.");
                TestAssert.AreEqual(shown, view.GetDisplay(actual).laserEnergy, "A snapshot must start at the currently rendered value.");
            }
            previous = view.GetDisplay(actual).laserEnergy;
        }
    }

    [TestMethod]
    public void RepeatedSnapshotsDoNotRestartAnUnfinishedCorrection()
    {
        var actual = Create(false);
        actual.laserEnergy = 50000;
        long now = 100;
        var view = new CombatRecoveryView(() => now);
        view.GetDisplay(actual);
        actual.laserEnergy = 30000;
        view.Rebase(actual, 100);
        for (now = 101; now <= 108; now++)
        {
            view.Rebase(actual, 100);
            TestAssert.AreEqual(30000L, view.Get(actual).laserEnergy);
        }
        now = 108;
        TestAssert.AreEqual(30000L, view.GetDisplay(actual).laserEnergy, "Duplicate checkpoints must not postpone convergence.");
    }

    [TestMethod]
    [DataRow("disabled")]
    [DataRow("empty")]
    [DataRow("full")]
    [DataRow("timeout")]
    public void CorrectionConvergesWhenRecoveryCannotContinue(string reason)
    {
        var actual = Create();
        actual.laserEnergy = 50000;
        long now = 100;
        var view = new CombatRecoveryView(() => now);
        view.GetDisplay(actual);
        actual.laserEnergy = reason == "full" ? actual.laserEnergyCapacity : 30000;
        if (reason == "disabled") actual.laserActive = false;
        if (reason == "empty") actual.coreEnergy = 0;
        view.Rebase(actual, 100);
        TestAssert.AreEqual(50000L, view.GetDisplay(actual).laserEnergy);
        if (reason == "timeout")
        {
            now = 221;
            view.Tick(actual, 1.0 / 60);
        }
        else
            for (now = 101; now <= 108; now++) view.Tick(actual, 1.0 / 60);
        TestAssert.AreEqual(view.Get(actual).laserEnergy, view.GetDisplay(actual).laserEnergy);
        TestAssert.AreEqual(actual.laserEnergy, view.Get(actual).laserEnergy, "Display convergence must not create usable energy.");
    }

    [TestMethod]
    public void LowCoreEnergyBoundsCorrectionByTheActualGainThenSettlesAfterRecoveryStops()
    {
        var actual = Create();
        actual.laserEnergy = 50000;
        actual.coreEnergy = 100;
        long now = 100;
        var view = new CombatRecoveryView(() => now);
        view.GetDisplay(actual);
        actual.laserEnergy = 30000;
        view.Rebase(actual, 100);
        now++;
        view.Tick(actual, 1.0 / 60);
        TestAssert.AreEqual(30100L, view.Get(actual).laserEnergy);
        TestAssert.AreEqual(50050L, view.GetDisplay(actual).laserEnergy);
        for (now = 102; now <= 110; now++) view.Tick(actual, 1.0 / 60);
        TestAssert.AreEqual(30100L, view.GetDisplay(actual).laserEnergy);
        TestAssert.AreEqual(100d, actual.coreEnergy, "Predicted recovery must not debit the checkpoint.");
    }

    [TestMethod]
    [DataRow("death")]
    [DataRow("respawn")]
    [DataRow("capacity")]
    [DataRow("zero-capacity")]
    [DataRow("clear")]
    public void PresentationCorrectionsDoNotSurviveLifeOrCapacityChanges(string change)
    {
        var actual = Create(false);
        actual.laserEnergy = 50000;
        if (change == "respawn") { actual.player.isAlive = false; actual.hp = 0; }
        long now = 100;
        var view = new CombatRecoveryView(() => now);
        view.GetDisplay(actual);
        actual.laserEnergy = 30000;
        view.Rebase(actual, 100);
        now = 102;
        actual.laserEnergy = change == "zero-capacity" ? 0 : 10000;
        switch (change)
        {
            case "death": actual.player.isAlive = false; actual.hp = 0; break;
            case "respawn": actual.player.isAlive = true; actual.hp = 1000; break;
            case "capacity": actual.laserEnergyCapacity *= 2; break;
            case "zero-capacity": actual.laserEnergyCapacity = 0; break;
            case "clear": view.Clear(); break;
        }
        view.Rebase(actual, now);
        TestAssert.AreEqual(actual.laserEnergy, view.GetDisplay(actual).laserEnergy);
    }

    [TestMethod]
    public void SpendingAndRefundDuringRechargeKeepTheirImmediateBudgetAndContinuousPresentation()
    {
        var actual = Create();
        actual.laserEnergy = 50000;
        long now = 100;
        var view = new CombatRecoveryView(() => now);
        view.GetDisplay(actual);
        actual.laserEnergy = 30000;
        view.Rebase(actual, now);
        now++;
        view.Tick(actual, 1.0 / 60);
        var shown = view.GetDisplay(actual).laserEnergy;
        var budget = view.Get(actual).laserEnergy;
        view.Spend(actual, 5000, 0, 100, true);
        TestAssert.AreEqual(shown - 5000, view.GetDisplay(actual).laserEnergy);
        TestAssert.AreEqual(budget - 5000, view.Get(actual).laserEnergy);
        view.Refund(actual, 5000, 0, 100);
        TestAssert.AreEqual(shown - 5000, view.GetDisplay(actual).laserEnergy);
        TestAssert.AreEqual(budget, view.Get(actual).laserEnergy);
        TestAssert.AreEqual(30000L, actual.laserEnergy);
    }

    [TestMethod]
    public void CapacityChangeResetsOnlyPresentationAndPreservesTheNativeRechargeLock()
    {
        var actual = Create(false);
        actual.laserRecharging = false;
        long now = 100;
        var view = new CombatRecoveryView(() => now);
        view.Spend(actual, 0, 0, 0, true);
        actual.laserEnergyCapacity *= 2;
        now++;
        view.Rebase(actual, 100);
        TestAssert.IsTrue(view.Get(actual).laserRecharging, "Changing capacity must not unlock a failed native fire attempt.");
        TestAssert.AreEqual(actual.laserEnergy, view.GetDisplay(actual).laserEnergy);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(201)]
    public void RespawnBeforeTheNextHostPacketImmediatelyRestoresHudAndWeaponBudget(int delay)
    {
        var actual = Create();
        actual.player.isAlive = false;
        actual.hp = 0;
        actual.laserEnergy = 0;
        long now = 100;
        var view = new CombatRecoveryView(() => now);
        view.GetDisplay(actual);
        view.Spend(actual, 0, 0, 12345, true);

        now += delay;
        actual.player.isAlive = true;
        actual.Respawn();
        var display = view.GetDisplay(actual); // UI can run before any host reconciliation.
        TestAssert.AreEqual(actual.hpMaxApplied, display.hp);
        TestAssert.IsGreaterThanOrEqualTo(actual.hpMaxApplied * .36, display.hp, "Respawn must not trigger the low-health voice again.");
        TestAssert.AreEqual(actual.coreEnergyCap, display.coreEnergy);
        TestAssert.AreEqual(actual.laserEnergyCapacity, view.Get(actual).laserEnergy);
        TestAssert.AreEqual(actual.laserEnergyCapacity, display.laserEnergy);
        TestAssert.IsFalse(display.laserRecharging);

        view.Rebase(actual, now);
        TestAssert.AreEqual(actual.laserEnergyCapacity, view.GetDisplay(actual).laserEnergy,
            "The first HUD read must not turn the old dead-life bar into a correction for the new life.");
    }

    [TestMethod]
    public void ADeathAndRespawnBetweenHudReadsStillDiscardThePreviousLife()
    {
        var actual = Create();
        actual.hp = 100;
        var view = new CombatRecoveryView(() => 100);
        view.Spend(actual, 1000, 0, 12345, true);
        var oldLife = view.GetLifeVersion(actual);
        actual.player.isAlive = false;
        actual.player.deathCount++;
        actual.Kill();
        actual.player.isAlive = true;
        actual.Respawn();
        TestAssert.AreNotEqual(oldLife, view.GetLifeVersion(actual));
        TestAssert.AreEqual(actual.hpMaxApplied, view.GetDisplay(actual).hp);
        TestAssert.AreEqual(actual.coreEnergyCap, view.GetDisplay(actual).coreEnergy);
        TestAssert.IsFalse(view.Get(actual).laserRecharging);
    }

    [TestMethod]
    public void AHostRebaseAcrossRespawnDiscardsThePreviousLocalRechargeLock()
    {
        var actual = Create(false);
        actual.player.isAlive = false;
        actual.hp = 0;
        var view = new CombatRecoveryView(() => 100);
        view.Spend(actual, 0, 0, 0, true);
        actual.player.isAlive = true;
        actual.Respawn();
        actual.laserEnergy = 30000; // A new-life checkpoint need not still be fully charged.
        view.Rebase(actual, 100);
        TestAssert.IsFalse(view.Get(actual).laserRecharging);
        TestAssert.IsFalse(view.GetDisplay(actual).laserRecharging);
        TestAssert.AreEqual(30000L, view.GetDisplay(actual).laserEnergy);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OldLifeReservationsSettleWithoutSpendingOrRefundingNewLifeEnergy(bool rejected)
    {
        var previousGame = GameMain.data;
        try
        {
            var game = Uninitialized<GameData>();
            var actual = Create(false);
            SetBackingField(actual.player, "mecha", actual);
            SetBackingField(game, "mainPlayer", actual.player);
            GameMain.data = game;
            var resources = new PlayerWeaponResources();
            var oldShot = new PlayerShotId(2, 1, 1);
            using (resources.Enter(actual))
            {
                resources.Reserve(oldShot, EAmmoType.Laser, false, 0);
                actual.coreEnergy -= 100;
                actual.laserEnergy -= 1000;
            }
            actual.player.isAlive = false;
            actual.player.deathCount++;
            actual.Kill();
            actual.player.isAlive = true;
            actual.Respawn();
            resources.Rebase(actual);
            TestAssert.AreEqual(1, resources.PendingCount, "Life changes must keep the pending settlement record.");
            TestAssert.AreEqual(actual.coreEnergyCap, resources.Recovery.Get(actual).coreEnergy);
            TestAssert.AreEqual(actual.laserEnergyCapacity, resources.Recovery.Get(actual).laserEnergy);
            TestAssert.IsFalse(resources.Recovery.Get(actual).laserRecharging);

            var newShot = new PlayerShotId(2, 1, 2);
            using (resources.Enter(actual))
            {
                resources.Reserve(newShot, EAmmoType.Laser, false, 0);
                actual.coreEnergy -= 50;
                actual.laserEnergy -= 500;
            }
            resources.Rebase(actual);
            resources.Rebase(actual);
            var budget = resources.Recovery.Get(actual);
            var laser = budget.laserEnergy;
            var core = budget.coreEnergy;
            TestAssert.AreEqual(actual.laserEnergyCapacity - 500, laser);
            TestAssert.AreEqual(actual.coreEnergyCap - 50, core);
            if (rejected) { resources.Reject(oldShot); resources.Reject(oldShot); }
            else { resources.Settle(oldShot, 1); resources.Applied(1); resources.Settle(oldShot, 1); }
            TestAssert.AreEqual(laser, resources.Recovery.Get(actual).laserEnergy, "A late old-life rejection must not refund a new-life shot.");
            TestAssert.AreEqual(core, resources.Recovery.Get(actual).coreEnergy);
            resources.Settle(newShot, 2);
            resources.Applied(2);
            TestAssert.AreEqual(0, resources.PendingCount);
        }
        finally { GameMain.data = previousGame; }
    }

    [TestMethod]
    [DataRow(-40)]
    [DataRow(-20)]
    [DataRow(-10)]
    [DataRow(10)]
    [DataRow(20)]
    [DataRow(40)]
    public void ShieldHitsAreReplayedOnceAndRetiredByTheirCheckpoint(int offset)
    {
        var actual = Create(false);
        actual.energyShieldCapacity = actual.energyShieldEnergy = 10000000;
        actual.energyShieldRechargeEnabled = false;
        actual.energyShieldRadius = 10;
        actual.energyShieldEnergyRate = 500;
        long now = 100;
        var view = new CombatRecoveryView(() => now);
        var history = new Dictionary<long, long> { [now] = actual.energyShieldEnergy };
        var costs = new List<(long Tick, long Cost)>();
        for (now = 101; now <= 160; now++)
        {
            var before = view.Get(actual).energyShieldEnergy;
            var damage = 3;
            view.Absorb(actual, ref damage);
            costs.Add((now, before - view.Get(actual).energyShieldEnergy));
            history[now] = history[now - 1] - costs.Last().Cost;
        }
        var baseline = now - 1 + offset;
        actual.energyShieldEnergy = offset > 0 ? history[160] : history[baseline];
        var expected = actual.energyShieldEnergy - costs.Where(x => x.Tick > baseline).Sum(x => x.Cost);
        view.Rebase(actual, baseline);
        view.Rebase(actual, baseline);
        TestAssert.AreEqual(expected, view.Get(actual).energyShieldEnergy);
        TestAssert.AreEqual(offset > 0 ? history[160] : history[baseline], actual.energyShieldEnergy);
        TestAssert.IsTrue(costs.All(x => x.Cost > 0), "The fixture must actually absorb damage.");
    }

    [TestMethod]
    public void ShieldCorrectionsAffectOnlyTheHudAndDoNotRestartOnDuplicateCheckpoints()
    {
        var actual = Create(false); actual.energyShieldCapacity = 100000;
        actual.energyShieldEnergy = 80000; actual.energyShieldRechargeEnabled = false;
        long now = 100; var view = new CombatRecoveryView(() => now);
        TestAssert.AreEqual(80000L, view.GetDisplay(actual).energyShieldEnergy);
        actual.energyShieldEnergy = 40000;
        view.Rebase(actual, 100);
        TestAssert.AreEqual(80000L, view.GetDisplay(actual).energyShieldEnergy);
        TestAssert.AreEqual(40000L, view.Get(actual).energyShieldEnergy);
        for (now = 101; now <= 108; now++)
        {
            view.Rebase(actual, now);
            TestAssert.AreEqual(80000L - (now - 100) * 5000, view.GetDisplay(actual).energyShieldEnergy);
            TestAssert.AreEqual(40000L, actual.energyShieldEnergy);
        }
        view.Spend(actual, 0, 10000, 0);
        TestAssert.AreEqual(30000L, view.GetDisplay(actual).energyShieldEnergy);
        actual.energyShieldEnergy = 10000; view.Rebase(actual, now);
        actual.energyShieldCapacity = 200000;
        TestAssert.AreEqual(10000L, view.GetDisplay(actual).energyShieldEnergy, "A new shield capacity clears the old residual.");
        view.Clear(); actual.energyShieldEnergy = 90000;
        TestAssert.AreEqual(90000L, view.GetDisplay(actual).energyShieldEnergy);
    }

    [TestMethod]
    public void FutureCheckpointsDoNotRecoverAgainBeforeTheirOwnTick()
    {
        var actual = Create(); long now = 100;
        var view = new CombatRecoveryView(() => now); view.Rebase(actual, 120);
        for (now = 101; now <= 120; now++) view.Tick(actual, 1.0 / 60);
        TestAssert.AreEqual(actual.laserEnergy, view.Get(actual).laserEnergy);
        view.Tick(actual, 1.0 / 60);
        TestAssert.IsGreaterThan(actual.laserEnergy, view.Get(actual).laserEnergy);
    }

    [TestMethod]
    [DataRow(50000L)]
    [DataRow(-50000L)]
    public void ShieldRecoveryRemainsContinuousAcrossDelayedCheckpointCorrections(long initialError)
    {
        var actual = Create(false); var native = Create(false);
        actual.energyShieldCapacity = native.energyShieldCapacity = 1000000;
        actual.energyShieldRechargeEnabled = native.energyShieldRechargeEnabled = true;
        actual.energyShieldRechargeSpeed = native.energyShieldRechargeSpeed = 1;
        actual.energyShieldEnergy = initialError > 0 ? 70000 : 20000;
        native.energyShieldEnergy = actual.energyShieldEnergy - initialError;
        long now = 100; var view = new CombatRecoveryView(() => now);
        var previous = view.GetDisplay(actual).energyShieldEnergy;
        var history = new Dictionary<long, (long Shield, double Core)> { [now] = (native.energyShieldEnergy, native.coreEnergy) };
        for (now = 101; now <= 172; now++)
        {
            native.UpdateCombatStats(1.0 / 60); history[now] = (native.energyShieldEnergy, native.coreEnergy);
            view.Tick(actual, 1.0 / 60);
            var shown = view.GetDisplay(actual).energyShieldEnergy;
            TestAssert.IsGreaterThanOrEqualTo(previous, shown, "Checkpoint correction must not reverse ongoing recovery.");
            if ((now - 100) % 6 == 0)
            {
                var baseline = now - 3;
                actual.energyShieldEnergy = history[baseline].Shield; actual.coreEnergy = history[baseline].Core;
                view.Rebase(actual, baseline); view.Rebase(actual, baseline);
                TestAssert.AreEqual(native.energyShieldEnergy, view.Get(actual).energyShieldEnergy);
                TestAssert.AreEqual(shown, view.GetDisplay(actual).energyShieldEnergy);
            }
            previous = view.GetDisplay(actual).energyShieldEnergy;
        }
    }

    [TestMethod]
    public void LocalShieldExhaustionIsImmediateAndRespawnDropsTheOldDisplayResidual()
    {
        var actual = Create(false); actual.energyShieldCapacity = 100000; actual.energyShieldEnergyRate = 500;
        actual.energyShieldEnergy = 80000; long now = 100; var view = new CombatRecoveryView(() => now);
        view.GetDisplay(actual); actual.energyShieldEnergy = 20000; view.Rebase(actual, 100);
        var damage = 50; view.Absorb(actual, ref damage);
        TestAssert.AreEqual(0L, view.GetDisplay(actual).energyShieldEnergy);
        TestAssert.AreEqual(20000L, actual.energyShieldEnergy);
        actual.player.isAlive = false; actual.hp = 0; view.GetDisplay(actual);
        actual.player.deathCount++; actual.player.isAlive = true; actual.hp = 1000; actual.energyShieldEnergy = 90000;
        TestAssert.AreEqual(90000L, view.GetDisplay(actual).energyShieldEnergy);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SinglePlayerAndHostHudContinueReadingNativeMechaValues(bool host)
    {
        var previousSession = Multiplayer.Session;
        try
        {
            Multiplayer.Session = null;
            if (host)
            {
                var session = Uninitialized<MultiplayerSession>();
                session.Server = Uninitialized<global::NebulaeNetwork.Server>();
                Multiplayer.Session = session;
            }
            var actual = Create();
            actual.UpdateCombatStats(1.0 / 60);
            TestAssert.AreSame(CombatRecoveryView.Display(actual), actual, "Native HUD paths must not create a predicted display budget.");
        }
        finally { Multiplayer.Session = previousSession; }
    }
}
