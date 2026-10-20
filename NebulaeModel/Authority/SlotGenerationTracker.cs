#region

using System;
using System.Collections.Generic;

#endregion

namespace NebulaeModel.Authority;

/// <summary>
/// Host-side generation bookkeeping for one pool's slots.
/// </summary>
/// <remarks>
/// <para>
/// Vanilla pools recycle slots without a generation: when entity 7 is dismantled and a new building
/// takes slot 7, every stale reference to "entity 7" now names the new object. The authority
/// protocol cannot tolerate that — <c>ObjectKey.Generation</c> is what makes a recycled slot a new
/// identity instead of a resurrection — so the adapter mints generations itself. This tracker is
/// that mint, as a pure model type so the reuse rule is testable without a game process.
/// </para>
/// <para>
/// The rule is scan-shaped because the replicator's read is scan-shaped: the adapter reports every
/// occupied slot of one scan via <see cref="ObserveOccupied"/>, then closes the scan with
/// <see cref="EndScan"/>. A slot that was alive in the previous scan but is absent now is dead, and
/// its generation is retired — the next object to take the slot gets generation+1, never the dead
/// object's identity. A slot observed again within one scan is the same live object and returns the
/// same generation.
/// </para>
/// </remarks>
internal sealed class SlotGenerationTracker
{
    private readonly object gate = new();
    private readonly Dictionary<int, long> generations = [];
    private readonly HashSet<int> alive = [];
    private readonly HashSet<int> scanned = [];

    /// <summary>Slots this tracker has ever minted a generation for (alive and retired).</summary>
    public int TrackedSlots
    {
        get
        {
            lock (gate)
            {
                return generations.Count;
            }
        }
    }

    /// <summary>Slots observed in the current or most recent completed scan.</summary>
    public int AliveSlots
    {
        get
        {
            lock (gate)
            {
                return alive.Count;
            }
        }
    }

    /// <summary>
    /// Reports one occupied slot of the current scan and returns its generation.
    /// </summary>
    /// <remarks>
    /// The first sighting of a slot mints generation 1. A sighting of a slot that died in an
    /// earlier scan mints the next generation — that slot reuse is exactly the case the generation
    /// exists to distinguish. Observing the same slot twice in one scan is the same live object,
    /// not a reuse.
    /// </remarks>
    public long ObserveOccupied(int slot)
    {
        if (slot <= 0) throw new ArgumentException("A pool slot is 1-based.", nameof(slot));
        lock (gate)
        {
            scanned.Add(slot);
            if (alive.Contains(slot))
            {
                return generations[slot];
            }
            var generation = generations.TryGetValue(slot, out var previous) ? previous + 1 : 1;
            generations[slot] = generation;
            alive.Add(slot);
            return generation;
        }
    }

    /// <summary>
    /// Closes the current scan: slots alive in the previous scan but not observed now are dead.
    /// </summary>
    /// <remarks>
    /// Death retires the generation but does not forget it — the counter must keep rising across
    /// repeated reuse, because generation 2 may die and be followed by generation 3 in the same
    /// slot. Only the session's end forgets (the tracker dies with its adapter).
    /// </remarks>
    public void EndScan()
    {
        lock (gate)
        {
            alive.IntersectWith(scanned);
            scanned.Clear();
        }
    }

    /// <summary>True when the slot was observed in the current or last completed scan.</summary>
    public bool IsAlive(int slot)
    {
        lock (gate)
        {
            return alive.Contains(slot);
        }
    }

    /// <summary>The generation a slot currently carries, or 0 when it was never seen.</summary>
    public long GenerationOf(int slot)
    {
        lock (gate)
        {
            return generations.TryGetValue(slot, out var generation) ? generation : 0;
        }
    }

    /// <summary>Retires a native removal immediately, including remove/rebuild within one scan.</summary>
    public void Retire(int slot)
    {
        lock (gate)
        {
            alive.Remove(slot);
            scanned.Remove(slot);
        }
    }
}
