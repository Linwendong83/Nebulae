using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NebulaeModel.Packets.Logistics;

namespace NebulaeWorld.Logistics;

internal sealed class StationStorageJournal
{
    private sealed class Delta
    {
        public int Index, Item;
        public long Count, Inc;
    }
    private sealed class Entry
    {
        public StationComponent Station;
        public long Revision, Previous;
        public readonly List<Delta> Deltas = [];
        public bool Pending;
    }

    private readonly Dictionary<int, Entry> entries = [];
    private readonly HashSet<Entry> pending = [];
    private readonly Dictionary<int, long> received = [];
    private readonly HashSet<int> repairs = [];
    private long revision;

    private Entry For(StationComponent station)
    {
        if (!entries.TryGetValue(station.gid, out var entry) || !ReferenceEquals(entry.Station, station))
            entries[station.gid] = entry = new Entry { Station = station };
        return entry;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public long SnapshotRevision(StationComponent station)
    {
        var entry = For(station);
        if (entry.Revision == 0) entry.Revision = ++revision;
        return entry.Revision;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Record(StationComponent station, int index, int item, int count, int inc)
    {
        if (station.gid <= 0 || index < 0 || item <= 0 || (count == 0 && inc == 0)) return;
        var entry = For(station);
        if (!entry.Pending)
        {
            entry.Previous = entry.Revision;
            entry.Revision = ++revision;
            entry.Pending = true;
            pending.Add(entry);
        }
        foreach (var delta in entry.Deltas)
            if (delta.Index == index && delta.Item == item) { delta.Count += count; delta.Inc += inc; return; }
        entry.Deltas.Add(new Delta { Index = index, Item = item, Count = count, Inc = inc });
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Flush(StationComponent[] pool, Action<ILSStorageCommitPacket> send)
    {
        foreach (var entry in pending)
        {
            var station = entry.Station;
            if (station.gid > 0 && station.gid < pool.Length && ReferenceEquals(pool[station.gid], station))
            {
                var packet = new ILSStorageCommitPacket
                {
                    GId = station.gid,
                    PlanetId = station.planetId,
                    Revision = entry.Revision,
                    PreviousRevision = entry.Previous,
                    Index = new int[entry.Deltas.Count],
                    ItemId = new int[entry.Deltas.Count],
                    CountDelta = new long[entry.Deltas.Count],
                    IncDelta = new long[entry.Deltas.Count]
                };
                for (var i = 0; i < entry.Deltas.Count; i++)
                {
                    var delta = entry.Deltas[i];
                    packet.Index[i] = delta.Index; packet.ItemId[i] = delta.Item;
                    packet.CountDelta[i] = delta.Count; packet.IncDelta[i] = delta.Inc;
                }
                send(packet);
            }
            entry.Deltas.Clear(); entry.Pending = false;
        }
        pending.Clear();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool AcceptSnapshot(int gid, long value)
    {
        if (gid <= 0 || value <= 0 || (received.TryGetValue(gid, out var last) && value < last)) return false;
        received[gid] = value; repairs.Remove(gid); return true;
    }
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool NeedsRepair(int gid) => gid > 0 && repairs.Add(gid);
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RepairUnavailable(int gid) => repairs.Remove(gid);

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool Receive(ILSStorageCommitPacket packet, StationComponent station)
    {
        if (received.TryGetValue(packet.GId, out var last) && packet.Revision <= last) return true;
        if (repairs.Contains(packet.GId) || last == 0 || packet.PreviousRevision != last ||
            packet.Revision <= last || !TryApply(packet, station)) return false;
        received[packet.GId] = packet.Revision; return true;
    }

    internal static bool TryApply(ILSStorageCommitPacket packet, StationComponent station)
    {
        if (station == null || station.gid != packet.GId || station.planetId != packet.PlanetId || station.storage == null ||
            packet.Index == null || packet.ItemId == null || packet.CountDelta == null || packet.IncDelta == null ||
            packet.Index.Length > 256 || packet.Index.Length != packet.ItemId.Length ||
            packet.Index.Length != packet.CountDelta.Length || packet.Index.Length != packet.IncDelta.Length) return false;
        lock (station.storage)
        {
            var storage = (StationStore[])station.storage.Clone();
            for (var i = 0; i < packet.Index.Length; i++)
            {
                var slot = packet.Index[i];
                if (slot < 0 || slot >= storage.Length || packet.ItemId[i] <= 0 || storage[slot].itemId != packet.ItemId[i] ||
                    packet.CountDelta[i] < int.MinValue || packet.CountDelta[i] > int.MaxValue ||
                    packet.IncDelta[i] < int.MinValue || packet.IncDelta[i] > int.MaxValue) return false;
                var count = storage[slot].count + packet.CountDelta[i];
                var inc = storage[slot].inc + packet.IncDelta[i];
                if (count < 0 || count > int.MaxValue || inc < 0 || inc > int.MaxValue) return false;
                storage[slot].count = (int)count; storage[slot].inc = (int)inc;
            }
            Array.Copy(storage, station.storage, storage.Length);
        }
        return true;
    }
}
