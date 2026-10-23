using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NebulaeModel.Packets.Factory;

namespace NebulaeWorld.Factory;

/// <summary>Host instance identities change on slot reuse, independently of build assignments.</summary>
internal sealed class PrebuildInstances
{
    private readonly ConcurrentDictionary<(int Planet, int Id), long> live = new();
    private long next;
    public void Clear() { live.Clear(); next = 0; }
    public void Remove(int planet, int id) => live.TryRemove((planet, id), out _);
    public void Apply(PrebuildInstancePacket packet)
    {
        if (packet.PlanetId <= 0 || packet.PrebuildId <= 0 || packet.Generation <= 0) return;
        live.AddOrUpdate((packet.PlanetId, packet.PrebuildId), packet.Generation,
            (_, current) => Math.Max(current, packet.Generation));
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
        var generation = live[(factory.planetId, id)] = Interlocked.Increment(ref next);
        Multiplayer.Session.Network.SendPacketToStar(new PrebuildInstancePacket
        { PlanetId = factory.planetId, PrebuildId = id, Generation = generation }, factory.planet.star.id);
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
