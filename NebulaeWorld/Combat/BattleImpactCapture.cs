using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Linq;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;

namespace NebulaeWorld.Combat;

internal sealed class BattleImpactCapture : IDisposable
{
    private readonly ConcurrentDictionary<(int Kind, int Id, long Born), Impact> authoritative = new();
    private readonly ConcurrentDictionary<(int Kind, int Id), long> predicted = new();
    private readonly Dictionary<int, DataPoolRenderer<ParticleData>> filtered = new();
    private long nextGeneration;
    private NativePlayerWeaponPool[] worldPools;
    private SkillSystem worldSkills;
    [ThreadStatic] private static WorldCapture current;
    private readonly BattleImpactHistory local = new();
    private long lastReceivedPrune = long.MinValue;
    private readonly Dictionary<(int, long), long> received = new();
    public long LocalImpactCount { get; private set; }
    public long ReconciledCount { get; private set; }
    public sealed class WorldCapture
    {
        internal int[] Snapshot;
        internal WorldCapture Previous;
        internal BattleEffectKind Origin = BattleEffectKind.Impact;
        internal int Astro, Caster;
        internal long Generation;
        internal bool SuppressAudio;
    }
    public static bool SuppressNativeAudio => current?.SuppressAudio == true;
    public static int EffectAstro(SkillSystem skills)
    {
        if (Multiplayer.Session?.IsServer != true || current == null) return skills.localAstroId;
        if (SpaceSector.IsPlanetAstro(current.Astro)) return current.Astro;
        var planet = CombatTargetContext.TargetMecha?.player?.planetId ?? 0;
        return planet > 0 ? planet : skills.localAstroId;
    }
    public WorldCapture BeginWorld(SkillSystem skills, IPoolElement value)
    {
        if (!ReferenceEquals(worldSkills, skills)) { worldSkills = skills; worldPools = NativePlayerWeaponPool.WorldEffects(skills); }
        var scope = new WorldCapture { Snapshot = Begin(skills), Previous = current };
        foreach (var pool in worldPools)
            if (pool.Exists(value.ID) && pool.Read(value.ID).Equals(value)) { scope.Origin = pool.Kind; break; }
        SkillTarget caster;
        switch (value)
        {
            case LocalGeneralProjectile p: caster = new SkillTarget { id = p.caster.id, type = p.caster.type, astroId = p.astroId }; break;
            case LocalLaserOneShot p: caster = new SkillTarget { id = p.caster.id, type = p.caster.type, astroId = p.astroId }; break;
            case SpaceLaserOneShot p: caster = p.caster; break;
            case GeneralProjectile p: caster = p.caster; break;
            case GeneralMissile p: caster = p.caster; break;
            case GeneralExpImpProjectile p: caster = p.caster; break;
            case SpaceLaserSweep p: caster = p.caster; break;
            default: caster = default; break;
        }
        scope.Astro = caster.astroId; scope.Caster = caster.id;
        if (Multiplayer.Session.IsClient && BattleEffectData.IsCraft(scope.Origin)) scope.Caster = NativeCraftSimulation.HostId(scope.Astro, scope.Caster);
        scope.Generation = SourceGeneration(scope.Origin, scope.Astro, scope.Caster);
        scope.SuppressAudio = local.Find(scope.Origin, scope.Astro, scope.Caster, scope.Generation, GameMain.gameTick, true) != null;
        current = scope; return scope;
    }
    public void EndWorld(SkillSystem skills, WorldCapture capture)
    {
        if (capture == null) return;
        try { End(skills, capture.Snapshot); }
        finally { current = capture.Previous; }
    }
    private static long SourceGeneration(BattleEffectKind kind, int astro, int id)
    {
        if (id <= 0 || kind == BattleEffectKind.Impact) return 0;
        var ground = kind is BattleEffectKind.RaiderLaser or BattleEffectKind.RangerPlasma or BattleEffectKind.GuardianPlasma or
            BattleEffectKind.GroundTowerLaser or BattleEffectKind.GroundTowerPlasma or BattleEffectKind.GroundLaser or BattleEffectKind.GroundPlasma or BattleEffectKind.GroundShieldPlasma;
        var craft = BattleEffectData.IsCraft(kind); var runtime = Multiplayer.Session.AuthorityRuntime;
        ObjectKey key = default;
        if (Multiplayer.Session.IsClient)
            runtime.WorldReplica.TryFindKey(craft ? ground ? PoolKind.GroundCraft : PoolKind.SpaceCraft : ground ? PoolKind.GroundEnemy : PoolKind.SpaceEnemy,
                ground ? astro : 0, id, out key);
        else if (craft)
        { if (ground) runtime.CraftHost?.TryGetGroundCraftKey(astro, id, out key); else runtime.CraftHost?.TryGetSpaceCraftKey(id, out key); }
        else if (ground) runtime.GroundEnemyHost?.TryGetEnemyKey(astro, id, out key);
        else runtime.SpaceEnemyHost?.TryGetEnemyKey(id, out key);
        return key.Generation;
    }

    public bool Reconcile(BattleEffectData effect, out bool silent)
    {
        silent = false;
        if (effect.Kind != BattleEffectKind.Impact || effect.OriginKind == BattleEffectKind.Impact) return false;
        if (lastReceivedPrune != GameMain.gameTick)
        {
            lastReceivedPrune = GameMain.gameTick;
            foreach (var key in received.Where(x => GameMain.gameTick - x.Value > 600).Select(x => x.Key).ToArray()) received.Remove(key);
        }
        if (received.ContainsKey((effect.Id, effect.Generation))) return true;
        received[(effect.Id, effect.Generation)] = GameMain.gameTick;
        local.Prune(GameMain.gameTick);
        var match = local.Find(effect.OriginKind, effect.SourceAstroId, effect.SourceObjectId, effect.SourceGeneration, effect.Tick, false, effect.Subtype);
        if (match != null)
        {
            match.Matched = true; ReconciledCount++;
            silent = true;
            var pool = GameMain.spaceSector.skillSystem.hitEffects[match.Kind];
            if (match.Kind == effect.Subtype && match.Id > 0 && match.Id < pool.cursor && pool.buffer[match.Id].id == match.Id &&
                Math.Abs(GameMain.gameTick - pool.buffer[match.Id].time - match.Born) <= 1)
            {
                using var stream = new MemoryStream(effect.Payload, false); using var r = new BinaryReader(stream);
                var particle = new ParticleData(); particle.Import(r);
                particle.id = match.Id; particle.time = pool.buffer[match.Id].time; pool.buffer[match.Id] = particle;
            }
            else if (match.Kind != effect.Subtype)
            {
                if (match.Id > 0 && match.Id < pool.cursor && pool.buffer[match.Id].id == match.Id &&
                    Math.Abs(GameMain.gameTick - pool.buffer[match.Id].time - match.Born) <= 1) pool.Remove(match.Id);
                return false;
            }
            return true;
        }
        local.Add(new BattleImpactHistory.Entry
        {
            Origin = effect.OriginKind,
            Astro = effect.SourceAstroId,
            Caster = effect.SourceObjectId,
            Kind = effect.Subtype,
            Tick = effect.Tick,
            Authority = true,
            Generation = effect.SourceGeneration
        });
        return false; // Host hit had no local counterpart: keep the authoritative fallback.
    }

    public int[] Begin(SkillSystem skills)
    {
        if (!ReferenceEquals(skills, GameMain.spaceSector?.skillSystem) || skills.hitEffects == null) return null;
        var snapshot = new int[skills.hitEffects.Length * 2];
        for (var i = 0; i < skills.hitEffects.Length; i++)
        {
            snapshot[i * 2] = skills.hitEffects[i]?.cursor ?? 0;
            snapshot[i * 2 + 1] = skills.hitEffects[i]?.recycleCursor ?? 0;
        }
        return snapshot;
    }

    public void End(SkillSystem skills, int[] snapshot)
    {
        if (snapshot == null) return;
        for (var kind = 0; kind < skills.hitEffects.Length; kind++)
        {
            var pool = skills.hitEffects[kind];
            if (pool == null) continue;
            for (var id = Math.Max(1, snapshot[kind * 2]); id < pool.cursor; id++) Capture(kind, id, pool);
            for (var slot = pool.recycleCursor; slot < snapshot[kind * 2 + 1]; slot++) Capture(kind, pool.recycleIds[slot], pool);
        }
    }

    private void Capture(int kind, int id, DataPoolRenderer<ParticleData> pool)
    {
        if (id <= 0 || id >= pool.cursor || pool.buffer[id].id != id) return;
        var particle = pool.buffer[id];
        if (Multiplayer.Session.PlayerWeapons.CaptureImpact(kind, id, particle)) return;
        var born = GameMain.gameTick - particle.time;
        if (Multiplayer.Session.IsClient)
        {
            if (current == null || current.Origin == BattleEffectKind.Impact) { predicted[(kind, id)] = born; return; }
            local.Prune(GameMain.gameTick);
            var match = local.Find(current.Origin, current.Astro, current.Caster, current.Generation, born, true, kind, true);
            if (match != null) { match.Matched = true; pool.Remove(id); ReconciledCount++; return; }
            local.Add(new BattleImpactHistory.Entry
            {
                Origin = current.Origin,
                Astro = current.Astro,
                Caster = current.Caster,
                Kind = kind,
                Id = id,
                Tick = born,
                Born = born,
                Generation = current.Generation
            });
            LocalImpactCount++; return;
        }
        // The universal position is needed only to route the effect to a star. The particle itself
        // keeps the frame the simulation gave it: the hit-flash shader converts astro-local
        // coordinates through the global _Global_GalaxyAstroBuffer every frame, and that conversion
        // is what carries a planetary flash along with the rotating planet. Flattening astroId to a
        // world-space upos instead pins the flash to a fixed point in space while the render frame
        // keeps turning with the planet, so the flash slides off the shield bubble it belongs to.
        var upos = UniversalPosition(particle);
        var nearestStar = -1;
        var distance = double.MaxValue;
        foreach (var star in GameMain.galaxy.stars)
        {
            var delta = star.uPosition - upos;
            var sqr = delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
            if (sqr < distance) { distance = sqr; nearestStar = star.id; }
        }
        authoritative.TryAdd((kind, id, born), new Impact
        {
            Star = nearestStar,
            Born = born,
            Particle = particle,
            Kind = kind,
            Generation = Interlocked.Increment(ref nextGeneration),
            Origin = current?.Origin ?? BattleEffectKind.Impact,
            Astro = current?.Astro ?? 0,
            Caster = current?.Caster ?? 0,
            SourceGeneration = current?.Generation ?? 0
        });
    }

    /// <summary>Universal position of a captured particle, leaving the frame it was simulated in untouched.</summary>
    private static VectorLF3 UniversalPosition(in ParticleData particle)
    {
        if (particle.astroId == 0) return particle.upos;
        var planet = GameMain.galaxy.PlanetById(particle.astroId);
        if (planet != null) return planet.uPosition + (VectorLF3)(planet.runtimeRotation * particle.pos);
        GameMain.spaceSector.TransformFromAstro(particle.astroId, out var upos, particle.pos);
        return upos;
    }

    public void Append(ISet<int> stars, Action<int, BattleEffectData> append)
    {
        var tick = GameMain.gameTick;
        foreach (var entry in authoritative)
        {
            var impact = entry.Value;
            var life = impact.Particle.duration - (int)(tick - impact.Born);
            if (life <= 0) { authoritative.TryRemove(entry.Key, out _); continue; }
            if (impact.Published || !stars.Contains(impact.Star)) continue;
            append(impact.Star, Sample(impact));
            impact.Published = true;
        }
    }

    public IEnumerable<BattleEffectData> Snapshot(int star, long tick)
    {
        foreach (var impact in authoritative.Values)
            if (impact.Star == star && impact.Born + impact.Particle.duration > tick) yield return Sample(impact);
    }

    private static BattleEffectData Sample(Impact impact)
    {
        if (impact.Sample != null) return impact.Sample;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        impact.Particle.Export(writer);
        return impact.Sample = new BattleEffectData
        {
            Kind = BattleEffectKind.Impact,
            OriginKind = impact.Origin,
            SourceAstroId = impact.Astro,
            SourceObjectId = impact.Caster,
            SourceGeneration = impact.SourceGeneration,
            Id = impact.Particle.id,
            Subtype = impact.Kind,
            Generation = impact.Generation,
            Tick = impact.Born + impact.Particle.time,
            Life = impact.Particle.duration - impact.Particle.time,
            Payload = stream.ToArray()
        };
    }

    public bool RenderNative(DataPoolRenderer<ParticleData> pool)
    {
        if (predicted.IsEmpty) return false;
        var pools = GameMain.spaceSector?.skillSystem?.hitEffects;
        if (pools == null) return false;
        var kind = Array.IndexOf(pools, pool);
        if (kind < 0) return false; // The independent replica renderer is never filtered.
        if (!filtered.TryGetValue(kind, out var copy))
        {
            copy = (DataPoolRenderer<ParticleData>)Activator.CreateInstance(pool.GetType());
            copy.InitRenderer(Configs.combat.skillHitDescs[kind]);
            copy.ResetPool();
            filtered[kind] = copy;
        }
        copy.SetCapacity(pool.capacity);
        Array.Copy(pool.buffer, copy.buffer, pool.cursor);
        copy.cursor = pool.cursor;
        copy.recycleCursor = pool.recycleCursor;
        for (var id = 1; id < pool.cursor; id++)
        {
            if (!predicted.TryGetValue((kind, id), out var born)) continue;
            var particle = pool.buffer[id];
            if (particle.id == id && Math.Abs(GameMain.gameTick - particle.time - born) <= 1) copy.buffer[id].id = 0;
            else predicted.TryRemove((kind, id), out _);
        }
        copy.Render(); // A separate buffer, so native simulation is never changed by rendering.
        return true;
    }

    public void Dispose()
    {
        foreach (var pool in filtered.Values) pool.FreeRenderer();
        filtered.Clear(); predicted.Clear(); authoritative.Clear();
        local.Clear(); received.Clear(); worldPools = null; worldSkills = null;
    }

    private sealed class Impact
    {
        public int Star;
        public BattleEffectKind Origin;
        public int Astro, Caster;
        public long SourceGeneration;
        public long Born;
        public ParticleData Particle;
        public int Kind;
        public long Generation;
        public bool Published;
        public BattleEffectData Sample;
    }
}
