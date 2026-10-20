using System;
using System.Collections.Generic;
using System.IO;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Combat;

namespace NebulaeWorld.Combat;

internal sealed class CombatGenerationManager : IDisposable
{
    private readonly object gate = new();
    private readonly CombatGenerationState state = new();

    public long Get(int astro, int id)
    {
        lock (gate)
        {
            var generation = state.Get(astro, id);
            if (generation == 0 && Multiplayer.Session.IsServer) generation = state.Create(astro, id);
            return generation;
        }
    }

    public void Created(int astro, int id)
    {
        if (!Multiplayer.Session.IsServer || id <= 0) return;
        long generation;
        lock (gate) generation = state.Create(astro, id);
        if (!Multiplayer.Session.IsGameLoaded) return;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(1); writer.Write(CombatGenerationState.NormalizeAstro(astro)); writer.Write(id); writer.Write(generation);
        Multiplayer.Session.Server.SendPacket(new CombatGenerationPacket { Data = stream.ToArray() });
    }

    public byte[] Export(int planetId = 0)
    {
        var list = new List<(int Astro, int Id, long Generation)>();
        var enemies = planetId == 0 ? GameMain.spaceSector.enemyPool : GameMain.galaxy.PlanetById(planetId)?.factory?.enemyPool;
        if (enemies != null) for (var i = 1; i < enemies.Length; i++)
                if (enemies[i].id == i) list.Add((planetId, i, Get(planetId, i)));
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(list.Count);
        foreach (var item in list) { writer.Write(item.Astro); writer.Write(item.Id); writer.Write(item.Generation); }
        return stream.ToArray();
    }

    public void Import(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
        var count = reader.ReadInt32();
        if (count < 0 || count > 1000000 || stream.Length != 4L + count * 16L) throw new InvalidDataException("Invalid combat generations");
        lock (gate) for (var i = 0; i < count; i++) state.Set(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt64());
    }

    public void Dispose() { state.Clear(); }
}
