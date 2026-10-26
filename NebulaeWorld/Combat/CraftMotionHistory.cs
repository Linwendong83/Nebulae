using System;

namespace NebulaeWorld.Combat;

// Compare a delayed checkpoint with the prediction from the same simulation tick.
// Correct only the error, not all movement since the host took that checkpoint.
internal sealed class CraftMotionHistory
{
    private struct Sample
    {
        public bool Valid;
        public long Tick;
        public VectorLF3 Position, Correction;
    }
    private readonly Sample[] samples = new Sample[256];
    private VectorLF3 correction, applied;
    private int steps;
    private long lastTick = -1, lastStep = -1;

    public void Record(long tick, VectorLF3 position)
    {
        if (tick < 0 || !Finite(position)) return;
        if (tick < lastTick) Reset();
        samples[(int)(tick % samples.Length)] = new Sample { Valid = true, Tick = tick, Position = position, Correction = applied };
        lastTick = tick;
    }

    public bool Reconcile(long tick, VectorLF3 authoritative, VectorLF3 current, double maximumError)
    {
        if (tick < 0 || !Finite(current) || !Finite(authoritative)) return false;
        ref var sample = ref samples[(int)(tick % samples.Length)];
        if (!sample.Valid || sample.Tick != tick) return false;
        var error = authoritative - sample.Position - (applied - sample.Correction);
        if (!Finite(error) || error.sqrMagnitude > maximumError * maximumError) return false;
        correction = error;
        steps = 12;
        return true;
    }

    public VectorLF3 Advance(long tick)
    {
        if (tick <= lastStep || steps == 0) return VectorLF3.zero;
        lastStep = tick;
        var delta = correction / steps;
        correction -= delta; applied += delta; steps--;
        return delta;
    }

    public void Reset()
    {
        Array.Clear(samples, 0, samples.Length);
        correction = applied = VectorLF3.zero;
        lastTick = lastStep = -1; steps = 0;
    }

    private static bool Finite(VectorLF3 value) =>
        !double.IsNaN(value.x) && !double.IsNaN(value.y) && !double.IsNaN(value.z) &&
        !double.IsInfinity(value.x) && !double.IsInfinity(value.y) && !double.IsInfinity(value.z);
}
