using System;
using NebulaeModel.DataStructures;

namespace NebulaeWorld.Combat;

/// <summary>Presentation time in host ticks; it never advances the combat simulation.</summary>
internal sealed class BattleVisualClock
{
    private long simulationTick = -1;
    private double sampledAt, sampled;
    public static double NormalizeUps(double ups) => double.IsNaN(ups) || double.IsInfinity(ups) || ups <= 0 ? 60 : ups;
    public double Sample(long tick, double now, double ups, bool paused = false)
    {
        // The shared game clock advances even when this effect sends no further packets. Interpolate
        // at most one tick; joining pauses, stalls and UPS changes cannot accumulate wall-clock drift.
        if (tick != simulationTick)
        {
            simulationTick = tick;
            sampledAt = now;
        }
        var fraction = paused ? 0 : Math.Min(1, Math.Max(0, now - sampledAt) * NormalizeUps(ups));
        sampled = Math.Max(sampled, tick + fraction);
        return sampled;
    }
}

internal sealed class BattleEffectTimeline
{
    public const double DelayTicks = 6;
    public const double MaxExtrapolateTicks = 12;
    public long SampleTick { get; private set; } = -1;
    public long BornTick { get; private set; }
    public long PreviousTick { get; private set; }
    public double ExpiresAt { get; private set; }
    public bool HasPrevious { get; private set; }
    public bool Flash { get; private set; }
    private bool localTrajectory, continuous;
    public long? DespawnTick { get; private set; }
    private double flashBornAt;

    public bool Observe(BattleEffectKind kind, long tick, int life, double receiptTick)
    {
        if (tick <= SampleTick) return false;
        Flash = BattleEffectData.IsOneShotLaser(kind);
        localTrajectory = BattleEffectData.SimulatesLocally(kind);
        continuous = kind == BattleEffectKind.TurretLaser;
        if (SampleTick >= 0) { PreviousTick = SampleTick; HasPrevious = true; }
        else
        {
            BornTick = tick;
            if (Flash)
            {
                ExpiresAt = receiptTick + life;
                flashBornAt = receiptTick;
            }
        }
        SampleTick = tick;
        DespawnTick = null;
        if (!Flash) ExpiresAt = continuous ? double.PositiveInfinity : tick + life;
        return true;
    }
    public double RenderTick(double clockTick) => Flash ? clockTick : clockTick - DelayTicks;
    public double Age(double renderTick) => Math.Min(localTrajectory ? double.PositiveInfinity : MaxExtrapolateTicks,
        Math.Max(0, renderTick - (Flash ? flashBornAt : SampleTick)));
    public float Blend(double renderTick) => !HasPrevious || renderTick >= SampleTick ? 1 :
        (float)Math.Max(0, (renderTick - PreviousTick) / (SampleTick - PreviousTick));
    public int Remaining(double renderTick) => continuous ? 360000 : (int)Math.Max(0, Math.Ceiling(ExpiresAt - renderTick));
    public void Despawn(long tick) => DespawnTick = DespawnTick.HasValue ? Math.Min(DespawnTick.Value, tick) : tick;
    public bool IsBorn(double renderTick) => Flash || renderTick >= BornTick;
    public bool IsDespawned(double renderTick) => !Flash && DespawnTick.HasValue && renderTick >= DespawnTick.Value;
}
