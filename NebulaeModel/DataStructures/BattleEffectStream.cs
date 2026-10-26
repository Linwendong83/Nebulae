using System;
using System.Collections.Generic;

namespace NebulaeModel.DataStructures;

/// <summary>Tracks live native skills, emitting launches, required corrections and explicit ends.</summary>
internal sealed class BattleEffectStream
{
    public const int CorrectionIntervalTicks = 6;
    private readonly Dictionary<(BattleEffectKind Kind, int Id), Entry> active = new();
    private readonly List<(BattleEffectKind Kind, int Id)> removed = new();
    private long generation;
    public readonly List<(int Star, BattleEffectData Effect)> Changes = new();
    public int ActiveCount => active.Count;

    private sealed class Entry
    {
        public int Star, NativeLife;
        public long SeenTick, CapturedTick = -1, Generation;
        public BattleEffectData Sample;
    }

    public void BeginFrame() => Changes.Clear();

    public bool Observe(BattleEffectKind kind, int id, int star, int nativeLife, long tick, out long identity)
    {
        var key = (kind, id);
        var exists = active.TryGetValue(key, out var entry);
        var risingClock = kind is BattleEffectKind.TurretMissile or BattleEffectKind.MechaMissile or BattleEffectKind.TurretLaser;
        var phaseFlag = kind is BattleEffectKind.TurretCannon or BattleEffectKind.MechaLocalCannon or
            BattleEffectKind.MechaSpaceCannon or BattleEffectKind.TurretDisturb;
        var phaseChanged = exists && (phaseFlag && nativeLife != entry.NativeLife ||
            risingClock && Math.Sign(nativeLife) != Math.Sign(entry.NativeLife));
        var reused = exists && (entry.Star != star || entry.SeenTick < tick - 1 ||
            (risingClock ? nativeLife > 0 && (entry.NativeLife <= 0 || nativeLife < entry.NativeLife) :
                phaseFlag ? nativeLife > entry.NativeLife :
                Math.Abs((long)nativeLife) >= Math.Abs((long)entry.NativeLife)));
        if (!exists || reused)
        {
            if (exists) End(key, entry, tick);
            active[key] = entry = new Entry { Star = star, Generation = ++generation };
        }
        entry.SeenTick = tick;
        entry.NativeLife = nativeLife;
        identity = entry.Generation;
        return entry.Sample == null || phaseChanged ||
            !BattleEffectData.SimulatesLocally(kind) && tick - entry.CapturedTick >= CorrectionIntervalTicks;
    }

    public void Capture(BattleEffectData effect) =>
        Capture(effect.Kind, effect.Id, effect.Generation, effect.Tick, effect.Life,
            effect.SourceAstroId, effect.Payload, effect.Payload.Length, effect.SourceObjectId, effect);

    /// <summary>
    /// Captures an effect whose payload the caller exported into a buffer it owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host exports every observed effect into one reused buffer, so the payload is copied only
    /// when the effect is actually published. A suppressed effect — a stationary continuous beam —
    /// therefore costs an export and a comparison but no record and no owned array, which is what
    /// makes the reuse possible in the first place: retaining a reference to a buffer that is about
    /// to be overwritten would corrupt every later effect.
    /// </para>
    /// <para>
    /// The captured tick advances before the suppression test, exactly as it did when the decision
    /// lived after the export. Skipping that would leave the correction clock where it was, and the
    /// stream would re-observe the beam every tick instead of every interval.
    /// </para>
    /// </remarks>
    public void Capture(BattleEffectKind kind, int id, long generation, long tick, int life,
        int sourceAstroId, byte[] payload, int length, int sourceObjectId = 0, BattleEffectData launch = null)
    {
        if (!active.TryGetValue((kind, id), out var entry) || entry.Generation != generation)
            throw new InvalidOperationException("Cannot capture a retired combat effect.");
        entry.CapturedTick = tick;
        // A stationary beam does not need a heartbeat payload or a renewed display lease.
        if (kind == BattleEffectKind.TurretLaser && entry.Sample != null &&
            SameBytes(entry.Sample.Payload, payload, length)) return;
        var effect = new BattleEffectData
        {
            Kind = kind,
            Id = id,
            Generation = generation,
            Tick = tick,
            SourceAstroId = sourceAstroId,
            SourceObjectId = sourceObjectId,
            SourceGeneration = launch?.SourceGeneration ?? 0,
            TargetGeneration = launch?.TargetGeneration ?? 0,
            LaunchTick = launch?.LaunchTick ?? -1,
            SourcePort = launch?.SourcePort ?? -1,
            MuzzleIndex = launch?.MuzzleIndex ?? -1,
            Life = life,
            // A fresh array, never the caller's buffer: the copy is what makes the reuse safe.
            Payload = CopyOf(payload, length)
        };
        entry.Sample = effect;
        Changes.Add((entry.Star, effect));
    }

    public void EndFrame(long tick)
    {
        removed.Clear();
        foreach (var pair in active)
            if (pair.Value.SeenTick != tick) removed.Add(pair.Key);
        foreach (var key in removed)
        {
            End(key, active[key], tick);
            active.Remove(key);
        }
    }

    private void End((BattleEffectKind Kind, int Id) key, Entry entry, long tick)
    {
        if (entry.Sample == null) return;
        Changes.Add((entry.Star, new BattleEffectData
        {
            Kind = key.Kind,
            Id = key.Id,
            Generation = entry.Generation,
            Tick = tick,
            End = true,
            SourceAstroId = entry.Sample.SourceAstroId,
            SourceObjectId = entry.Sample.SourceObjectId,
            SourceGeneration = entry.Sample.SourceGeneration,
            TargetGeneration = entry.Sample.TargetGeneration,
            LaunchTick = entry.Sample.LaunchTick,
            SourcePort = entry.Sample.SourcePort,
            MuzzleIndex = entry.Sample.MuzzleIndex,
            Payload = Array.Empty<byte>()
        }));
    }

    public IEnumerable<BattleEffectData> Snapshot(int star, long tick)
    {
        foreach (var entry in active.Values)
        {
            var sample = entry.Sample;
            if (entry.Star == star && sample != null &&
                (sample.Kind == BattleEffectKind.TurretLaser || sample.Tick + sample.Life > tick)) yield return sample;
        }
    }

    /// <summary>
    /// Copies the first <paramref name="length"/> bytes of a caller-owned buffer into a new array.
    /// </summary>
    /// <remarks>
    /// Written out rather than expressed with a range indexer because the project targets net472,
    /// which has no <see cref="System.Range"/>. The copy is the point: the caller reuses its buffer
    /// for the next effect, so a retained payload cannot be a view of it.
    /// </remarks>
    private static byte[] CopyOf(byte[] source, int length)
    {
        var copy = new byte[length];
        Buffer.BlockCopy(source, 0, copy, 0, length);
        return copy;
    }

    private static bool SameBytes(byte[] a, byte[] b, int length)
    {
        if (a.Length != length) return false;
        for (var i = 0; i < length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    public void Clear() { active.Clear(); Changes.Clear(); removed.Clear(); }
}
