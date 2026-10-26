using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using UnityEngine;

namespace NebulaeWorld.Combat;

/// <summary>Pairs native launches with authoritative effects, independently of craft motion.</summary>
internal sealed class CraftWeaponPrediction
{
    private const int MatchWindow = 24;
    private readonly object gate = new();
    private readonly Dictionary<(BattleEffectKind, int), Shot> launches = new();
    private readonly List<Decision> awaiting = new();
    private readonly Dictionary<(BattleEffectKind, int, long), Shot> owned = new();
    private NativePlayerWeaponPool[] pools;
    private SkillSystem skills;
    private object world;
    private int planet, star;
    private long pruned = -1;

    internal sealed class Capture
    {
        internal ObjectKey Source;
        internal int Port, Muzzle0, Muzzle1;
        internal long Tick;
        internal int[] Cursors, Recycled;
    }
    private sealed class Shot
    {
        internal ObjectKey Source, Target;
        internal BattleEffectKind Kind;
        internal int Id, Caster, Port, Muzzle, Life;
        internal long Tick;
        internal bool Matched;
        internal NativePlayerWeaponPool Pool;
        internal Vector3 Direction;
    }
    private sealed class Decision
    {
        internal ObjectKey Source;
        internal BattleEffectKind Kind;
        internal long Tick;
    }

    private void EnsureContext(SkillSystem current)
    {
        var local = Multiplayer.Session.IsClient;
        var p = local ? GameMain.localPlanet?.id ?? 0 : 0;
        var s = local ? GameMain.localStar?.id ?? 0 : 0;
        if (!ReferenceEquals(skills, current) || !ReferenceEquals(world, GameMain.data) || p != planet || s != star)
        {
            ClearCore(); skills = current; world = GameMain.data; planet = p; star = s;
            if (skills != null) pools = NativePlayerWeaponPool.WorldEffects(skills).Where(p => BattleEffectData.IsCraft(p.Kind)).ToArray();
        }
        if (pruned == GameMain.gameTick) return;
        pruned = GameMain.gameTick;
        foreach (var pair in launches.ToArray())
            if (GameMain.gameTick - pair.Value.Tick > 360 || !Current(pair.Value.Source))
            { if (local) Remove(pair.Value); launches.Remove(pair.Key); }
        awaiting.RemoveAll(d => Math.Abs(GameMain.gameTick - d.Tick) > 360 || !Current(d.Source));
        foreach (var pair in owned.ToArray())
            if (GameMain.gameTick - pair.Value.Tick > 360 || !Current(pair.Value.Source)) owned.Remove(pair.Key);
    }

    internal static ObjectKey Key(bool craft, bool ground, int astro, int id, bool localId = false)
    {
        if (id <= 0) return default;
        var session = Multiplayer.Session;
        ObjectKey key = default;
        if (session.IsClient)
        {
            if (craft && localId) id = NativeCraftSimulation.HostId(astro, id);
            session.AuthorityRuntime?.WorldReplica?.TryFindKey(craft ? ground ? PoolKind.GroundCraft : PoolKind.SpaceCraft :
                ground ? PoolKind.GroundEnemy : PoolKind.SpaceEnemy, ground ? astro : 0, id, out key);
        }
        else if (craft)
        {
            if (ground) session.AuthorityRuntime?.CraftHost?.TryGetGroundCraftKey(astro, id, out key);
            else session.AuthorityRuntime?.CraftHost?.TryGetSpaceCraftKey(id, out key);
        }
        else if (ground) session.AuthorityRuntime?.GroundEnemyHost?.TryGetEnemyKey(astro, id, out key);
        else session.AuthorityRuntime?.SpaceEnemyHost?.TryGetEnemyKey(id, out key);
        return key;
    }
    private static bool Current(ObjectKey key) => key.IsValid &&
        Key(key.Kind is PoolKind.GroundCraft or PoolKind.SpaceCraft,
            key.Kind is PoolKind.GroundCraft or PoolKind.GroundEnemy, key.Scope, key.NativeId) == key;

    public Capture Begin(SkillSystem system, in UnitComponent unit, in CraftData craft, PrefabDesc desc, bool ground)
    {
        lock (gate)
        {
            EnsureContext(system);
            if (pools == null || Multiplayer.Session.IsClient && !NativeCraftSimulation.PredictsWeapons(ground ? craft.astroId : 0, craft.id)) return null;
            if (unit.fire0 > 0 && unit.fire1 > 0) return null;
            var source = Key(true, ground, craft.astroId, craft.id, true);
            if (!source.IsValid) return null;
            return new Capture
            {
                Source = source,
                Port = craft.port,
                Tick = GameMain.gameTick,
                Muzzle0 = unit.muzzleIndex0 >= desc.craftUnitMuzzleCount0 ? 0 : unit.muzzleIndex0,
                Muzzle1 = unit.muzzleIndex1 >= desc.craftUnitMuzzleCount1 ? 0 : unit.muzzleIndex1,
                Cursors = pools.Select(p => p.Cursor).ToArray(),
                Recycled = pools.Select(p => p.Recycled).ToArray()
            };
        }
    }
    public void End(Capture capture)
    {
        if (capture == null) return;
        lock (gate)
        {
            if (pools == null || !Current(capture.Source)) return;
            for (var i = 0; i < pools.Length; i++)
                foreach (var id in pools[i].Added(capture.Cursors[i], capture.Recycled[i]))
                {
                    var value = pools[i].Read(id);
                    if (!Describe(value, out var caster, out var target, out var direction)) continue;
                    if (Key(true, capture.Source.Kind == PoolKind.GroundCraft, caster.astroId, caster.id, true) != capture.Source) continue;
                    var shot = new Shot
                    {
                        Source = capture.Source,
                        Target = target.type == ETargetType.Enemy ? Key(false, SpaceSector.IsPlanetAstro(target.astroId), target.astroId, target.id) : default,
                        Kind = pools[i].Kind,
                        Id = id,
                        Caster = caster.id,
                        Port = capture.Port,
                        Muzzle = pools[i].Kind == BattleEffectKind.SpaceLaser ? capture.Muzzle1 : capture.Muzzle0,
                        Tick = capture.Tick,
                        Pool = pools[i],
                        Direction = direction,
                        Life = Math.Abs(NativePlayerWeaponPool.Life(value))
                    };
                    launches[(shot.Kind, id)] = shot;
                    if (!Multiplayer.Session.IsClient) continue;
                    // A host-owned effect may arrive before this client's corresponding fire tick.
                    var decision = awaiting.Where(d => d.Source == shot.Source && d.Kind == shot.Kind && Math.Abs(d.Tick - shot.Tick) <= MatchWindow)
                        .OrderBy(d => Math.Abs(d.Tick - shot.Tick)).FirstOrDefault();
                    if (decision != null) { Remove(shot); shot.Matched = true; awaiting.Remove(decision); }
                }
        }
    }

    public BattleEffectData Launch(BattleEffectKind kind, int id)
    {
        lock (gate)
        {
            EnsureContext(GameMain.spaceSector?.skillSystem);
            if (!launches.TryGetValue((kind, id), out var shot) || !Current(shot.Source)) return null;
            return new BattleEffectData
            {
                SourceGeneration = shot.Source.Generation,
                TargetGeneration = shot.Target.Generation,
                SourcePort = shot.Port,
                MuzzleIndex = shot.Muzzle,
                LaunchTick = shot.Tick
            };
        }
    }

    public bool Reconcile(BattleEffectData effect)
    {
        lock (gate)
        {
            EnsureContext(GameMain.spaceSector?.skillSystem);
            var ground = SpaceSector.IsPlanetAstro(effect.SourceAstroId);
            var source = Key(true, ground, effect.SourceAstroId, effect.SourceObjectId);
            if (!source.IsValid || source.Generation != effect.SourceGeneration || effect.LaunchTick < 0) return false;
            using var stream = new MemoryStream(effect.Payload, false);
            using var reader = new BinaryReader(stream);
            IPoolElement value;
            switch (effect.Kind)
            {
                case BattleEffectKind.GroundLaser: var a = new LocalLaserOneShot(); a.Import(reader); value = a; break;
                case BattleEffectKind.SpaceLaser: var b = new SpaceLaserOneShot(); b.Import(reader); value = b; break;
                case BattleEffectKind.GroundPlasma:
                case BattleEffectKind.GroundShieldPlasma: var c = new LocalGeneralProjectile(); c.Import(reader); value = c; break;
                default: var d = new GeneralProjectile(); d.Import(reader); value = d; break;
            }
            Describe(value, out _, out var target, out var direction);
            var targetKey = target.type == ETargetType.Enemy ? Key(false, SpaceSector.IsPlanetAstro(target.astroId), target.astroId, target.id) : default;
            var candidates = launches.Values.Where(s => !s.Matched && s.Source == source && s.Kind == effect.Kind &&
                Math.Abs(s.Tick - effect.LaunchTick) <= MatchWindow).OrderBy(s => Math.Abs(s.Tick - effect.LaunchTick)).ToArray();
            var match = candidates.FirstOrDefault(s => s.Target.IsValid && s.Target == targetKey &&
                s.Target.Generation == effect.TargetGeneration && s.Port == effect.SourcePort && s.Muzzle == effect.MuzzleIndex &&
                Vector3.Dot(s.Direction, direction) >= .9961947f && Exists(s));
            if (match != null)
            { match.Matched = true; owned[(effect.Kind, effect.Id, effect.Generation)] = match; return true; }
            if (candidates.Length > 0) { candidates[0].Matched = true; Remove(candidates[0]); }
            else awaiting.Add(new Decision { Source = source, Kind = effect.Kind, Tick = effect.LaunchTick });
            return false;
        }
    }
    public void EndEffect(BattleEffectData effect)
    {
        lock (gate)
        {
            var key = (effect.Kind, effect.Id, effect.Generation);
            if (!owned.TryGetValue(key, out var shot)) return;
            Remove(shot); owned.Remove(key);
        }
    }
    public void Tick() { lock (gate) EnsureContext(GameMain.spaceSector?.skillSystem); }
    private static bool Describe(IPoolElement value, out SkillTarget caster, out SkillTarget target, out Vector3 direction)
    {
        switch (value)
        {
            case LocalLaserOneShot a:
                caster = new SkillTarget { id = a.caster.id, type = a.caster.type, astroId = a.astroId };
                target = new SkillTarget { id = a.target.id, type = a.target.type, astroId = a.astroId };
                direction = (a.endPos - a.beginPos).normalized; return true;
            case LocalGeneralProjectile a:
                caster = new SkillTarget { id = a.caster.id, type = a.caster.type, astroId = a.astroId };
                target = new SkillTarget { id = a.target.id, type = a.target.type, astroId = a.astroId };
                direction = a.dir.normalized; return true;
            case SpaceLaserOneShot a: caster = a.caster; target = a.target; direction = ((Vector3)(a.endPosU - a.beginPosU)).normalized; return true;
            case GeneralProjectile a: caster = a.caster; target = a.target; direction = a.uVel.normalized; return true;
            default: caster = target = default; direction = default; return false;
        }
    }
    private bool Exists(Shot shot)
    {
        if (!launches.TryGetValue((shot.Kind, shot.Id), out var current) || !ReferenceEquals(current, shot) || !shot.Pool.Exists(shot.Id)) return false;
        var value = shot.Pool.Read(shot.Id);
        return Describe(value, out var caster, out _, out _) && caster.id == shot.Caster &&
            Math.Abs(Math.Abs(NativePlayerWeaponPool.Life(value)) + GameMain.gameTick - shot.Tick - shot.Life) <= 2;
    }
    private void Remove(Shot shot) { if (Exists(shot)) shot.Pool.Remove(shot.Id); }
    private void ClearCore()
    {
        if (Multiplayer.Session?.IsClient == true) foreach (var shot in launches.Values) Remove(shot);
        launches.Clear(); awaiting.Clear(); owned.Clear(); pools = null; pruned = -1;
    }
    public void Clear() { lock (gate) { ClearCore(); skills = null; world = null; } }
}
