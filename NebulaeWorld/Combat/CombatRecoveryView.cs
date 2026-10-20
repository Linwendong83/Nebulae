using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Linq;

namespace NebulaeWorld.Combat;

/// <summary>Disposable combat values for prediction and HUD. Never a checkpoint source.</summary>
internal sealed class CombatRecoveryView
{
    private static readonly MethodInfo clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
    private readonly ConcurrentDictionary<Mecha, Entry> entries = new();
    private readonly Func<long> clock;
    public CombatRecoveryView(Func<long> clock = null) { this.clock = clock ?? (() => GameMain.gameTick); }
    private long Now => clock();
    private readonly Dictionary<Mecha, List<(long Tick, long Cost, int Cooldown)>> hits = new();
    private const int PredictionHorizon = 120;
    private const int PresentationCorrectionTicks = 8;
    internal readonly struct Expenditure
    {
        public readonly long Tick, Laser, Shield;
        public readonly double Core;
        public readonly bool? Recharging;
        public Expenditure(long tick, long laser, long shield, double core, bool? recharging)
        { Tick = tick; Laser = laser; Shield = shield; Core = core; Recharging = recharging; }
    }
    private sealed class Entry
    {
        public Mecha Value;
        public Mecha Presentation;
        public double CoreSpent;
        public long Tick, Baseline;
        public double LaserCorrection, CorrectionPerTick;
        public long CorrectionTick, ChargingSince, LaserCapacity;
        public long? LocalRechargeTick;
        public bool DisplayCharging, ChargingTarget, Alive;
    }
    public Mecha Get(Mecha actual)
    {
        if (!entries.TryGetValue(actual, out var entry))
        {
            entry = new Entry { Value = (Mecha)clone.Invoke(actual, null), Tick = Now, Baseline = Now };
            entry.Value.energyShieldResistHistory = actual.energyShieldResistHistory == null ? new long[60] : (long[])actual.energyShieldResistHistory.Clone();
            ResetPresentation(entry, actual, Now);
            entry = entries.GetOrAdd(actual, entry);
        }
        if (entry.LaserCapacity != actual.laserEnergyCapacity || entry.Alive != Alive(actual))
            ResetPresentation(entry, actual, Now);
        entry.Value.coreEnergy = Math.Max(0, actual.coreEnergy - entry.CoreSpent);
        entry.Value.energyShieldBurstProgress = actual.energyShieldBurstProgress;
        return entry.Value;
    }
    private static bool Alive(Mecha actual) => actual.player?.isAlive == true && actual.hp > 0;
    private static void ResetPresentation(Entry entry, Mecha actual, long now)
    {
        entry.LaserCapacity = actual.laserEnergyCapacity;
        entry.Alive = Alive(actual);
        entry.Presentation = null;
        entry.LaserCorrection = entry.CorrectionPerTick = 0;
        entry.CorrectionTick = entry.ChargingSince = now;
        entry.DisplayCharging = entry.ChargingTarget = entry.Value.laserRecharging;
    }
    private static void CorrectLaser(Entry entry, double correction, long now, Entry previous)
    {
        var rate = Math.Abs(correction) / PresentationCorrectionTicks;
        // Repeated checkpoints and settlements must not restart the same residual's window.
        if (correction != 0 && previous != null && Math.Sign(correction) == Math.Sign(previous.LaserCorrection))
            rate = Math.Max(rate, previous.CorrectionPerTick);
        entry.LaserCorrection = correction;
        entry.CorrectionPerTick = rate;
        entry.CorrectionTick = now;
    }
    private static void AdvanceCorrection(Entry entry, long now, long laserGain = 0)
    {
        var amount = Math.Min(Math.Abs(entry.LaserCorrection), entry.CorrectionPerTick * Math.Max(0, now - entry.CorrectionTick));
        // A fixed-duration offset can decay faster than the weapon recovers and reverse the bar.
        // Spend at most half of this tick's real recovery on either direction of HUD correction.
        if (laserGain > 0) amount = Math.Min(amount, laserGain * .5);
        entry.LaserCorrection -= Math.Sign(entry.LaserCorrection) * amount;
        if (entry.LaserCorrection == 0) entry.CorrectionPerTick = 0;
        entry.CorrectionTick = now;
    }
    private static long PresentedLaser(Entry entry, Mecha actual, long now)
    {
        // While recovery is possible only Tick may retire the offset, using the gain it actually
        // produced. Repeated HUD reads cannot spend the correction budget ahead of that tick.
        if (!actual.laserActive || !Alive(actual) || entry.Value.laserEnergy >= actual.laserEnergyCapacity ||
            actual.coreEnergy <= entry.CoreSpent || now - entry.Baseline > PredictionHorizon)
            AdvanceCorrection(entry, now);
        return (long)Math.Max(0, Math.Min(actual.laserEnergyCapacity, entry.Value.laserEnergy + entry.LaserCorrection));
    }
    private static bool PresentedCharging(Entry entry, long now)
    {
        if (now - entry.ChargingSince >= PresentationCorrectionTicks) entry.DisplayCharging = entry.ChargingTarget;
        return entry.DisplayCharging;
    }
    public Mecha GetDisplay(Mecha actual)
    {
        var value = Get(actual);
        var entry = entries[actual];
        var display = entry.Presentation ??= (Mecha)clone.Invoke(value, null);
        display.hp = value.hp;
        display.hpRecoverCD = value.hpRecoverCD;
        display.coreEnergy = value.coreEnergy;
        display.energyShieldEnergy = value.energyShieldEnergy;
        display.energyShieldRecoverCD = value.energyShieldRecoverCD;
        display.energyShieldRechargeCurrent = value.energyShieldRechargeCurrent;
        display.energyShieldResistHistory = value.energyShieldResistHistory;
        display.energyShieldBurstProgress = value.energyShieldBurstProgress;
        display.laserEnergyCapacity = actual.laserEnergyCapacity;
        display.laserEnergy = PresentedLaser(entry, actual, Now);
        display.laserRecharging = PresentedCharging(entry, Now);
        return display;
    }
    public void Rebase(Mecha actual, long? hostTick = null, IEnumerable<Expenditure> expenditures = null)
    {
        var now = Now;
        var baseline = hostTick ?? now;
        entries.TryGetValue(actual, out var previous);
        var continuous = previous != null && previous.LaserCapacity == actual.laserEnergyCapacity && previous.Alive == Alive(actual);
        var shownLaser = continuous ? PresentedLaser(previous, actual, now) : 0;
        var shownCharging = continuous && PresentedCharging(previous, now);
        var localRecharge = previous?.LocalRechargeTick;
        entries.TryRemove(actual, out _);
        Get(actual);
        var entry = entries[actual];
        entry.Baseline = baseline;
        // A checkpoint describes the end of its host tick, not the time it arrived.
        // Replay recovery and unacknowledged actions on that same timeline.
        var spends = (expenditures ?? Enumerable.Empty<Expenditure>()).OrderBy(x => x.Tick).ToArray();
        var damage = hits.TryGetValue(actual, out var pending) ? pending : new List<(long Tick, long Cost, int Cooldown)>();
        damage.RemoveAll(x => x.Tick <= baseline || now - x.Tick > PredictionHorizon);
        var spendIndex = 0;
        var damageIndex = 0;
        var localRechargeApplied = false;
        void ApplyThrough(long tick)
        {
            while (spendIndex < spends.Length && spends[spendIndex].Tick <= tick)
            {
                var spend = spends[spendIndex++];
                Spend(actual, spend.Laser, spend.Shield, spend.Core);
                if (spend.Recharging.HasValue) entry.Value.laserRecharging = spend.Recharging.Value;
            }
            while (damageIndex < damage.Count && damage[damageIndex].Tick <= tick)
            {
                var hit = damage[damageIndex++];
                entry.Value.energyShieldEnergy = Math.Max(0, entry.Value.energyShieldEnergy - hit.Cost);
                entry.Value.energyShieldRecoverCD = Math.Max(entry.Value.energyShieldRecoverCD, hit.Cooldown);
            }
            // A failed local fire attempt sends no shot to the host. Its native recharge
            // lock must survive host snapshots that therefore still say "not charging".
            if (!localRechargeApplied && localRecharge.HasValue && localRecharge.Value <= tick)
            {
                entry.Value.laserRecharging = true;
                entry.LocalRechargeTick = localRecharge;
                localRechargeApplied = true;
            }
        }
        ApplyThrough(baseline);
        if (now - baseline <= PredictionHorizon)
            for (var tick = baseline + 1; tick <= now; tick++)
            {
                Recover(actual, entry, 1.0 / 60);
                ApplyThrough(tick);
            }
        // Old checkpoints may reconcile costs, but must not create recovery beyond the horizon.
        ApplyThrough(long.MaxValue);
        if (actual.laserEnergyCapacity > 0 && entry.Value.laserEnergy >= actual.laserEnergyCapacity)
        {
            entry.Value.laserRecharging = false;
            entry.LocalRechargeTick = null;
        }
        entry.Tick = now;
        if (continuous && hostTick.HasValue && now - baseline <= PredictionHorizon && Alive(actual))
        {
            // Corrections belong to presentation only. Native firing always reads Value.
            // Local spending changes Value immediately and retains the remaining offset.
            CorrectLaser(entry, shownLaser - entry.Value.laserEnergy, now, previous);
            entry.DisplayCharging = shownCharging;
            entry.ChargingTarget = entry.Value.laserRecharging;
            entry.ChargingSince = previous.ChargingTarget == entry.ChargingTarget ? previous.ChargingSince : now;
        }
    }
    public bool Absorb(Mecha actual, ref int damage)
    {
        var view = Get(actual); var before = view.energyShieldEnergy;
        var result = view.EnergyShieldResist(ref damage);
        if (before > view.energyShieldEnergy)
        {
            if (!hits.TryGetValue(actual, out var pending)) hits[actual] = pending = new();
            pending.RemoveAll(x => Now - x.Tick > PredictionHorizon);
            pending.Add((Now, before - view.energyShieldEnergy, view.energyShieldRecoverCD));
        }
        return result;
    }
    public void Spend(Mecha actual, long laser, long shield, double core, bool? charging = null)
    {
        var view = Get(actual);
        if (laser != 0) view.laserEnergy = Math.Max(0, Math.Min(actual.laserEnergyCapacity, view.laserEnergy - laser));
        if (shield != 0) view.energyShieldEnergy = Math.Max(0, Math.Min(actual.energyShieldCapacity, view.energyShieldEnergy - shield));
        if (charging.HasValue)
        {
            view.laserRecharging = charging.Value;
            entries[actual].DisplayCharging = entries[actual].ChargingTarget = charging.Value;
            entries[actual].ChargingSince = Now;
            entries[actual].LocalRechargeTick = charging.Value ? Now : null;
        }
        entries[actual].CoreSpent = Math.Max(0, entries[actual].CoreSpent + core);
    }
    public void Refund(Mecha actual, long laser, long shield, double core)
    {
        Get(actual);
        var entry = entries[actual];
        var shown = PresentedLaser(entry, actual, Now);
        PresentedCharging(entry, Now);
        Spend(actual, -laser, -shield, -core);
        if (laser == 0) return;
        // A rejection is a network correction, not a new local firing action.
        // Restore its usable budget now while keeping the rendered bar continuous.
        if (entry.Value.laserEnergy >= actual.laserEnergyCapacity)
        {
            entry.Value.laserRecharging = false;
            entry.LocalRechargeTick = null;
        }
        CorrectLaser(entry, shown - entry.Value.laserEnergy, Now, entry);
        if (entry.ChargingTarget != entry.Value.laserRecharging) entry.ChargingSince = Now;
        entry.ChargingTarget = entry.Value.laserRecharging;
    }
    public void Tick(Mecha actual, double dt)
    {
        Get(actual);
        var e = entries[actual];
        if (e.Tick == Now) return;
        e.Tick = Now;
        // A lost connection must not manufacture unlimited usable shield/laser energy.
        if (Now - e.Baseline > PredictionHorizon)
        {
            AdvanceCorrection(e, Now);
            return;
        }
        var charging = e.Value.laserRecharging;
        var laser = e.Value.laserEnergy;
        Recover(actual, e, dt);
        AdvanceCorrection(e, Now, Math.Max(0, e.Value.laserEnergy - laser));
        if (charging != e.Value.laserRecharging)
        {
            e.DisplayCharging = e.ChargingTarget = e.Value.laserRecharging;
            e.ChargingSince = Now;
        }
    }
    private static void Recover(Mecha actual, Entry e, double dt)
    {
        if (!actual.player.isAlive || actual.hp <= 0) return;
        var m = e.Value;
        m.coreEnergy = Math.Max(0, actual.coreEnergy - e.CoreSpent);
        var before = m.coreEnergy;
        if (m.hpRecoverCD > 0) m.hpRecoverCD--;
        else m.hp = Math.Min(actual.hpMaxApplied, m.hp + actual.hpRecover);
        var ratio = actual.coreEnergyCap > 0 ? m.coreEnergy / actual.coreEnergyCap : 0;
        m.energyShieldRechargeCurrent = 0;
        if (m.energyShieldRecoverCD > 0) m.energyShieldRecoverCD--;
        else if (actual.energyShieldCapacity > 0 && actual.energyShieldRechargeEnabled)
        {
            var potential = actual.reactorPowerForWeaponEnhanced * dt * ratio * actual.energyShieldRechargeSpeed;
            var gain = (long)(Math.Max(0, Math.Min(potential, Math.Min(actual.energyShieldCapacity - m.energyShieldEnergy, m.coreEnergy))) + .01);
            m.coreEnergy -= gain; m.energyShieldEnergy += gain;
            m.energyShieldRechargeCurrent = (long)(Math.Min(potential, m.coreEnergy) + .01) * 60;
        }
        m.ScrollShieldResistHistory();
        if (actual.laserActive)
        {
            var gain = (long)(Math.Max(0, Math.Min(actual.reactorPowerForWeaponEnhanced * dt * Math.Min(1, ratio * .5 + .7) * 1.8,
                Math.Min(actual.laserEnergyCapacity - m.laserEnergy, m.coreEnergy))) + .01);
            m.coreEnergy -= gain; m.laserEnergy += gain;
        }
        if (m.laserEnergy == actual.laserEnergyCapacity)
        {
            m.laserRecharging = false;
            e.LocalRechargeTick = null;
        }
        e.CoreSpent += Math.Max(0, before - m.coreEnergy);
    }
    public void Clear() { entries.Clear(); hits.Clear(); }
    public static Mecha Display(Mecha mecha) => mecha != null && Multiplayer.IsActive && Multiplayer.Session.IsClient &&
        Multiplayer.Session.Authority.IsActive && !PlayerWeaponResources.Active
            ? Multiplayer.Session.PlayerWeapons.Resources.Recovery.GetDisplay(mecha) : mecha;
}
