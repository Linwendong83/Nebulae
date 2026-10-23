using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Combat;
using UnityEngine;

namespace NebulaeWorld.Combat;

/// <summary>One identity joins local native prediction, host simulation and observable effects.</summary>
internal sealed class PlayerWeaponManager : IDisposable
{
    internal sealed class Entry
    {
        public PlayerShotId Shot;
        public int Part, Id, Star, Astro, ImpactOrdinal;
        public NativePlayerWeaponPool Pool;
        public ObjectKey Target;
        public long SampleTick, LastCorrection, EndTick = -1;
        public bool Predicted, Launched, Ended, LaunchAudio, EndRequested;
        public long MinimumUntil;
        public byte[] Pending;
        public readonly ConcurrentDictionary<int, (int Kind, int Id, long Born)> Impacts = new();
        public readonly HashSet<int> ConfirmedImpacts = new();
    }
    public sealed class FireCapture
    {
        internal (NativePlayerWeaponPool Pool, int Cursor, int Recycled)[] Pools;
        internal CombatIntent Intent;
        internal SkillTarget Target;
        internal int Item;
        internal PlayerWeaponResources.Scope Resources;
    }
    public sealed class TickScope : IDisposable
    {
        internal Entry Entry;
        private readonly Entry previous;
        private readonly int[] impacts;
        private readonly SkillSystem skills;
        internal TickScope(Entry entry, SkillSystem skills)
        {
            Entry = entry; previous = current; current = entry; this.skills = skills;
            impacts = Multiplayer.Session.Impacts.Begin(skills);
        }
        public void Dispose()
        {
            try { Multiplayer.Session?.Impacts.End(skills, impacts); }
            finally { current = previous; }
        }
    }
    public readonly struct HostScope : IDisposable
    {
        private readonly PlayerShotId? previous;
        public HostScope(PlayerShotId shot) { previous = hostShot; hostShot = shot; }
        public void Dispose() => hostShot = previous;
    }
    [ThreadStatic] private static Entry current;
    [ThreadStatic] private static PlayerShotId? hostShot;
    public static bool SuppressImpactAudio => current != null && current.Impacts.ContainsKey(current.ImpactOrdinal + 1);
    public static int EffectAstro(SkillSystem system)
    {
        if (current == null || Multiplayer.Session?.IsServer != true) return BattleImpactCapture.EffectAstro(system);
        var astro = current.Pool.Exists(current.Id) ? NativePlayerWeaponPool.Astro(current.Pool.Read(current.Id)) : current.Astro;
        if (astro > 0 && SpaceSector.IsPlanetAstro(astro)) return astro;
        if (current.Target.Kind == PoolKind.GroundEnemy) return current.Target.Scope;
        var id = current.Target.NativeId;
        if (current.Target.Kind == PoolKind.SpaceEnemy && id > 0 && id < system.sector.enemyCursor)
        {
            ref var enemy = ref system.sector.enemyPool[id];
            if (enemy.id == id && SpaceSector.IsPlanetAstro(enemy.astroId)) return enemy.astroId;
        }
        return system.localAstroId;
    }
    private readonly Dictionary<(PlayerShotId, int), Entry> entries = new();
    private readonly Dictionary<(BattleEffectKind, int), Entry> slots = new();
    private readonly ConcurrentQueue<(AuthorityEpoch Epoch, PlayerWeaponSample Sample)> inbound = new();
    private readonly ConcurrentQueue<PlayerWeaponSample> outbound = new();
    private readonly ConcurrentQueue<(INebulaeConnection Connection, int Star)> initial = new();
    private readonly HashSet<PlayerShotId> unsettled = new();
    private readonly Dictionary<PlayerShotId, long> settled = new();
    private readonly Dictionary<PlayerShotId, long> cancelled = new();
    private readonly List<Entry> tickEntries = new();
    private readonly List<PlayerWeaponSample> eventScratch = new(), selectedScratch = new();
    private readonly List<(PlayerShotId, int)> expiredEntries = new();
    private readonly List<PlayerShotId> expiredShots = new();
    private NativePlayerWeaponPool[] pools;
    private SkillSystem skills;
    private AuthorityEpoch epoch;
    private ulong connection;
    private long hostSequence;
    private BattleAudioPlayback audio;
    public long PredictedShots { get; private set; }
    public long HostShots { get; private set; }
    public long ReceivedShots { get; private set; }
    public long LocalImpacts { get; private set; }
    public long HostImpacts { get; private set; }
    public long CancelledShots { get; private set; }
    public long LastPredictionTick { get; private set; }
    public int LastRejectCode { get; private set; }
    public PlayerWeaponResources Resources { get; } = new();
    public int ActiveCount => entries.Values.Count(x => !x.Ended);
    internal static int PresentationWindow(BattleEffectKind kind) => kind == BattleEffectKind.MechaShieldBurst ? 45 : BattleEffectData.IsOneShotLaser(kind) ? 20 : 0;

    private void Ensure()
    {
        var session = Multiplayer.Session;
        if (!epoch.Equals(session.Authority.Epoch) || connection != session.Authority.Connection.Value || !ReferenceEquals(skills, GameMain.spaceSector?.skillSystem))
        {
            var waiting = new List<PlayerWeaponSample>();
            while (inbound.TryDequeue(out var message))
                if (message.Epoch.Equals(session.Authority.Epoch)) waiting.Add(message.Sample);
            if (skills != null || epoch.IsValid)
            {
                Dispose();
                session.DarkFogTurrets?.Dispose();
                session.FleetCommands?.Dispose();
                session.Impacts?.Dispose();
            }
            foreach (var sample in waiting) inbound.Enqueue((session.Authority.Epoch, sample));
            epoch = session.Authority.Epoch; connection = session.Authority.Connection.Value;
            skills = GameMain.spaceSector?.skillSystem;
            if (skills != null) pools = NativePlayerWeaponPool.Create(skills);
        }
    }
    public FireCapture BeginFire(CombatIntent intent, SkillTarget target)
    {
        Ensure();
        if (pools == null) return null;
        return new FireCapture
        {
            Intent = intent,
            Target = target,
            Resources = Multiplayer.Session.IsClient && !PlayerWeaponResources.Active ? Resources.Enter(GameMain.mainPlayer.mecha) : null,
            Item = intent.Kind == CombatIntentKind.Bomb ? GameMain.mainPlayer?.mecha?.bombStorage?.grids[0].itemId ?? 0 : (int)intent.X,
            Pools = pools.Select(p => (p, p.Cursor, p.Recycled)).ToArray()
        };
    }
    public void FinishFire(FireCapture capture, bool success)
    {
        try { FinishFireCore(capture, success); }
        finally { capture?.Resources?.Dispose(); }
    }
    private void FinishFireCore(FireCapture capture, bool success)
    {
        if (capture == null) return;
        var added = capture.Pools.SelectMany(x => x.Pool.Added(x.Cursor, x.Recycled).Select(id => (Pool: x.Pool, Id: id))).ToArray();
        if (!success) { foreach (var item in added) item.Pool.Remove(item.Id); return; }
        if (added.Length == 0) return;
        var session = Multiplayer.Session;
        PlayerShotId shot;
        if (session.IsClient)
        {
            if (!session.CombatAuthority.Send(capture.Intent, capture.Target, out var sequence))
            { foreach (var item in added) item.Pool.Remove(item.Id); return; }
            shot = new PlayerShotId(session.LocalPlayer.Id, session.Authority.Connection.Value, sequence);
            PredictedShots++; LastPredictionTick = GameMain.gameTick;
            Resources.Reserve(shot, (EAmmoType)capture.Intent.Argument, capture.Intent.Kind == CombatIntentKind.Bomb,
                capture.Target.astroId == GameMain.localPlanet?.id, capture.Item, capture.Intent.Kind == CombatIntentKind.Bomb ? 0 : (int)capture.Intent.Y);
        }
        else { shot = hostShot ?? new PlayerShotId(session.LocalPlayer.Id, 0, ++hostSequence); HostShots++; }
        var target = TargetKey(capture.Target);
        var star = session.IsServer ? CombatAuthorityManager.CasterStar()?.id ?? 0 : GameMain.localStar?.id ?? 0;
        for (var part = 0; part < added.Length; part++)
        {
            var item = added[part];
            var entry = new Entry
            {
                Shot = shot,
                Part = part,
                Id = item.Id,
                Pool = item.Pool,
                Star = star,
                Astro = NativePlayerWeaponPool.Astro(item.Pool.Read(item.Id)),
                Target = target,
                Predicted = session.IsClient,
                Launched = session.IsServer,
                SampleTick = GameMain.gameTick,
                LastCorrection = GameMain.gameTick
            };
            Bind(entry);
            if (session.IsServer) outbound.Enqueue(Sample(entry, PlayerWeaponEvent.Launch));
        }
        if (session.IsServer && shot.Connection != 0) unsettled.Add(shot);
    }
    private ObjectKey TargetKey(SkillTarget target)
    {
        if (target.id <= 0) return default;
        var session = Multiplayer.Session;
        var ground = target.astroId > 0 && target.astroId <= 1000000;
        ObjectKey key = default;
        if (session.IsClient) session.AuthorityRuntime.WorldReplica.TryFindKey(ground ? PoolKind.GroundEnemy : PoolKind.SpaceEnemy, ground ? target.astroId : 0, target.id, out key);
        else if (ground) session.AuthorityRuntime.GroundEnemyHost?.TryGetEnemyKey(target.astroId, target.id, out key);
        else session.AuthorityRuntime.SpaceEnemyHost?.TryGetEnemyKey(target.id, out key);
        return key;
    }
    private void Bind(Entry entry)
    {
        var slot = (entry.Pool.Kind, entry.Id);
        if (entry.Id > 0)
        {
            if (slots.TryGetValue(slot, out var previous) && previous != entry)
            { if (Multiplayer.Session.IsServer) End(previous); else previous.Id = 0; }
            slots[slot] = entry;
        }
        entries[(entry.Shot, entry.Part)] = entry;
    }
    public bool Owns(BattleEffectKind kind, int id) => slots.ContainsKey((kind, id));

    public TickScope BeforeSkill(IPoolElement value, SkillSystem system, out bool run)
    {
        run = true;
        if (!ReferenceEquals(system, skills) || pools == null) return null;
        Entry found = null;
        foreach (var pool in pools)
            if (slots.TryGetValue((pool.Kind, value.ID), out var candidate) && pool.Exists(value.ID) && pool.Read(value.ID).Equals(value))
            { found = candidate; break; }
        if (found == null) return null;
        var astro = NativePlayerWeaponPool.Astro(value);
        if (astro > 0) found.Astro = astro;
        run = Multiplayer.Session.IsServer || Ready(found);
        return run ? new TickScope(found, system) : null;
    }
    private bool Ready(Entry entry)
    {
        if (entry.Ended || entry.Star != GameMain.localStar?.id) return false;
        if (NativePlayerWeaponPool.IsLocal(entry.Pool.Kind) && entry.Astro != GameMain.localPlanet?.id) return false;
        var astro = entry.Id > 0 && entry.Pool.Exists(entry.Id) ? NativePlayerWeaponPool.RequiredPlanet(entry.Pool.Read(entry.Id)) : entry.Astro;
        if (astro > 0 && astro < 1000000 && (astro >= skills.astroFactories.Length || skills.astroFactories[astro] == null)) return false;
        if (entry.Target.NativeId > 0 && (!Multiplayer.Session.AuthorityRuntime.WorldReplica.TryFindKey(entry.Target.Kind,
                entry.Target.Scope, entry.Target.NativeId, out var currentKey) || !currentKey.Equals(entry.Target))) return false;
        return true;
    }
    public bool CaptureImpact(int kind, int id, ParticleData particle)
    {
        var entry = current;
        if (entry == null) return false;
        var ordinal = Interlocked.Increment(ref entry.ImpactOrdinal);
        if (Multiplayer.Session.IsClient)
        {
            if (entry.Impacts.ContainsKey(ordinal) || entry.ConfirmedImpacts.Contains(ordinal)) { skills.hitEffects[kind].Remove(id); return true; }
            entry.Impacts[ordinal] = (kind, id, GameMain.gameTick - particle.time); LocalImpacts++;
        }
        else
        {
            HostImpacts++;
            using var s = new MemoryStream(); using var w = new BinaryWriter(s); particle.Export(w);
            outbound.Enqueue(new PlayerWeaponSample
            {
                Shot = entry.Shot,
                Part = entry.Part,
                Ordinal = ordinal,
                Event = PlayerWeaponEvent.Impact,
                Kind = entry.Pool.Kind,
                Star = entry.Star,
                Astro = particle.astroId,
                Tick = GameMain.gameTick,
                Subtype = kind,
                Payload = s.ToArray()
            });
        }
        return true;
    }
    private static PlayerWeaponSample Sample(Entry entry, PlayerWeaponEvent type) => new()
    {
        Shot = entry.Shot,
        Part = entry.Part,
        Kind = entry.Pool.Kind,
        Event = type,
        Star = entry.Star,
        Astro = entry.Astro,
        Target = entry.Target,
        Tick = GameMain.gameTick,
        Payload = type is PlayerWeaponEvent.Launch or PlayerWeaponEvent.State ? entry.Pool.Export(entry.Id) : Array.Empty<byte>()
    };
    private void End(Entry entry)
    {
        if (entry.Ended) return;
        entry.Ended = true; entry.EndTick = GameMain.gameTick;
        if (Multiplayer.Session.IsServer) outbound.Enqueue(Sample(entry, PlayerWeaponEvent.End));
        if (entry.Id > 0 && slots.TryGetValue((entry.Pool.Kind, entry.Id), out var owner) && owner == entry) slots.Remove((entry.Pool.Kind, entry.Id));
    }
    public void Cancel(PlayerShotId shot)
    {
        if (shot.Sequence <= 0) return;
        if (Multiplayer.Session.IsServer)
            outbound.Enqueue(new PlayerWeaponSample { Shot = shot, Event = PlayerWeaponEvent.Cancel, Tick = GameMain.gameTick });
        else
        {
            if (!Resources.Contains(shot) && !entries.Keys.Any(x => x.Item1.Equals(shot))) return;
            if (!cancelled.ContainsKey(shot)) CancelledShots++;
            cancelled[shot] = GameMain.gameTick; Resources.Reject(shot);
            foreach (var entry in entries.Values.Where(x => x.Shot.Equals(shot)))
            { RemoveNative(entry); RemoveImpacts(entry); End(entry); }
        }
    }
    public bool NeedsSettlement(ushort player) => unsettled.Any(x => x.Player == player);
    public void Settle(ushort player, long revision)
    {
        foreach (var shot in unsettled.Where(x => x.Player == player).ToArray())
        {
            outbound.Enqueue(new PlayerWeaponSample { Shot = shot, Event = PlayerWeaponEvent.Settled, Tick = GameMain.gameTick, ResourceRevision = revision });
            unsettled.Remove(shot);
            settled[shot] = GameMain.gameTick;
        }
    }
    public long[] SettledSequences(ushort player, ulong connectionId) => settled.Where(x => x.Key.Player == player && x.Key.Connection == connectionId)
        .OrderByDescending(x => x.Value).Take(256).Select(x => x.Key.Sequence).ToArray();
    public void ReceiveResult(AuthorityCommandResultPacket packet)
    {
        if ((CommandResultCode)packet.ResultCode is CommandResultCode.Applied or CommandResultCode.RejectedDuplicate) return;
        LastRejectCode = packet.ResultCode;
        inbound.Enqueue((Multiplayer.Session.Authority.Epoch, new PlayerWeaponSample { Shot = new PlayerShotId(Multiplayer.Session.LocalPlayer.Id, packet.ConnectionEpoch, packet.Sequence), Event = PlayerWeaponEvent.Cancel, Tick = GameMain.gameTick }));
    }
    public void Receive(PlayerWeaponPacket packet)
    {
        if (!Multiplayer.Session.Authority.Epoch.Equals(new AuthorityEpoch(packet.EpochHigh, packet.EpochLow))) return;
        foreach (var sample in PlayerWeaponSample.Decode(packet.Data)) inbound.Enqueue((Multiplayer.Session.Authority.Epoch, sample));
    }
    public void TickClient()
    {
        if (!Multiplayer.Session.IsGameLoaded) return;
        Ensure(); if (pools == null) return;
        while (inbound.TryDequeue(out var message)) if (message.Epoch.Equals(epoch)) Apply(message.Sample);
        tickEntries.Clear(); tickEntries.AddRange(entries.Values);
        foreach (var entry in tickEntries)
        {
            if (entry.Ended) continue;
            if (entry.EndRequested && GameMain.gameTick >= entry.MinimumUntil) { RemoveNative(entry); End(entry); continue; }
            if (entry.Star != GameMain.localStar?.id || NativePlayerWeaponPool.IsLocal(entry.Pool.Kind) && entry.Astro != GameMain.localPlanet?.id)
            { RemoveNative(entry); entries.Remove((entry.Shot, entry.Part)); continue; }
            if (entry.Pending != null && Ready(entry))
            {
                entry.Id = entry.Pool.Install(entry.Pending, entry.Id); entry.Pending = null; Bind(entry);
                entry.ImpactOrdinal = 0;
                if (!entry.Predicted && PresentationWindow(entry.Pool.Kind) > 0 && !entry.LaunchAudio)
                    entry.MinimumUntil = GameMain.gameTick + Math.Min(PresentationWindow(entry.Pool.Kind), Math.Abs(NativePlayerWeaponPool.Life(entry.Pool.Read(entry.Id))));
                if (!entry.LaunchAudio && !entry.Predicted && BattleAudioPlayback.TryCue(entry.Pool.Kind, 0, entry.Pool.Read(entry.Id), out var cue))
                { audio ??= new BattleAudioPlayback(); audio.Play(cue); }
                entry.LaunchAudio = true;
                entry.Pool.Advance(entry.Id, skills, entry.SampleTick, entry.MinimumUntil > GameMain.gameTick ? 0 : (int)Math.Min(120, Math.Max(0, GameMain.gameTick - entry.SampleTick)));
            }
        }
        audio?.Update();
        tickEntries.Clear();
        CollectExpired();
    }
    private void Apply(PlayerWeaponSample s)
    {
        if (s.Shot.Player == Multiplayer.Session.LocalPlayer.Id && s.Shot.Connection != connection) return;
        if (s.Event == PlayerWeaponEvent.Cancel) { Cancel(s.Shot); return; }
        if (s.Event == PlayerWeaponEvent.Settled) { Resources.Settle(s.Shot, s.ResourceRevision); return; }
        if (cancelled.ContainsKey(s.Shot)) return;
        var key = (s.Shot, s.Part);
        if (!entries.TryGetValue(key, out var entry))
        {
            var pool = pools.FirstOrDefault(x => x.Kind == s.Kind); if (pool == null) return;
            entry = new Entry { Shot = s.Shot, Part = s.Part, Pool = pool, Star = s.Star, Astro = s.Astro, Target = s.Target, SampleTick = -1 };
            entries[key] = entry;
        }
        if (s.Event == PlayerWeaponEvent.Impact) { ApplyImpact(entry, s); return; }
        if (s.Event == PlayerWeaponEvent.End)
        {
            if (!entry.Predicted && (!entry.LaunchAudio && entry.Pending != null || PresentationWindow(entry.Pool.Kind) > 0 && GameMain.gameTick < entry.MinimumUntil))
            { entry.EndRequested = true; if (!entry.LaunchAudio) entry.MinimumUntil = GameMain.gameTick + Math.Max(1, PresentationWindow(entry.Pool.Kind)); return; }
            RemoveNative(entry); End(entry); return;
        }
        if (entry.Ended || s.Tick <= entry.SampleTick && entry.Launched) return;
        entry.SampleTick = s.Tick; entry.Star = s.Star; entry.Astro = s.Astro; entry.Target = s.Target;
        var alreadyPredicted = entry.Predicted && !entry.Launched;
        if (!entry.Launched) ReceivedShots++;
        entry.Launched = true;
        // The shooter's launch is an acknowledgement, not a second trigger or a restart.
        if (alreadyPredicted && s.Event == PlayerWeaponEvent.Launch) return;
        entry.Pending = s.Payload;
    }
    private void ApplyImpact(Entry entry, PlayerWeaponSample s)
    {
        if (!entry.ConfirmedImpacts.Add(s.Ordinal) || entry.Star != GameMain.localStar?.id || s.Subtype <= 0 || s.Subtype >= skills.hitEffects.Length) return;
        using var stream = new MemoryStream(s.Payload, false); using var r = new BinaryReader(stream);
        var particle = new ParticleData(); particle.Import(r);
        var pool = skills.hitEffects[s.Subtype]; if (pool == null) return;
        var played = entry.Impacts.TryGetValue(s.Ordinal, out var predicted);
        if (played && predicted.Kind == s.Subtype && predicted.Id > 0 && predicted.Id < pool.cursor && pool.buffer[predicted.Id].id == predicted.Id && Math.Abs(GameMain.gameTick - pool.buffer[predicted.Id].time - predicted.Born) <= 1)
        {
            // Correct the existing effect instead of spawning a duplicate. Its elapsed clock and
            // audio remain local; authoritative HP is delivered by the world-state stream.
            var id = predicted.Id; var time = pool.buffer[id].time;
            particle.id = id; particle.time = time; pool.buffer[id] = particle; return;
        }
        if (played) return; // A short effect already finished on this peer. Never replay its sound.
        particle.id = pool.Add().id; pool.buffer[particle.id] = particle;
        entry.Impacts[s.Ordinal] = (s.Subtype, particle.id, GameMain.gameTick - particle.time);
        if (BattleAudioPlayback.TryCue(BattleEffectKind.Impact, s.Subtype, particle, out var cue))
        { audio ??= new BattleAudioPlayback(); audio.Play(cue); }
    }
    private void RemoveImpacts(Entry entry)
    {
        foreach (var value in entry.Impacts.Values)
        {
            var pool = skills.hitEffects[value.Kind];
            if (value.Id > 0 && value.Id < pool.cursor && pool.buffer[value.Id].id == value.Id &&
                Math.Abs(GameMain.gameTick - pool.buffer[value.Id].time - value.Born) <= 1) pool.Remove(value.Id);
        }
    }
    private void RemoveNative(Entry entry)
    {
        if (entry.Id > 0 && slots.TryGetValue((entry.Pool.Kind, entry.Id), out var owner) && owner == entry)
        { entry.Pool.Remove(entry.Id); slots.Remove((entry.Pool.Kind, entry.Id)); }
        entry.Id = 0;
    }
    public void HostFrame()
    {
        if (!Multiplayer.Session.IsGameLoaded) return;
        Ensure(); if (pools == null) return;
        tickEntries.Clear(); tickEntries.AddRange(entries.Values);
        foreach (var entry in tickEntries)
        {
            if (entry.Ended) continue;
            if (!entry.Pool.Exists(entry.Id)) { End(entry); continue; }
            if (!BattleEffectData.SimulatesLocally(entry.Pool.Kind) && GameMain.gameTick - entry.LastCorrection >= 6)
            { entry.LastCorrection = GameMain.gameTick; outbound.Enqueue(Sample(entry, PlayerWeaponEvent.State)); }
        }
        tickEntries.Clear();
        eventScratch.Clear(); while (outbound.TryDequeue(out var sample)) eventScratch.Add(sample);
        if (eventScratch.Count > 0)
            foreach (var player in Multiplayer.Session.Server.Players.Connected.Values)
            {
                selectedScratch.Clear();
                foreach (var sample in eventScratch)
                    if (sample.Shot.Player == player.Id || sample.Star > 0 && sample.Star == player.Data.LocalStarId) selectedScratch.Add(sample);
                if (selectedScratch.Count > 0) player.SendPacket(Packet(selectedScratch));
            }
        eventScratch.Clear(); selectedScratch.Clear();
        while (initial.TryDequeue(out var request)) SendSnapshot(request.Connection, request.Star);
        CollectExpired();
    }
    private PlayerWeaponPacket Packet(IReadOnlyList<PlayerWeaponSample> samples) => new()
    { EpochHigh = epoch.High, EpochLow = epoch.Low, Data = PlayerWeaponSample.Encode(samples) };
    public void SendInitial(INebulaeConnection connection, int star) => initial.Enqueue((connection, star));
    private void SendSnapshot(INebulaeConnection connection, int star)
    {
        if (pools == null) return;
        selectedScratch.Clear();
        foreach (var entry in entries.Values)
            if (!entry.Ended && entry.Star == star && entry.Pool.Exists(entry.Id)) selectedScratch.Add(Sample(entry, PlayerWeaponEvent.State));
        if (selectedScratch.Count > 0) connection.SendPacket(Packet(selectedScratch));
        selectedScratch.Clear();
    }
    private void CollectExpired()
    {
        var tick = GameMain.gameTick;
        expiredEntries.Clear();
        foreach (var pair in entries) if (pair.Value.Ended && tick - pair.Value.EndTick > 600) expiredEntries.Add(pair.Key);
        foreach (var key in expiredEntries) entries.Remove(key);
        expiredEntries.Clear();
        RemoveExpired(cancelled, tick); RemoveExpired(settled, tick);
    }
    private void RemoveExpired(Dictionary<PlayerShotId, long> source, long tick)
    {
        expiredShots.Clear();
        foreach (var pair in source) if (tick - pair.Value > 600) expiredShots.Add(pair.Key);
        foreach (var key in expiredShots) source.Remove(key);
        expiredShots.Clear();
    }
    public void Dispose()
    {
        if (Multiplayer.Session?.IsClient == true) foreach (var entry in entries.Values) RemoveNative(entry);
        entries.Clear(); slots.Clear(); unsettled.Clear(); settled.Clear(); cancelled.Clear(); Resources.Clear();
        tickEntries.Clear(); eventScratch.Clear(); selectedScratch.Clear(); expiredEntries.Clear(); expiredShots.Clear();
        while (inbound.TryDequeue(out _)) { } while (outbound.TryDequeue(out _)) { }
        audio?.Dispose(); audio = null;
        pools = null; skills = null; hostSequence = 0;
    }
}
