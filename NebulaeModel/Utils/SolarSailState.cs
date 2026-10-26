using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NebulaeModel.Logger;

namespace NebulaeModel.Utils;

internal static class SolarSailState
{
    internal static readonly AccessTools.FieldRef<DysonSwarm, DysonSail[]> Pool =
        AccessTools.FieldRefAccess<DysonSwarm, DysonSail[]>("sailPoolForSave");
    private static readonly ConditionalWeakTable<DysonSwarm, List<AbsorbOrder>> cancelledAbsorption = new();

    // Native Import leaves its save array available on graphical clients too. Validate before
    // committing a replacement or writing a save, so an inconsistent snapshot cannot propagate.
    internal static void Validate(DysonSwarm swarm)
    {
        var pool = ValidateBounds(swarm);
        var active = 0;
        for (var i = 0; i < swarm.sailCursor; i++)
        {
            var exists = pool[i].st >= .001f;
            if (exists != (swarm.sailInfos[i].orbit != 0))
                throw new InvalidDataException("Solar sail data and orbit metadata disagree");
            if (exists) active++;
        }
        if (active != swarm.sailCount)
            throw new InvalidDataException("Solar sail snapshot has inconsistent active slots");
    }

    private static DysonSail[] ValidateBounds(DysonSwarm swarm)
    {
        if (swarm.sailCursor < 0 || swarm.sailCursor > swarm.sailCapacity ||
            swarm.sailRecycleCursor < 0 || swarm.sailRecycleCursor > swarm.sailCursor ||
            swarm.sailInfos == null || swarm.sailInfos.Length < swarm.sailCursor)
            throw new InvalidDataException("Invalid solar sail pool bounds");
        var pool = Pool(swarm);
        if (swarm.sailCursor > 0 && (pool == null || pool.Length < swarm.sailCursor))
            throw new InvalidDataException("Missing solar sail state");
        return pool;
    }

    internal static void AfterImport(DysonSwarm swarm)
    {
        RecoverLegacySlots(swarm);
        Validate(swarm);
    }

    // Older headless saves kept the CPU cursors/queues but wrote zero GPU sail records. This
    // migration runs only on import; live/exported state and received snapshots stay strict.
    private static void RecoverLegacySlots(DysonSwarm swarm)
    {
        var pool = ValidateBounds(swarm);
        var active = 0;
        for (var i = 0; i < swarm.sailCursor; i++)
        {
            var exists = pool[i].st >= .001f;
            if (exists != (swarm.sailInfos[i].orbit != 0))
                throw new InvalidDataException("Solar sail data and orbit metadata disagree");
            if (exists) active++;
        }
        if (active == swarm.sailCount) return;
        if (active > swarm.sailCount || (swarm.sailRecycle?.Length ?? 0) < swarm.sailCursor)
            throw new InvalidDataException("Solar sail snapshot has inconsistent active slots");

        var recycled = new bool[swarm.sailCursor];
        for (var i = 0; i < swarm.sailRecycleCursor; i++)
        {
            var slot = swarm.sailRecycle[i];
            if ((uint)slot >= (uint)recycled.Length || recycled[slot] || pool[slot].st >= .001f)
                throw new InvalidDataException("Invalid solar sail recycle slot");
            recycled[slot] = true;
        }
        for (var i = 0; i < swarm.sailCursor; i++)
        {
            if (recycled[i] || pool[i].st >= .001f) continue;
            // Accept only the completely empty records emitted by the old headless path.
            if (!pool[i].Equals(default(DysonSail)) || !swarm.sailInfos[i].Equals(default(DysonSailInfo)))
                throw new InvalidDataException("Solar sail snapshot has inconsistent active slots");
        }

        var scheduled = new bool[swarm.sailCursor];
        var expiry = new ExpiryOrder[swarm.sailCapacity];
        var expiryCount = 0;
        foreach (var i in Ring(swarm.expiryOrder?.Length ?? 0, swarm.sailCapacity, swarm.expiryCursor, swarm.expiryEnding))
        {
            var order = swarm.expiryOrder[i];
            CheckQueueSlot(order.index, scheduled);
            if (pool[order.index].st >= .001f) expiry[expiryCount++] = order;
        }
        var absorb = new AbsorbOrder[swarm.sailCapacity];
        var absorbCount = 0;
        var cancelled = new List<AbsorbOrder>();
        foreach (var i in Ring(swarm.absorbOrder?.Length ?? 0, swarm.sailCapacity, swarm.absorbCursor, swarm.absorbEnding))
        {
            var order = swarm.absorbOrder[i];
            CheckQueueSlot(order.index, scheduled);
            if (pool[order.index].st >= .001f) absorb[absorbCount++] = order;
            else if (!recycled[order.index]) cancelled.Add(order);
        }
        for (var i = 0; i < swarm.sailCursor; i++)
            if (!recycled[i] && !scheduled[i]) throw new InvalidDataException("Missing solar sail lifecycle order");
        var counts = new int[swarm.orbits.Length];
        for (var i = 0; i < swarm.sailCursor; i++)
        {
            if (pool[i].st < .001f) continue;
            var orbit = swarm.sailInfos[i].orbit;
            if (orbit >= counts.Length || swarm.orbits[orbit].id != orbit)
                throw new InvalidDataException("Invalid solar sail orbit");
            counts[orbit]++;
        }

        var missing = swarm.sailCount - active;
        swarm.sailRecycleCursor = 0;
        for (var i = 0; i < swarm.sailCursor; i++)
            if (pool[i].st < .001f) swarm.sailRecycle[swarm.sailRecycleCursor++] = i;
        swarm.expiryOrder = expiry; swarm.expiryCursor = 0; swarm.expiryEnding = expiryCount;
        swarm.absorbOrder = absorb; swarm.absorbCursor = 0; swarm.absorbEnding = absorbCount;
        for (var i = 0; i < counts.Length; i++) swarm.orbits[i].count = counts[i];
        // Swarm.Import precedes the sphere's layers/nodes. Release their reservations only
        // after DysonSphere.Import has restored those nodes, preserving completed shell cells.
        cancelledAbsorption.Remove(swarm);
        if (cancelled.Count > 0) cancelledAbsorption.Add(swarm, cancelled);
        swarm.energyGenCurrentTick = active * swarm.dysonSphere.energyGenPerSail;
        swarm.dysonSphere.needRecalculatePower = true;
        Log.Warn($"[dyson] Recovered legacy headless swarm star={swarm.starData?.id ?? 0}: removed {missing} missing sail records; retained {active} sails.");
    }

    private static IEnumerable<int> Ring(int length, int capacity, int cursor, int ending)
    {
        if (capacity <= 0 || length < capacity || (uint)cursor >= (uint)capacity || (uint)ending >= (uint)capacity)
            throw new InvalidDataException("Invalid solar sail lifecycle queue");
        for (var i = cursor; i != ending; i = (i + 1) % capacity) yield return i;
    }

    private static void CheckQueueSlot(int slot, bool[] scheduled)
    {
        if ((uint)slot >= (uint)scheduled.Length || scheduled[slot])
            throw new InvalidDataException("Invalid solar sail lifecycle slot");
        scheduled[slot] = true;
    }

    internal static void FinishImport(DysonSphere sphere)
    {
        if (sphere.swarm == null || !cancelledAbsorption.TryGetValue(sphere.swarm, out var orders)) return;
        foreach (var order in orders)
            if (order.layer > 0 && order.layer < (sphere.layersIdBased?.Length ?? 0))
                sphere.FindNode(order.layer, order.node)?.CancelOrderConstructCp();
        cancelledAbsorption.Remove(sphere.swarm);
    }
}
