using System;
using System.Collections.Generic;
using NebulaeModel.DataStructures;
using UnityEngine;

namespace NebulaeWorld.Combat;

/// <summary>Remembers consumed generations even after a short visual proxy is recycled.</summary>
internal sealed class BattleAudioGate
{
    private readonly Dictionary<(BattleEffectKind, int, int), long> generations = new();
    public bool TryPlay(BattleEffectKind kind, int id, long generation, int subtype = 0)
    {
        var key = (kind, id, subtype);
        if (generations.TryGetValue(key, out var previous) && generation <= previous) return false;
        generations[key] = generation;
        return true;
    }
}

/// <summary>Listener-local audio only. Never ticks a skill or writes to a combat pool.</summary>
internal sealed class BattleAudioPlayback : IDisposable
{
    private readonly SkillAudioLogic audio = new();
    private readonly Dictionary<int, (int PoolId, Cue Cue)> playing = new();
    private readonly List<int> expired = new();
    private int nextHandle;
    private int listenerPlanet;
    public long PlayedCount { get; private set; }
    public int ActiveCount => playing.Count;
    public bool IsPlaying(int handle) => playing.ContainsKey(handle);

    public BattleAudioPlayback()
    {
        audio.Init(GameMain.data);
        listenerPlanet = GameMain.localPlanet?.id ?? 0;
    }

    internal readonly struct Cue
    {
        public Cue(int proto, float life, int planet, VectorLF3 position, bool universal, bool continuous = false)
        { Proto = proto; Life = life; Planet = planet; Position = position; Universal = universal; Continuous = continuous; }
        public readonly int Proto, Planet;
        public readonly float Life;
        public readonly VectorLF3 Position;
        public readonly bool Universal, Continuous;
    }

    public static int PlayerProto(BattleEffectKind kind) => kind switch
    {
        BattleEffectKind.MechaGroundLaser or BattleEffectKind.MechaSpaceLaser => 312,
        BattleEffectKind.MechaGroundGauss or BattleEffectKind.MechaSpaceGauss => 313,
        BattleEffectKind.MechaLocalCannon or BattleEffectKind.MechaSpaceCannon => 314,
        BattleEffectKind.MechaMissile => 315,
        BattleEffectKind.MechaPlasma => 316,
        BattleEffectKind.MechaShieldBurst => 168,
        _ => 0
    };

    public static bool Audible(int sourcePlanet, int localPlanet, double distance, double radius = 120) =>
        (sourcePlanet == 0 || sourcePlanet == localPlanet) && distance >= 0 && distance < radius;

    public static bool TryCue(BattleEffectKind kind, int subtype, object sample, out Cue cue)
    {
        var planet = 0;
        var position = VectorLF3.zero;
        var universal = false;
        var caster = default(SkillTarget);
        switch (sample)
        {
            case LocalLaserOneShot p: planet = p.astroId; position = p.beginPos; caster = Target(p.caster, planet); break;
            case LocalGeneralProjectile p: planet = p.astroId; position = p.pos; caster = Target(p.caster, planet); break;
            case LocalLaserContinuous p: planet = p.astroId; position = p.beginPos; caster = Target(p.caster, planet); break;
            case LocalCannonade p: planet = p.astroId; position = p.targetPos; caster = Target(p.caster, planet); break;
            case SpaceLaserOneShot p: position = p.beginPosU; universal = true; caster = p.caster; break;
            case GeneralProjectile p: position = p.uPos; universal = true; caster = p.caster; break;
            case GeneralMissile p: position = p.uPos; universal = true; caster = p.caster; break;
            case GeneralCannonade p: position = p.targetUPos; universal = true; caster = p.caster; break;
            case GeneralShieldBurst p: position = p.upos; universal = true; caster = p.caster; break;
            case ParticleData p: planet = p.astroId; universal = planet == 0; position = universal ? p.upos : (VectorLF3)p.pos; break;
            default: cue = default; return false;
        }
        var proto = PlayerProto(kind);
        var life = proto switch { 168 => 4.7f, 314 => 2.1f, 315 or 316 => 2f, _ => 1.2f };
        if (proto != 0 && Multiplayer.Session.Combat.IndexByPlayerId.TryGetValue(caster.id, out var index))
        {
            var player = Multiplayer.Session.Combat.Players[index];
            planet = player.planetId > 0 ? player.planetId : 0;
            universal = planet == 0;
            position = universal ? player.uPosition : (VectorLF3)player.position;
        }
        else if (kind == BattleEffectKind.Impact)
        {
            var pools = GameMain.spaceSector.skillSystem.hitEffects;
            proto = subtype > 0 && subtype < pools.Length ? pools[subtype]?.sfxProtoId ?? 0 : 0;
            life = 1f;
        }
        else if (kind == BattleEffectKind.TurretLaser) { proto = 302; life = 3f; }
        else if (proto == 0)
        {
            // The authoritative snapshot carries the caster; use its actual model's vanilla SFX.
            var factory = planet > 0 ? GameMain.galaxy.PlanetById(planet)?.factory : null;
            var model = 0;
            if (factory != null && caster.id > 0)
            {
                if (caster.type == ETargetType.Enemy && caster.id < factory.enemyCursor && factory.enemyPool[caster.id].id == caster.id)
                    model = factory.enemyPool[caster.id].modelIndex;
                else if (caster.type == ETargetType.None && caster.id < factory.entityCursor && factory.entityPool[caster.id].id == caster.id)
                    model = factory.entityPool[caster.id].modelIndex;
            }
            proto = model > 0 ? LDB.models.Select(model)?.prefabDesc.audioProtoId0 ?? 0 : 0;
            life = kind == BattleEffectKind.RaiderLaser ? 0.7f : 1.1f;
        }
        cue = new Cue(proto, life, planet, position, universal, kind == BattleEffectKind.TurretLaser);
        return proto > 0;
    }

    private static SkillTarget Target(SkillTargetLocal value, int planet) =>
        new() { id = value.id, type = value.type, astroId = planet };

    private static Vector3 LocalPosition(Cue cue) => cue.Universal
        ? Maths.QInvRotateLF(GameMain.data.relativeRot, cue.Position - GameMain.data.relativePos)
        : (Vector3)cue.Position;

    public int Play(Cue cue)
    {
        var localPlanet = GameMain.localPlanet?.id ?? 0;
        var position = LocalPosition(cue);
        if (!Audible(cue.Planet, localPlanet, (position - GameMain.mainPlayer.position).magnitude) || LDB.audios.Select(cue.Proto) == null)
            return 0;
        // Resolve player audio through the planet entry.
        ref var holder = ref audio.AddPlanetAudio(cue.Proto, cue.Life, localPlanet, position);
        if (localPlanet == 0) holder.bind.type = ETargetType.Player;
        var handle = ++nextHandle;
        playing[handle] = (holder.id, cue);
        PlayedCount++;
        return handle;
    }

    public void Sustain(int id)
    {
        if (playing.TryGetValue(id, out var voice) && voice.PoolId < audio.holders.cursor && audio.holders.buffer[voice.PoolId].id == voice.PoolId)
            audio.holders.buffer[voice.PoolId].time = 0;
    }

    public void Stop(int id)
    {
        if (!playing.TryGetValue(id, out var voice)) return;
        playing.Remove(id);
        audio.RemoveAudio(voice.PoolId);
    }

    public void Update()
    {
        if (listenerPlanet != (GameMain.localPlanet?.id ?? 0)) Clear();
        expired.Clear();
        foreach (var entry in playing)
        {
            var id = entry.Value.PoolId;
            if (id >= audio.holders.cursor || audio.holders.buffer[id].id != id) { expired.Add(entry.Key); continue; }
            ref var holder = ref audio.holders.buffer[id];
            holder.pos = LocalPosition(entry.Value.Cue);
            if (listenerPlanet == 0)
            {
                holder.SetVolumeByDist(((Vector3)holder.pos - GameMain.mainPlayer.position).magnitude);
                holder.multiplier = holder.volume; // Native player-bound audio has no distance falloff in space.
            }
        }
        foreach (var id in expired) playing.Remove(id);
        audio.LateUpdate();
        expired.Clear();
        foreach (var entry in playing)
            if (audio.holders.buffer[entry.Value.PoolId].id != entry.Value.PoolId) expired.Add(entry.Key);
        foreach (var id in expired) playing.Remove(id);
    }

    public void Clear()
    {
        foreach (var voice in playing.Values) audio.RemoveAudio(voice.PoolId);
        playing.Clear();
        listenerPlanet = GameMain.localPlanet?.id ?? 0;
    }
    public void Dispose() { playing.Clear(); audio.Free(); }
}
