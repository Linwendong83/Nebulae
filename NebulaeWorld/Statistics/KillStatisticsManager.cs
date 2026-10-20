using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NebulaeModel.Networking;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Statistics;

namespace NebulaeWorld.Statistics;

internal sealed class KillStatisticsManager : IDisposable
{
    private readonly Dictionary<string, AstroKillStat> personal = new(StringComparer.Ordinal);
    private readonly Dictionary<ushort, INebulaeConnection> viewers = new();
    private readonly ConcurrentQueue<(long Tick, int Astro, int Model, string Owner)> events = new();
    private readonly List<(long Tick, int Astro, int Model, string Owner)> batch = new();
    private readonly Dictionary<int, AstroKillStat> remote = new();
    [ThreadStatic] private static ushort currentOwner;
    [ThreadStatic] private static short currentSource;
    private long lastTick = -1;
    private long lastBroadcast;
    public long RemoteTick { get; private set; } = -1;
    public bool Applying { get; private set; }
    public IEnumerable<int> RemotePlanets => remote.Keys.Where(x => x > 0 && x % 100 != 0);
    public AstroKillStat GetRemote(int astro) => remote.TryGetValue(astro, out var value) ? value : null;

    public IDisposable EnterDeath(in CombatStat stat)
    {
        var oldOwner = currentOwner;
        var oldSource = currentSource;
        currentOwner = stat.lastCaster.type == ETargetType.Player ? (ushort)stat.lastCaster.id : (ushort)0;
        currentSource = (short)stat.lastCaster.type;
        return new DeathScope(() => { currentOwner = oldOwner; currentSource = oldSource; });
    }

    public void Record(int astro, int model)
    {
        if (!Multiplayer.Session.IsServer || model < 0 || model >= 2048) return;
        events.Enqueue((GameMain.gameTick, astro, model, ""));
    }

    public bool RecordPersonal(int model)
    {
        if (!Multiplayer.Session.IsServer || currentSource != (short)ETargetType.Player || currentOwner == 0) return false;
        var identity = Identity(currentOwner);
        if (identity == null) return false;
        events.Enqueue((GameMain.gameTick, -1, model, identity));
        if (currentOwner == Multiplayer.Session.LocalPlayer.Id) return true;
        var stat = GetPersonal(identity);
        lock (stat) stat.killRegister[model]++;
        return false;
    }

    private static string Identity(ushort id)
    {
        if (id == Multiplayer.Session.LocalPlayer.Id)
            return Multiplayer.Session.IsDedicated ? null : ((PlayerData)Multiplayer.Session.LocalPlayer.Data).PersistentId;
        return ((Multiplayer.Session.Server.Players.Get(id) ??
            Multiplayer.Session.Server.Players.Get(id, EConnectionStatus.Syncing))?.Data as PlayerData)?.PersistentId;
    }

    private AstroKillStat GetPersonal(string identity)
    {
        lock (personal)
        {
            if (personal.TryGetValue(identity, out var stat)) return stat;
            stat = new AstroKillStat();
            if (SaveManager.PlayerSaves.TryGetValue(identity, out var data) &&
                data is PlayerData { PersonalKillData.Length: > 0 } player)
            {
                using var stream = new MemoryStream(player.PersonalKillData);
                using var reader = new BinaryReader(stream);
                stat.InitRegister(); stat.Import(stream, reader);
            }
            else stat.Init();
            personal.Add(identity, stat);
            return stat;
        }
    }

    public void Subscribe(ushort id, INebulaeConnection connection)
    {
        viewers[id] = connection;
        connection.SendPacket(new KillStatisticsPacket
        { Snapshot = true, ToTick = GameMain.gameTick, Data = ExportSnapshot(id) });
    }

    public void Unsubscribe(ushort id) => viewers.Remove(id);

    public byte[] ExportSnapshot(ushort id)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(GameMain.gameTick);
        var stats = GameMain.statistics.kill;
        var list = new List<(int Astro, AstroKillStat Stat)>();
        for (var i = 0; i < stats.starKillStatPool.Length; i++)
            if (stats.starKillStatPool[i] != null) list.Add((i * 100, stats.starKillStatPool[i]));
        for (var i = 0; i < GameMain.data.factoryCount; i++)
            if (stats.factoryKillStatPool[i] != null) list.Add((GameMain.data.factories[i].planetId, stats.factoryKillStatPool[i]));
        var own = id == Multiplayer.Session.LocalPlayer.Id ? stats.mechaKillStat : GetPersonal(Identity(id) ?? "pending");
        if (own != null) list.Add((-1, own));
        writer.Write(list.Count);
        foreach (var entry in list)
        {
            writer.Write(entry.Astro);
            entry.Stat.Export(stream, writer);
        }
        return stream.ToArray();
    }

    public void ApplySnapshot(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        var tick = reader.ReadInt64();
        var count = reader.ReadInt32();
        if (count < 0 || count > GameMain.galaxy.astrosData.Length + 1) throw new InvalidDataException("Invalid kill statistics count");
        var next = new Dictionary<int, AstroKillStat>();
        for (var i = 0; i < count; i++)
        {
            var astro = reader.ReadInt32();
            var stat = new AstroKillStat(); stat.InitRegister(); stat.Import(stream, reader);
            next.Add(astro, stat);
        }
        foreach (var stat in remote.Values) stat.Free();
        remote.Clear();
        foreach (var entry in next) remote.Add(entry.Key, entry.Value);
        GameMain.statistics.kill.mechaKillStat = GetRemote(-1);
        RemoteTick = tick;
    }

    public void ApplyDelta(KillStatisticsPacket packet)
    {
        if (packet.ToTick <= RemoteTick) return;
        if (RemoteTick < 0 || packet.FromTick > RemoteTick || packet.ToTick - RemoteTick > 600)
        {
            Multiplayer.Session.Network.SendPacket(new KillStatisticsRequest { Subscribe = true });
            return;
        }
        using var stream = new MemoryStream(packet.Data, false);
        using var reader = new BinaryReader(stream);
        var count = reader.ReadInt32();
        if (count < 0 || count > 1000000) throw new InvalidDataException("Invalid kill event batch");
        var changes = new Dictionary<long, List<(int Astro, int Model)>>();
        for (var i = 0; i < count; i++)
        {
            var tick = reader.ReadInt64(); var astro = reader.ReadInt32(); var model = reader.ReadInt32();
            if (tick <= RemoteTick || tick > packet.ToTick || model < 0 || model >= 2048) continue;
            if (!changes.TryGetValue(tick, out var list)) changes[tick] = list = new();
            list.Add((astro, model));
        }
        Applying = true;
        try
        {
            for (var tick = RemoteTick + 1; tick <= packet.ToTick; tick++)
            {
                if (changes.TryGetValue(tick, out var list)) foreach (var change in list)
                    {
                        if (!remote.TryGetValue(change.Astro, out var stat))
                        { stat = new AstroKillStat(); stat.Init(); remote.Add(change.Astro, stat); }
                        stat.killRegister[change.Model]++;
                    }
                foreach (var stat in remote.Values) { stat.GameTick(tick); stat.AfterTick(); }
            }
            RemoteTick = packet.ToTick;
            GameMain.statistics.kill.mechaKillStat = GetRemote(-1);
        }
        finally { Applying = false; }
    }

    public void GameTick()
    {
        if (!Multiplayer.Session.IsServer || !Multiplayer.Session.IsGameLoaded) return;
        var tick = GameMain.gameTick;
        if (tick == lastTick) return;
        lastTick = tick;
        lock (personal) foreach (var stat in personal.Values) { stat.GameTick(tick); stat.AfterTick(); }
        while (events.TryDequeue(out var item)) batch.Add(item);
        if (tick % 60 != 0) return;
        foreach (var viewer in viewers)
        {
            var identity = Identity(viewer.Key);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            var filtered = batch.Where(x => x.Owner.Length == 0 || x.Owner == identity).ToArray();
            writer.Write(filtered.Length);
            foreach (var item in filtered) { writer.Write(item.Tick); writer.Write(item.Astro); writer.Write(item.Model); }
            viewer.Value.SendPacket(new KillStatisticsPacket
            { FromTick = lastBroadcast, ToTick = tick, Data = stream.ToArray() });
        }
        lastBroadcast = tick;
        batch.Clear();
    }

    public void CapturePlayersForSave()
    {
        foreach (var entry in SaveManager.PlayerSaves)
        {
            if (entry.Value is not PlayerData data || !personal.TryGetValue(entry.Key, out var stat)) continue;
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            stat.Export(stream, writer);
            data.PersonalKillData = stream.ToArray();
        }
    }

    public void Dispose()
    {
        foreach (var stat in personal.Values) stat.Free();
        personal.Clear(); viewers.Clear(); batch.Clear(); remote.Clear();
    }

    private sealed class DeathScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
