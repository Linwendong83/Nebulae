using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NebulaeModel.Packets.Logistics;

namespace NebulaeWorld.Logistics;

/// <summary>Session-owned event journal. Revisions also fence snapshots and reused station slots.</summary>
internal sealed class ShipLifecycle
{
    private readonly struct Facts : IEquatable<Facts>
    {
        public readonly int Planet, Other, Direction, Item, Count, Inc;
        public Facts(in ShipData ship)
        { Planet = ship.planetB; Other = ship.otherGId; Direction = ship.direction; Item = ship.itemId; Count = ship.itemCount; Inc = ship.inc; }
        public bool Equals(Facts other) => Planet == other.Planet && Other == other.Other && Direction == other.Direction &&
            Item == other.Item && Count == other.Count && Inc == other.Inc;
    }

    private sealed class Entry
    {
        public StationComponent Station;
        public long Revision;
        public long PreviousRevision;
        public readonly List<int> Changes = [];
        public bool Pending;
        public readonly Facts[] Facts = new Facts[64];
        public readonly bool[] Present = new bool[64];
        public int Warpers;
        public double WarpDistance;
    }

    private readonly Dictionary<int, Entry> entries = [];
    private readonly HashSet<Entry> pending = [];
    private readonly Dictionary<int, long> received = [];
    private readonly HashSet<int> repairs = [];
    private long revision;

    private Entry For(StationComponent station)
    {
        if (!entries.TryGetValue(station.gid, out var entry) || !ReferenceEquals(entry.Station, station))
        {
            entries[station.gid] = entry = new Entry { Station = station, Warpers = station.warperCount, WarpDistance = station.warpEnableDist };
            for (var i = 0; i < station.workShipCount; i++) Remember(entry, in station.workShipDatas[i]);
        }
        return entry;
    }

    private static void Remember(Entry entry, in ShipData ship)
    {
        if (ship.shipIndex < 0 || ship.shipIndex >= 64) return;
        entry.Facts[ship.shipIndex] = new Facts(ship);
        entry.Present[ship.shipIndex] = true;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ObserveBefore(StationComponent station, StationComponent[] pool)
    {
        For(station);
        for (var i = 0; i < station.workShipCount; i++)
        {
            var gid = station.workShipDatas[i].otherGId;
            if (gid > 0 && gid < pool.Length && pool[gid] != null) For(pool[gid]);
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ObserveAfter(StationComponent station, StationComponent[] pool)
    {
        var entry = For(station);
        ObserveWarper(entry);
        for (var i = 0; i < station.workShipCount; i++)
        {
            ref var ship = ref station.workShipDatas[i];
            var slot = ship.shipIndex;
            if (slot >= 0 && slot < 64 && entry.Present[slot] && !entry.Facts[slot].Equals(new Facts(ship)))
            {
                Changed(station);
                entry.Changes.AddRange([ILSShipLifecyclePacket.Facts, slot, ship.planetB, ship.otherGId,
                    ship.direction, ship.itemId, ship.itemCount, ship.inc, ship.warperCnt, ship.stage,
                    BitConverter.ToInt32(BitConverter.GetBytes(ship.t), 0), BitConverter.ToInt32(BitConverter.GetBytes(ship.warpState), 0)]);
            }
            Remember(entry, ship);
            var other = ship.otherGId;
            if (other > 0 && other < pool.Length && pool[other] != null) ObserveWarper(For(pool[other]));
        }
    }

    private void ObserveWarper(Entry entry)
    {
        if (entry.Warpers == entry.Station.warperCount && entry.WarpDistance == entry.Station.warpEnableDist) return;
        entry.Warpers = entry.Station.warperCount;
        entry.WarpDistance = entry.Station.warpEnableDist;
        Changed(entry.Station);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public long SnapshotRevision(StationComponent station)
    {
        var entry = For(station);
        if (entry.Revision == 0) entry.Revision = ++revision;
        return entry.Revision;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Changed(StationComponent station)
    {
        var entry = For(station);
        if (entry.Pending) return;
        entry.PreviousRevision = entry.Revision;
        entry.Revision = ++revision;
        entry.Pending = true;
        pending.Add(entry);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Depart(StationComponent station, in ShipData ship)
    {
        Changed(station);
        var changes = For(station).Changes;
        changes.Add(ILSShipLifecyclePacket.Depart);
        changes.Add(ship.shipIndex);
        changes.Add(ship.planetB);
        changes.Add(ship.otherGId);
        changes.Add(ship.itemId);
        changes.Add(ship.itemCount);
        changes.Add(ship.inc);
        changes.Add(ship.warperCnt);
        changes.Add(ship.gene);
        Remember(For(station), ship);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Return(StationComponent station, int shipIndex)
    {
        Changed(station);
        var entry = For(station);
        entry.Changes.Add(ILSShipLifecyclePacket.Return);
        entry.Changes.Add(shipIndex);
        if (shipIndex >= 0 && shipIndex < 64) entry.Present[shipIndex] = false;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Flush(StationComponent[] pool, Action<ILSShipLifecyclePacket> send)
    {
        foreach (var entry in pending)
        {
            if (!entry.Pending) continue;
            var station = entry.Station;
            if (station.gid > 0 && station.gid < pool.Length && ReferenceEquals(pool[station.gid], station))
                send(new ILSShipLifecyclePacket
                {
                    GId = station.gid,
                    PlanetId = station.planetId,
                    PreviousRevision = entry.PreviousRevision,
                    Revision = entry.Revision,
                    Changes = entry.Changes.ToArray(),
                    WorkShipCount = station.workShipCount,
                    IdleShipCount = station.idleShipCount,
                    WorkShipIndices = station.workShipIndices,
                    IdleShipIndices = station.idleShipIndices,
                    Energy = station.energy,
                    WarperCount = station.warperCount,
                    WarpEnableDistance = station.warpEnableDist
                });
            entry.Pending = false;
            entry.Changes.Clear();
        }
        pending.Clear();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool AcceptSnapshot(int gid, long value)
    {
        if (gid <= 0 || value <= 0 || (received.TryGetValue(gid, out var last) && value < last)) return false;
        received[gid] = value;
        repairs.Remove(gid);
        return true;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool NeedsRepair(int gid) => gid > 0 && repairs.Add(gid);
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RepairUnavailable(int gid) => repairs.Remove(gid);

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool Receive(ILSShipLifecyclePacket packet, StationComponent station)
    {
        if (received.TryGetValue(packet.GId, out var last) && packet.Revision <= last) return true;
        if (repairs.Contains(packet.GId) || last == 0 || packet.PreviousRevision != last ||
            packet.Revision <= packet.PreviousRevision || !TryApply(packet, station)) return false;
        received[packet.GId] = packet.Revision;
        return true;
    }

    // Validate and replay into scratch first: a damaged batch must not partially shift live ship slots.
    internal static bool TryApply(ILSShipLifecyclePacket packet, StationComponent station)
    {
        if (station == null || station.gid != packet.GId || station.planetId != packet.PlanetId ||
            station.workShipDatas == null || packet.Changes == null ||
            packet.Changes.Length > ILSShipLifecyclePacket.MaxChanges * ILSShipLifecyclePacket.MaxRecordSize ||
            packet.WorkShipCount < 0 || packet.IdleShipCount < 0 || packet.WarperCount < 0 ||
            packet.WorkShipCount > station.workShipDatas.Length ||
            packet.IdleShipCount > station.workShipDatas.Length - packet.WorkShipCount ||
            (packet.IdleShipIndices & packet.WorkShipIndices) != 0 || packet.WarpEnableDistance < 0 ||
            double.IsNaN(packet.WarpEnableDistance) || double.IsInfinity(packet.WarpEnableDistance)) return false;
        var ships = (ShipData[])station.workShipDatas.Clone();
        var count = station.workShipCount;
        if (count < 0 || count > ships.Length) return false;
        var events = 0;
        for (var i = 0; i < packet.Changes.Length;)
        {
            if (++events > ILSShipLifecyclePacket.MaxChanges) return false;
            var kind = packet.Changes[i++];
            if (i == packet.Changes.Length) return false;
            var slot = packet.Changes[i++];
            if (slot < 0 || slot >= ships.Length || slot >= 64) return false;
            var index = -1;
            for (var j = 0; j < count; j++) if (ships[j].shipIndex == slot) { index = j; break; }
            if (kind == ILSShipLifecyclePacket.Return)
            {
                if (index < 0) return false;
                Array.Copy(ships, index + 1, ships, index, count - index - 1);
                ships[--count] = default;
                continue;
            }
            if (kind == ILSShipLifecyclePacket.Facts)
            {
                if (index < 0 || packet.Changes.Length - i < 10) return false;
                ref var current = ref ships[index];
                current.planetB = packet.Changes[i++]; current.otherGId = packet.Changes[i++];
                current.direction = packet.Changes[i++]; current.itemId = packet.Changes[i++];
                current.itemCount = packet.Changes[i++]; current.inc = packet.Changes[i++]; current.warperCnt = packet.Changes[i++];
                current.stage = packet.Changes[i++];
                current.t = BitConverter.ToSingle(BitConverter.GetBytes(packet.Changes[i++]), 0);
                current.warpState = BitConverter.ToSingle(BitConverter.GetBytes(packet.Changes[i++]), 0);
                if (current.planetB <= 0 || current.otherGId < 0 || (current.direction != -1 && current.direction != 1) ||
                    current.itemId < 0 || current.itemCount < 0 || current.inc < 0 || current.warperCnt < 0 ||
                    float.IsNaN(current.t) || float.IsInfinity(current.t) || float.IsNaN(current.warpState) || float.IsInfinity(current.warpState)) return false;
                continue;
            }
            if (kind != ILSShipLifecyclePacket.Depart) return false;
            if (index >= 0 || count == ships.Length || packet.Changes.Length - i < 7) return false;
            var ship = new ShipData
            {
                shipIndex = slot,
                planetA = packet.PlanetId,
                planetB = packet.Changes[i++],
                otherGId = packet.Changes[i++],
                itemId = packet.Changes[i++],
                itemCount = packet.Changes[i++],
                inc = packet.Changes[i++],
                warperCnt = packet.Changes[i++],
                gene = packet.Changes[i++],
                stage = -2,
                direction = 1
            };
            if (ship.planetB <= 0 || ship.otherGId <= 0 || ship.itemId <= 0 || ship.itemCount < 0 ||
                ship.inc < 0 || ship.warperCnt < 0) return false;
            ships[count++] = ship;
        }
        ulong mask = 0;
        for (var i = 0; i < count; i++)
        {
            var slot = ships[i].shipIndex;
            if (slot < 0 || slot >= 64 || slot >= ships.Length || (mask & (1UL << slot)) != 0) return false;
            mask |= 1UL << slot;
        }
        if (count != packet.WorkShipCount || mask != packet.WorkShipIndices) return false;
        var idleCount = 0;
        for (var i = 0; i < 64; i++)
            if ((packet.IdleShipIndices & (1UL << i)) != 0) { if (i >= ships.Length) return false; idleCount++; }
        if (idleCount != packet.IdleShipCount) return false;
        Array.Copy(ships, station.workShipDatas, ships.Length);
        station.workShipCount = count;
        station.idleShipCount = packet.IdleShipCount;
        station.workShipIndices = packet.WorkShipIndices;
        station.idleShipIndices = packet.IdleShipIndices;
        station.energy = packet.Energy;
        station.warperCount = packet.WarperCount;
        station.warpEnableDist = packet.WarpEnableDistance;
        return true;
    }
}
