using System;
using System.Collections.Generic;
using System.IO;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority.Adapters;
using NebulaeModel.DataStructures;

namespace NebulaeWorld.Combat;

/// <summary>Local native aiming/fire with detached builder energy and host-selected targets.</summary>
internal sealed class NativeDarkFogTurrets : IDisposable
{
    [ThreadStatic] public static bool Active;
    private sealed class Entry
    {
        public object World;
        public int Enemy, Astro, Base;
        public long Received;
        public ObjectKey Target;
        public bool CounterAttack;
        public DFGTurretComponent Ground;
        public DFSTurretComponent Space;
        public EnemyBuilderComponent[] Builders = new EnemyBuilderComponent[1];
    }
    private readonly Dictionary<(int Astro, int Id), Entry> entries = new();
    private readonly List<(int Astro, int Id)> expired = new();
    public long SimulatedTicks { get; private set; }
    public static byte[] Export<T>(T turret) where T : struct, IPoolElement
    {
        return NativeComponentCodec<T>.Encode(turret);
    }
    public void Install(PlanetFactory factory, int id, GroundEnemyState state)
    {
        var key = (factory.planetId, id);
        if (state.NativeTurret?.Length is not > 0) { entries.Remove(key); return; }
        if (!NativeComponentCodec<DFGTurretComponent>.TryDecode(state.NativeTurret, out var turret) || turret.enemyId != id || turret.id <= 0) return;
        if (entries.TryGetValue(key, out var old) && ReferenceEquals(old.World, factory) && old.Ground.target.Equals(turret.target))
        { turret.localDir = old.Ground.localDir; turret.fire = old.Ground.fire; }
        turret.builderId = 0;
        turret.TurretDataSetup(ref factory.enemyPool[id]);
        var entry = old;
        if (entry == null) entries[key] = entry = new Entry();
        entry.World = factory; entry.Enemy = id; entry.Astro = factory.planetId; entry.Base = state.BaseId;
        entry.Ground = turret; entry.Space = default; entry.Received = GameMain.gameTick;
        entry.Target = Target(turret.target, factory.planetId); entry.CounterAttack = state.TurretCounterAttack;
        entry.Builders[0] = new EnemyBuilderComponent { state = state.BuilderState, energy = state.Energy };
    }
    public void Install(SpaceSector sector, int id, SpaceEnemyState state)
    {
        var key = (state.OriginAstroId, id);
        if (state.NativeTurret?.Length is not > 0) { entries.Remove(key); return; }
        if (!NativeComponentCodec<DFSTurretComponent>.TryDecode(state.NativeTurret, out var turret) || turret.enemyId != id || turret.id <= 0) return;
        if (entries.TryGetValue(key, out var old) && ReferenceEquals(old.World, sector) && old.Space.target.Equals(turret.target))
        { turret.localDir = old.Space.localDir; turret.fire = old.Space.fire; }
        turret.builderId = 0;
        var entry = old;
        if (entry == null) entries[key] = entry = new Entry();
        entry.World = sector; entry.Enemy = id; entry.Astro = state.OriginAstroId; entry.Base = 0;
        entry.Space = turret; entry.Ground = default; entry.CounterAttack = false;
        entry.Received = GameMain.gameTick; entry.Target = Target(turret.target, 0);
        entry.Builders[0] = new EnemyBuilderComponent { state = state.BuilderState, energy = state.Energy };
    }
    private static ObjectKey Target(SkillTargetLocal target, int planet)
    {
        if (target.id <= 0 || target.type == ETargetType.Player) return default;
        var kind = target.type == ETargetType.Craft ? (planet > 0 ? PoolKind.GroundCraft : PoolKind.SpaceCraft) : PoolKind.Entity;
        Multiplayer.Session.AuthorityRuntime.WorldReplica.TryFindKey(kind, planet, target.id, out var key);
        return key;
    }
    private static bool ReadyTarget(Entry entry, ref SkillTargetLocal target)
    {
        if (target.id <= 0) return false;
        if (target.type == ETargetType.Player)
        {
            var combat = Multiplayer.Session.Combat;
            return combat.IndexByPlayerId.TryGetValue(target.id, out var index) && combat.Players[index].isAlive &&
                (entry.World is not PlanetFactory f || combat.Players[index].planetId == f.planetId);
        }
        var replica = Multiplayer.Session.AuthorityRuntime.WorldReplica;
        if (!entry.Target.IsValid || !replica.Mirrors.TryGetValue(entry.Target, out var mirror)) return false;
        target.id = mirror.LocalComponentId;
        return target.id > 0;
    }
    public bool CounterAttackFor(PlanetFactory factory, int playerId, int enemyId) =>
        entries.TryGetValue((factory.planetId, enemyId), out var entry) && ReferenceEquals(entry.World, factory) &&
        entry.CounterAttack && GameMain.gameTick - entry.Received <= 120 &&
        entry.Builders[0].state >= 3 && enemyId > 0 && enemyId < factory.enemyCursor && factory.enemyPool[enemyId].id == enemyId &&
        entry.Ground.state == EDFTurretState.Aiming && entry.Ground.target.type == ETargetType.Player && entry.Ground.target.id == playerId &&
        GroundTurretCombat.TryPlayer(factory, playerId, out _);
    public bool TryGetGroundState(PlanetFactory factory, int enemyId, out DFGTurretComponent turret, out bool counterAttack)
    {
        turret = default; counterAttack = false;
        if (!entries.TryGetValue((factory.planetId, enemyId), out var entry) || !ReferenceEquals(entry.World, factory)) return false;
        turret = entry.Ground;
        counterAttack = CounterAttackFor(factory, turret.target.id, enemyId);
        return true;
    }
    // A pose mirror alone does not own a shot: a tower can fire and lose its target before
    // the next state capture. Keep that host effect unless a matching native shot exists.
    public bool OwnsGroundEffect(BattleEffectData effect)
    {
        if (!entries.TryGetValue((effect.SourceAstroId, effect.SourceObjectId), out var entry) || !Ready(entry) ||
            entry.World is not PlanetFactory || effect.Payload?.Length is not > 0) return false;
        SkillTargetLocal target;
        try
        {
            using var stream = new MemoryStream(effect.Payload, false); using var reader = new BinaryReader(stream);
            if (effect.Kind == BattleEffectKind.GroundTowerLaser) { var shot = new LocalLaserOneShot(); shot.Import(reader); target = shot.target; }
            else if (effect.Kind == BattleEffectKind.GroundTowerPlasma) { var shot = new LocalGeneralProjectile(); shot.Import(reader); target = shot.target; }
            else return false;
        }
        catch (IOException) { return false; }
        if (target.type != ETargetType.Player)
        {
            var key = Target(target, entry.Astro);
            if (!key.IsValid || !Multiplayer.Session.AuthorityRuntime.WorldReplica.Mirrors.TryGetValue(key, out var mirror)) return false;
            target.id = mirror.LocalComponentId;
        }
        return HasGroundShot(GameMain.spaceSector.skillSystem, effect.Kind, effect.SourceAstroId, effect.SourceObjectId, target);
    }

    internal static bool HasGroundShot(SkillSystem skills, BattleEffectKind kind, int astro, int enemy, SkillTargetLocal target)
    {
        if (kind == BattleEffectKind.GroundTowerLaser && skills?.dfgTowerLasers != null)
        {
            var pool = skills.dfgTowerLasers;
            using (pool.GetScopeLock()) for (var i = 1; i < pool.cursor; i++)
            {
                ref var shot = ref pool.buffer[i];
                if (shot.id == i && shot.life != 0 && shot.astroId == astro && shot.caster.type == ETargetType.Enemy && shot.caster.id == enemy && shot.target.Equals(target)) return true;
            }
        }
        else if (kind == BattleEffectKind.GroundTowerPlasma && skills?.dfgTowerPlasmas != null)
        {
            var pool = skills.dfgTowerPlasmas;
            using (pool.GetScopeLock()) for (var i = 1; i < pool.cursor; i++)
            {
                ref var shot = ref pool.buffer[i];
                if (shot.id == i && shot.life > 0 && shot.astroId == astro && shot.caster.type == ETargetType.Enemy && shot.caster.id == enemy && shot.target.Equals(target)) return true;
            }
        }
        return false;
    }
    public bool CanRun(int astro, int enemy = 0)
    {
        if (enemy != 0) return entries.TryGetValue((astro, enemy), out var entry) && Ready(entry);
        foreach (var candidate in entries.Values)
            if (candidate.Astro == astro && Ready(candidate)) return true;
        return false;
    }
    private static bool Ready(Entry e)
    {
        if (GameMain.gameTick - e.Received > 120 || e.Builders[0].state <= 0) return false;
        if (e.World is PlanetFactory f) return ReferenceEquals(GameMain.localPlanet?.factory, f) && NativeEnemySimulation.CanRun(f) &&
            e.Enemy < f.enemyCursor && f.enemyPool[e.Enemy].id == e.Enemy;
        var sector = (SpaceSector)e.World;
        var hive = sector.GetHiveByAstroId(e.Astro);
        return ReferenceEquals(GameMain.spaceSector, sector) && hive?.starData.id == GameMain.localStar?.id && NativeEnemySimulation.CanRun(hive) &&
            e.Enemy < sector.enemyCursor && sector.enemyPool[e.Enemy].id == e.Enemy;
    }
    public void Tick()
    {
        expired.Clear();
        foreach (var pair in entries)
        {
            var e = pair.Value;
            if (GameMain.gameTick - e.Received > 600) { expired.Add(pair.Key); continue; }
            if (!Ready(e)) continue;
            Active = true;
            try
            {
                if (e.World is PlanetFactory f)
                {
                    var b = e.Base > 0 && e.Base < f.enemySystem.bases.cursor ? f.enemySystem.bases.buffer[e.Base] : null;
                    if (b == null) continue;
                    var p = PlanetFactory.PrefabDescByModelIndex[f.enemyPool[e.Enemy].modelIndex];
                    e.Ground.InternalUpdate(p);
                    var original = e.Ground.target;
                    if (e.Ground.state == EDFTurretState.Aiming && e.Builders[0].state > 1 && ReadyTarget(e, ref e.Ground.target)) e.Ground.Aim(f, b, e.Builders, EAggressiveLevel.Passive);
                    e.Ground.target = original;
                    f.enemyAnimPool[e.Enemy] = EnemyTurretAnimation.Resolve(e.Ground.localDir, e.Ground.state, e.Ground.fire, e.Ground.heat,
                        e.Ground.sensorRange, e.Ground.attackRange, p.dfTurretAttackInterval);
                }
                else
                {
                    var sector = (SpaceSector)e.World; var hive = sector.GetHiveByAstroId(e.Astro);
                    var p = SpaceSector.PrefabDescByModelIndex[sector.enemyPool[e.Enemy].modelIndex];
                    e.Space.InternalUpdate(p);
                    var original = e.Space.target;
                    if (e.Space.state == EDFTurretState.Aiming && e.Builders[0].state > 1 && ReadyTarget(e, ref e.Space.target)) e.Space.Aim(hive, e.Builders, EAggressiveLevel.Passive);
                    e.Space.target = original;
                    sector.enemyAnimPool[e.Enemy] = EnemyTurretAnimation.Resolve(e.Space.localDir, e.Space.state, e.Space.fire, e.Space.heat,
                        e.Space.sensorRange, e.Space.attackRange, p.dfTurretAttackInterval);
                }
                SimulatedTicks++;
            }
            finally { Active = false; }
        }
        foreach (var key in expired) entries.Remove(key);
    }
    public void Dispose() { entries.Clear(); expired.Clear(); }
}
