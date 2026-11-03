using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Factory;

namespace NebulaeWorld.Factory;

/// <summary>Host instance identities change on slot reuse, independently of build assignments.</summary>
internal sealed class PrebuildInstances
{
    /// <summary>Identity entries per packet. A paste creates thousands in one frame; one frame per entry flooded the queue.</summary>
    internal const int BatchSize = 512;
    private const int MaxBatchEntries = 4096;
    private readonly ConcurrentDictionary<(int Planet, int Id), long> live = new();
    private readonly List<(int Id, long Generation)> pending = new();
    private int pendingPlanet, pendingStar;
    private long next;
    public void Clear()
    {
        live.Clear(); next = 0;
        pending.Clear(); pendingPlanet = pendingStar = 0;
    }
    public void Remove(int planet, int id) => live.TryRemove((planet, id), out _);
    public void Apply(PrebuildInstancePacket packet)
    {
        if (packet.PlanetId <= 0 || packet.PrebuildId <= 0 || packet.Generation <= 0) return;
        live.AddOrUpdate((packet.PlanetId, packet.PrebuildId), packet.Generation,
            (_, current) => Math.Max(current, packet.Generation));
    }

    /// <summary>Applies one batch. Entries are independent, so one bad pair cannot hide the others.</summary>
    public void Apply(PrebuildInstanceBatchPacket packet)
    {
        if (packet?.PrebuildIds == null || packet.Generations == null || packet.PlanetId <= 0 ||
            packet.PrebuildIds.Length != packet.Generations.Length ||
            packet.PrebuildIds.Length > MaxBatchEntries) return;
        for (var i = 0; i < packet.PrebuildIds.Length; i++)
        {
            var id = packet.PrebuildIds[i]; var generation = packet.Generations[i];
            if (id <= 0 || generation <= 0) continue;
            live.AddOrUpdate((packet.PlanetId, id), generation, (_, current) => Math.Max(current, generation));
        }
    }
    public long Get(PlanetFactory factory, int id)
    {
        if (factory == null || id <= 0 || id >= factory.prebuildCursor || factory.prebuildPool[id].id != id) return 0;
        if (live.TryGetValue((factory.planetId, id), out var generation)) return generation;
        if (!Multiplayer.Session.IsServer) return 0;
        return live.GetOrAdd((factory.planetId, id), _ => Interlocked.Increment(ref next));
    }
    public void Created(PlanetFactory factory, int id)
    {
        if (!Multiplayer.Session.IsServer) return;
        // The identity itself is published immediately: local claims and manual builds read it from
        // the map in the same frame. Only the wire send waits for the frame boundary.
        var generation = live[(factory.planetId, id)] = Interlocked.Increment(ref next);
        Queue(factory, id, generation);
    }

    /// <summary>Sends every identity collected since the last frame, in bounded batches.</summary>
    /// <remarks>
    /// A blueprint paste reaches this list once per pasted entity in a single frame, so the entries are
    /// chunked: one reliable frame per <see cref="BatchSize"/> identities instead of one per entity.
    /// </remarks>
    public void Flush()
    {
        if (pending.Count == 0) return;
        var planet = pendingPlanet; var star = pendingStar;
        try
        {
            if (star <= 0)
            {
                Diagnostics.Warn("prebuild:instances:star", () =>
                    $"[build] dropping {pending.Count} prebuild identities for planet={planet}: the factory has no star");
                return;
            }
            for (var offset = 0; offset < pending.Count; offset += BatchSize)
            {
                var count = Math.Min(BatchSize, pending.Count - offset);
                var ids = new int[count]; var generations = new long[count];
                for (var i = 0; i < count; i++)
                {
                    ids[i] = pending[offset + i].Id;
                    generations[i] = pending[offset + i].Generation;
                }
                Multiplayer.Session.Network.SendPacketToStar(new PrebuildInstanceBatchPacket
                { PlanetId = planet, PrebuildIds = ids, Generations = generations }, star);
            }
        }
        finally { pending.Clear(); pendingPlanet = pendingStar = 0; }
    }

    private void Queue(PlanetFactory factory, int id, long generation)
    {
        // A frame can span planets when a remote request builds elsewhere; never mix them in one packet.
        if (pending.Count > 0 && pendingPlanet != factory.planetId) Flush();
        if (pending.Count == 0)
        {
            pendingPlanet = factory.planetId;
            pendingStar = factory.planet?.star?.id ?? 0;
        }
        pending.Add((id, generation));
    }
    public byte[] Export(PlanetFactory factory, byte[] claims)
    {
        var entries = new List<(int Id, long Generation)>();
        for (var i = 1; i < factory.prebuildCursor; i++)
            if (factory.prebuildPool[i].id == i) entries.Add((i, Get(factory, i)));
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(claims.Length); writer.Write(claims); writer.Write(entries.Count);
        foreach (var entry in entries) { writer.Write(entry.Id); writer.Write(entry.Generation); }
        return stream.ToArray();
    }
    public byte[] Import(PlanetFactory factory, byte[] data)
    {
        using var stream = new MemoryStream(data, false); using var reader = new BinaryReader(stream);
        var length = reader.ReadInt32();
        if (length < 4 || length > stream.Length - 8) throw new InvalidDataException("Invalid construction snapshot.");
        var claims = reader.ReadBytes(length); var count = reader.ReadInt32();
        if (count < 0 || count >= factory.prebuildCursor || stream.Length - stream.Position != (long)count * 12)
            throw new InvalidDataException("Invalid prebuild instance snapshot.");
        var staged = new Dictionary<(int Planet, int Id), long>();
        for (var i = 0; i < count; i++)
        {
            var id = reader.ReadInt32(); var generation = reader.ReadInt64();
            if (id <= 0 || id >= factory.prebuildCursor || generation <= 0 || staged.ContainsKey((factory.planetId, id)))
                throw new InvalidDataException("Invalid prebuild identity.");
            staged.Add((factory.planetId, id), generation);
        }
        foreach (var key in live.Keys) if (key.Planet == factory.planetId) live.TryRemove(key, out _);
        foreach (var entry in staged) live[entry.Key] = entry.Value;
        return claims;
    }
}
