using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Combat;
using UnityEngine;

namespace NebulaeWorld.Combat;

/// <summary>
/// Owns detached skill renderer pools. Never runs damage or target-selection logic.
/// </summary>
internal sealed class BattleVisualRenderer : IDisposable
{

    private readonly SkillSystem visuals;
    private readonly BattleAudioPlayback audio;
    private readonly Dictionary<int, Source> sources = new();
    private readonly List<int> expiredSources = new();
    private readonly List<(BattleEffectKind, int, long)> expiredEffects = new();
    private readonly Predicate<int> finishedAudio;

    public long DrawnEffectFrames { get; private set; }
    public long PlayedAudioCount => audio.PlayedCount;
    public int ActiveAudioCount => audio.ActiveCount;

    public BattleVisualRenderer()
    {
        if (Multiplayer.Session.IsDedicated) throw new InvalidOperationException("Headless sessions cannot create visual replicas");
        // The normal constructor rebuilds static combat tables. A renderer must not touch those tables.
        visuals = (SkillSystem)FormatterServices.GetUninitializedObject(typeof(SkillSystem));
        visuals.gameData = GameMain.data;
        visuals.sector = GameMain.spaceSector;
        visuals.history = GameMain.history;
        visuals.Init();
        visuals.SetForNewGame();
        audio = new BattleAudioPlayback();
        finishedAudio = id => !audio.IsPlaying(id);
    }

    public void Receive(int star, BattleVisualPacket packet, BattleVisualFrame frame)
    {
        if (packet.Reset) RemoveSource(star);
        if (!sources.TryGetValue(star, out var source)) sources[star] = source = new Source();
        var receiptTick = source.Clock.Sample(GameMain.gameTick, Time.realtimeSinceStartup,
            Multiplayer.Session.State.GetServerUPS(), GameMain.isPaused);
        foreach (var effect in frame.Effects)
        {
            var id = (effect.Kind, effect.Id, effect.Generation);
            var fixedOwner = BattleEffectData.IsCraft(effect.Kind) || BattleEffectData.IsDarkFogTurret(effect.Kind);
            if (fixedOwner && source.NativeOwned.ContainsKey(id)) continue;
            var native = effect.Kind is BattleEffectKind.GroundTowerLaser or BattleEffectKind.GroundTowerPlasma
                ? Multiplayer.Session.DarkFogTurrets.OwnsGroundEffect(effect)
                : NativeTurretEffect(effect.Kind, effect.SourceAstroId, effect.SourceObjectId);
            if ((!fixedOwner || !source.Effects.ContainsKey(id)) && native)
            {
                if (fixedOwner) source.NativeOwned[id] = GameMain.gameTick + Math.Max(120, effect.Life + 120);
                RemoveEffect(source, id);
                continue;
            }
            var silent = false;
            if (effect.Kind == BattleEffectKind.Impact && Multiplayer.Session.Impacts.Reconcile(effect, out silent)) continue;
            if (effect.End)
            {
                if (source.Effects.TryGetValue(id, out var ended)) ended.Timeline.Despawn(effect.Tick);
                continue;
            }
            if (!source.Effects.TryGetValue(id, out var proxy))
            {
                proxy = new EffectProxy { Kind = effect.Kind, Subtype = effect.Subtype, SourceAstroId = effect.SourceAstroId, SourceObjectId = effect.SourceObjectId, Silent = silent, PoolId = AllocateEffect(effect) };
                if (proxy.PoolId == 0) continue;
                source.Effects.Add(id, proxy);
            }
            if (proxy.Timeline.Observe(effect.Kind, effect.Tick, effect.Life, receiptTick))
            {
                proxy.PreviousSample = proxy.Sample;
                proxy.Sample = Decode(effect);
            }
        }
    }
    private int AllocateEffect(BattleEffectData effect) => effect.Kind switch
    {
        BattleEffectKind.RaiderLaser => visuals.raiderLasers.Add().id,
        BattleEffectKind.GroundTowerLaser => visuals.dfgTowerLasers.Add().id,
        BattleEffectKind.RangerPlasma => visuals.rangerPlasmas.Add().id,
        BattleEffectKind.GuardianPlasma => visuals.guardianPlasmas.Add().id,
        BattleEffectKind.GroundTowerPlasma => visuals.dfgTowerPlasmas.Add().id,
        BattleEffectKind.SpaceTowerLaser => visuals.dfsTowerLasers.Add().id,
        BattleEffectKind.LancerLaser => visuals.lancerLaserOneShots.Add().id,
        BattleEffectKind.SpaceTowerPlasma => visuals.dfsTowerPlasmas.Add().id,
        BattleEffectKind.LancerPlasma => visuals.lancerSpacePlasma.Add().id,
        BattleEffectKind.MechaGroundLaser => visuals.mechaLocalLaserOneShots.Add().id,
        BattleEffectKind.MechaGroundGauss => visuals.mechaLocalGaussProjectiles.Add().id,
        BattleEffectKind.MechaSpaceLaser => visuals.mechaSpaceLaserOneShots.Add().id,
        BattleEffectKind.MechaSpaceGauss => visuals.mechaSpaceGaussProjectiles.Add().id,
        BattleEffectKind.MechaPlasma => visuals.mechaPlasmas.Add().id,
        BattleEffectKind.MechaMissile => visuals.mechaMissiles.Add().id,
        BattleEffectKind.MechaLocalCannon => visuals.mechaLocalCannonades.Add().id,
        BattleEffectKind.MechaSpaceCannon => visuals.mechaSpaceCannonades.Add().id,
        BattleEffectKind.MechaShieldBurst => visuals.mechaShieldBursts.Add().id,
        BattleEffectKind.ExplosiveBomb => visuals.explosiveUnitBombs.Add().id,
        BattleEffectKind.LiquidBomb => visuals.liquidBombs.Add().id,
        BattleEffectKind.EMBomb => visuals.emCapsuleBombs.Add().id,
        BattleEffectKind.GroundLaser => visuals.fighterLasers.Add().id,
        BattleEffectKind.GroundPlasma => visuals.fighterPlasmas.Add().id,
        BattleEffectKind.GroundShieldPlasma => visuals.fighterShieldPlasmas.Add().id,
        BattleEffectKind.SpaceLaser => visuals.warshipTypeFLasers.Add().id,
        BattleEffectKind.SpacePlasmaF => visuals.warshipTypeFPlasmas.Add().id,
        BattleEffectKind.SpacePlasmaA => visuals.warshipTypeAPlasmas.Add().id,
        BattleEffectKind.TurretGauss => visuals.turretGaussProjectiles.Add().id,
        BattleEffectKind.TurretCannon => visuals.turretCannonades.Add().id,
        BattleEffectKind.TurretLocalPlasma => visuals.turretLocalPlasmas.Add().id,
        BattleEffectKind.TurretLaser => visuals.turretLaserContinuous.Add().id,
        BattleEffectKind.TurretDisturb => visuals.turretDisturbingWave.Add().id,
        BattleEffectKind.TurretMissile => visuals.turretMissiles.Add().id,
        BattleEffectKind.TurretPlasma => visuals.turretPlasmas.Add().id,
        BattleEffectKind.LancerSweep => visuals.lancerLaserSweeps.Add().id,
        BattleEffectKind.BomberProjectile => visuals.humpbackProjectiles.Add().id,
        BattleEffectKind.Impact when effect.Subtype > 0 && effect.Subtype < visuals.hitEffects.Length && visuals.hitEffects[effect.Subtype] != null =>
            visuals.hitEffects[effect.Subtype].Add().id,
        _ => 0
    };

    private static bool NativeTurretEffect(BattleEffectKind kind, int sourceAstro, int sourceObject = 0) =>
        BattleEffectData.IsCraft(kind) ? NativeCraftSimulation.CanPresent(sourceAstro, sourceObject) :
        BattleEffectData.IsDarkFogTurret(kind) ? Multiplayer.Session.DarkFogTurrets.CanRun(sourceAstro, sourceObject) :
        BattleEffectData.IsEnemyUnit(kind) ? NativeEnemyEffect(sourceAstro) :
        sourceAstro > 0 && sourceAstro == GameMain.localPlanet?.id && BattleEffectData.IsTurret(kind) &&
        Multiplayer.Session.AuthorityRuntime?.TurretReplica?.CanSimulatePlanet(GameMain.localPlanet.factory) == true;

    private static bool NativeEnemyEffect(int source)
    {
        if (source <= 0) return false;
        if (SpaceSector.IsPlanetAstro(source)) return source == GameMain.localPlanet?.id && NativeEnemySimulation.CanRun(GameMain.localPlanet.factory);
        var hive = GameMain.spaceSector?.GetHiveByAstroId(source);
        return NativeEnemySimulation.CanRun(hive);
    }

    private static object Decode(BattleEffectData effect)
    {
        var reader = RentReader(effect.Payload);
        switch (effect.Kind)
        {
            case BattleEffectKind.RaiderLaser:
            case BattleEffectKind.GroundTowerLaser:
            case BattleEffectKind.GroundLaser:
                var laser = new LocalLaserOneShot(); laser.Import(reader); laser.damage = 0; laser.mask = 0; return laser;
            case BattleEffectKind.RangerPlasma:
            case BattleEffectKind.GuardianPlasma:
            case BattleEffectKind.GroundTowerPlasma:
            case BattleEffectKind.GroundPlasma:
            case BattleEffectKind.GroundShieldPlasma:
                var local = new LocalGeneralProjectile(); local.Import(reader); local.damage = 0; local.damageIncoming = 0; local.mask = 0; return local;
            case BattleEffectKind.SpaceTowerLaser:
            case BattleEffectKind.LancerLaser:
            case BattleEffectKind.SpaceLaser:
                var spaceLaser = new SpaceLaserOneShot(); spaceLaser.Import(reader); spaceLaser.damage = 0; spaceLaser.mask = 0; return spaceLaser;
            case BattleEffectKind.TurretMissile:
                var missile = new GeneralMissile(); missile.Import(reader); missile.damage = 0; missile.damageIncoming = 0; missile.mask = 0; return missile;
            case BattleEffectKind.SpaceTowerPlasma:
            case BattleEffectKind.LancerPlasma:
            case BattleEffectKind.SpacePlasmaF:
            case BattleEffectKind.SpacePlasmaA:
            case BattleEffectKind.TurretPlasma:
                var projectile = new GeneralProjectile(); projectile.Import(reader); projectile.damage = 0; projectile.damageIncoming = 0; projectile.mask = 0; return projectile;
            case BattleEffectKind.LancerSweep:
                var sweep = new SpaceLaserSweep(); sweep.Import(reader); sweep.damage = 0; sweep.mask = 0;
                sweep.endPos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); return sweep;
            case BattleEffectKind.BomberProjectile:
                var bomb = new GeneralExpImpProjectile(); bomb.Import(reader); bomb.damage = 0; bomb.mask = 0; return bomb;
            case BattleEffectKind.Impact:
                var particle = new ParticleData(); particle.Import(reader); return particle;
            case BattleEffectKind.TurretLaser:
                var beam = new LocalLaserContinuous(); beam.Import(reader); beam.damage = 0; beam.mask = 0; beam.hitId = beam.hitIndex = beam.sfxId = 0; return beam;
            case BattleEffectKind.TurretGauss:
                var gauss = new LocalGeneralProjectile(); gauss.Import(reader); return NeutralizeNative(gauss);
            case BattleEffectKind.TurretCannon:
                var cannon = new LocalCannonade(); cannon.Import(reader); return NeutralizeNative(cannon);
            case BattleEffectKind.TurretLocalPlasma:
                var localPlasma = new GeneralProjectile(); localPlasma.Import(reader); return NeutralizeNative(localPlasma);
            case BattleEffectKind.TurretDisturb:
                var disturb = new LocalDisturbingWave(); disturb.Import(reader); return NeutralizeNative(disturb);
            case BattleEffectKind.MechaGroundLaser:
                var sample0 = new LocalLaserOneShot(); sample0.Import(reader); return NeutralizeNative(sample0);
            case BattleEffectKind.MechaGroundGauss:
                var sample1 = new LocalGeneralProjectile(); sample1.Import(reader); return NeutralizeNative(sample1);
            case BattleEffectKind.MechaSpaceLaser:
                var sample2 = new SpaceLaserOneShot(); sample2.Import(reader); return NeutralizeNative(sample2);
            case BattleEffectKind.MechaSpaceGauss:
                var sample3 = new GeneralProjectile(); sample3.Import(reader); return NeutralizeNative(sample3);
            case BattleEffectKind.MechaPlasma:
                var sample4 = new GeneralProjectile(); sample4.Import(reader); return NeutralizeNative(sample4);
            case BattleEffectKind.MechaMissile:
                var sample5 = new GeneralMissile(); sample5.Import(reader); return NeutralizeNative(sample5);
            case BattleEffectKind.MechaLocalCannon:
                var sample6 = new LocalCannonade(); sample6.Import(reader); return NeutralizeNative(sample6);
            case BattleEffectKind.MechaSpaceCannon:
                var sample7 = new GeneralCannonade(); sample7.Import(reader); return NeutralizeNative(sample7);
            case BattleEffectKind.MechaShieldBurst:
                var sample8 = new GeneralShieldBurst(); sample8.Import(reader); return NeutralizeNative(sample8);
            case BattleEffectKind.ExplosiveBomb:
                var sample9 = new Bomb_Explosive(); sample9.Import(reader); return NeutralizeNative(sample9);
            case BattleEffectKind.LiquidBomb:
                var sample10 = new Bomb_Liquid(); sample10.Import(reader); return NeutralizeNative(sample10);
            case BattleEffectKind.EMBomb:
                var sample11 = new Bomb_EMCapsule(); sample11.Import(reader); return NeutralizeNative(sample11);
            default: throw new InvalidDataException("Unknown combat visual effect");
        }
    }

    public void Draw()
    {
        DrawVisuals();
    }

    // A replicated particle's position is already expressed in the client's current render
    // frame, so the shared playback delay has to come back out of its own clock; otherwise
    // the vel * f(t) drift lags behind the position and drags the flash off a fast-moving
    // shield. A non-positive duration means "no known expiry", so only a bounded flash is
    // clamped.
    internal static int FlashClock(int captured, int duration, double age)
    {
        return duration > 0 ? Math.Min(duration, captured + (int)(age + 6.0)) : captured + (int)(age + 6.0);
    }

    // The neutralized field set and its default-value boxes are resolved once per effect
    // struct type. The original implementation ran six GetField lookups, six
    // Activator.CreateInstance calls and allocated a string array on every decoded frame.
    private static readonly string[] NeutralizedFieldNames =
        { "damage", "damageIncoming", "damageTotal", "damagePerObject", "abilityValue", "mask" };

    private sealed class NeutralizedFields
    {
        public FieldInfo[] Fields = [];
        public object[] Zeros = [];
    }

    private static readonly ConcurrentDictionary<Type, NeutralizedFields> NeutralizedByType = new();

    private static object NeutralizeNative<T>(T sample) where T : struct
    {
        var type = typeof(T);
        if (!NeutralizedByType.TryGetValue(type, out var cached))
        {
            var resolved = new List<FieldInfo>();
            var zeros = new List<object>();
            foreach (var name in NeutralizedFieldNames)
            {
                var field = type.GetField(name);
                if (field == null) continue;
                resolved.Add(field);
                zeros.Add(Activator.CreateInstance(field.FieldType));
            }
            cached = new NeutralizedFields { Fields = resolved.ToArray(), Zeros = zeros.ToArray() };
            NeutralizedByType[type] = cached;
        }
        object value = sample;
        var fields = cached.Fields;
        var zeroBoxes = cached.Zeros;
        for (var i = 0; i < fields.Length; i++) fields[i].SetValue(value, zeroBoxes[i]);
        return value;
    }

    // Decode runs once per replicated effect frame, so the reader is pooled per thread
    // instead of allocating a MemoryStream and a BinaryReader for every single frame.
    [ThreadStatic] private static MemoryStream decodeStream;
    [ThreadStatic] private static BinaryReader decodeReader;

    private static BinaryReader RentReader(byte[] payload)
    {
        if (decodeStream == null)
        {
            decodeStream = new MemoryStream(payload.Length);
            decodeReader = new BinaryReader(decodeStream);
        }
        decodeStream.SetLength(0);
        decodeStream.Write(payload, 0, payload.Length);
        decodeStream.Position = 0;
        return decodeReader;
    }

    private static VectorLF3 BlendUniversal(EffectProxy proxy, VectorLF3 current, float alpha)
    {
        if (alpha >= 1 || proxy.PreviousSample == null) return current;
        var previous = proxy.PreviousSample switch
        {
            GeneralProjectile p => p.uPos,
            GeneralMissile p => p.uPos,
            GeneralExpImpProjectile p => p.uPos,
            Bomb_Explosive p => p.uPos,
            Bomb_Liquid p => p.uPos,
            Bomb_EMCapsule p => p.uPos,
            _ => current
        };
        return previous + (current - previous) * alpha;
    }

    private void DrawVisuals()
    {
        if (GameMain.inOtherScene || GameCamera.main == null) { audio.Clear(); return; }
        var now = Time.realtimeSinceStartup;
        var paused = GameMain.isPaused;
        var tick = GameMain.gameTick;
        var star = GameMain.localStar?.id ?? 0;
        var ups = BattleVisualClock.NormalizeUps(Multiplayer.Session.State.GetServerUPS());
        var rotation = default(Quaternion);
        var origin = default(VectorLF3);
        var hasEffects = false;
        var frameReady = false;
        expiredSources.Clear();
        foreach (var entry in sources)
        {
            var key = entry.Key;
            if (key != star)
            { expiredSources.Add(key); continue; }
            var source = entry.Value;
            // Expiry uses logic ticks. Multiple renders of the same tick can reuse this pass.
            if (source.LastPruneTick != tick)
            {
                expiredEffects.Clear();
                foreach (var owned in source.NativeOwned)
                    if (owned.Value < tick) expiredEffects.Add(owned.Key);
                foreach (var id in expiredEffects) source.NativeOwned.Remove(id);
                source.LastPruneTick = tick;
            }
            source.AudioIds.RemoveWhere(finishedAudio);
            var clockTick = source.Clock.Sample(tick, now, ups, paused);
            if (source.Effects.Count > 0 && !frameReady)
            {
                // Every effect in this draw uses the same render frame.
                rotation = Quaternion.Inverse(GameMain.data.relativeRot);
                origin = GameMain.data.relativePos;
                frameReady = true;
            }
            expiredEffects.Clear();
            foreach (var pair in source.Effects)
            {
                var effect = pair.Value;
                if (!BattleEffectData.IsCraft(effect.Kind) && !BattleEffectData.IsDarkFogTurret(effect.Kind) &&
                    NativeTurretEffect(effect.Kind, effect.SourceAstroId, effect.SourceObjectId)) { expiredEffects.Add(pair.Key); continue; }
                var renderTick = effect.Timeline.RenderTick(clockTick);
                if (!effect.Timeline.IsBorn(renderTick)) continue;
                var remaining = effect.Timeline.Remaining(renderTick);
                if (remaining <= 0 || effect.Timeline.IsDespawned(renderTick)) { expiredEffects.Add(pair.Key); continue; }
                UpdateEffect(effect, effect.Timeline.Age(renderTick),
                    remaining, effect.Timeline.Blend(renderTick), rotation, origin);
                if (!effect.Silent && (effect.Kind == BattleEffectKind.TurretLaser ? !audio.IsPlaying(effect.AudioId) :
                        source.AudioGate.TryPlay(effect.Kind, pair.Key.Item2, pair.Key.Item3, effect.Subtype)) &&
                    BattleAudioPlayback.TryCue(effect.Kind, effect.Subtype, effect.Sample, out var cue))
                {
                    var audioId = audio.Play(cue);
                    if (audioId > 0) source.AudioIds.Add(audioId);
                    if (cue.Continuous) effect.AudioId = audioId;
                }
                if (effect.AudioId > 0) audio.Sustain(effect.AudioId);
                DrawnEffectFrames++;
            }
            foreach (var id in expiredEffects) RemoveEffect(source, id);
            hasEffects |= source.Effects.Count > 0;
        }
        foreach (var key in expiredSources) RemoveSource(key);
        audio.Update();
        DrawPools(hasEffects);
    }

    internal void DrawPools(bool hasEffects)
    {
        if (!hasEffects)
        {
            // Mecha missile smoke outlives its projectile. Preserve that native tail without
            // updating and drawing every other empty skill pool on idle frames.
            var trails = visuals.mechaMissileTrails;
            if (trails.trailCursor > 1)
            {
                visuals.mechaMissileRenderer.Update();
                trails.instMat.SetBuffer("_MissileBuffer", visuals.mechaMissileRenderer.instBuffer);
                trails.Render();
            }
            return;
        }
        // Only renderer methods are called. These detached pools never run damage or target selection.
        // The shield hit flash is scaled by _Radius, which vanilla PlayerEffect refreshes on the native
        // pool every frame; the mirror pool owns separate materials, so mirror the value here or the
        // replicated shield flash has no bubble to draw on.
        var shieldRadius = 2f;
        var localMecha = GameMain.mainPlayer?.mecha;
        if (localMecha != null) shieldRadius = Mathf.Max(2f, localMecha.energyShieldRadius * localMecha.energyShieldRadiusMultiplier);
        for (var index = 8; index <= 9; index++)
        {
            var pool = visuals.hitEffects[index];
            if (pool?.instMats is { Length: > 0 } materials && materials[0] != null)
                materials[0].SetFloat("_Radius", shieldRadius);
        }
        visuals.RendererUpdate();
        visuals.RendererDraw();
    }

    private void UpdateEffect(EffectProxy proxy, double age, int remaining, float alpha, Quaternion rotation, VectorLF3 origin)
    {
        var seconds = (float)(age / 60);
        switch (proxy.Kind)
        {
            case BattleEffectKind.TurretCannon:
            case BattleEffectKind.MechaLocalCannon:
                var cannon = (LocalCannonade)proxy.Sample;
                cannon.id = proxy.PoolId;
                var localProgress = proxy.PreviousSample is LocalCannonade oldCannon ? Mathf.Lerp(oldCannon.progress, cannon.progress, alpha) : cannon.progress;
                cannon.progress = BattleEffectMotion.CannonProgress(localProgress, cannon.distance, cannon.speed, age);
                (proxy.Kind == BattleEffectKind.TurretCannon ? visuals.turretCannonades : visuals.mechaLocalCannonades).buffer[proxy.PoolId] = cannon;
                break;
            case BattleEffectKind.MechaSpaceCannon:
                var spaceCannon = (GeneralCannonade)proxy.Sample;
                spaceCannon.id = proxy.PoolId;
                var spaceProgress = proxy.PreviousSample is GeneralCannonade oldSpaceCannon ? Mathf.Lerp(oldSpaceCannon.progress, spaceCannon.progress, alpha) : spaceCannon.progress;
                spaceCannon.progress = BattleEffectMotion.CannonProgress(spaceProgress, spaceCannon.distance, spaceCannon.speed, age);
                visuals.mechaSpaceCannonades.buffer[proxy.PoolId] = spaceCannon;
                break;
            case BattleEffectKind.TurretDisturb:
                var wave = (LocalDisturbingWave)proxy.Sample;
                wave.id = proxy.PoolId;
                var radius = proxy.PreviousSample is LocalDisturbingWave oldWave ? Mathf.Lerp(oldWave.currentDiffuseRadius, wave.currentDiffuseRadius, alpha) : wave.currentDiffuseRadius;
                wave.currentDiffuseRadius = Math.Min(wave.diffusionMaxRadius + wave.thickness * 0.5f, radius + wave.diffusionSpeed * seconds);
                visuals.turretDisturbingWave.buffer[proxy.PoolId] = wave;
                break;
            case BattleEffectKind.MechaShieldBurst:
                var burst = (GeneralShieldBurst)proxy.Sample;
                burst.id = proxy.PoolId; burst.life = remaining;
                burst.rpos = rotation * (Vector3)(burst.upos - origin);
                visuals.mechaShieldBursts.buffer[proxy.PoolId] = burst;
                break;
            case BattleEffectKind.ExplosiveBomb:
                var explosive = (Bomb_Explosive)proxy.Sample;
                explosive.id = proxy.PoolId; explosive.life = remaining;
                explosive.uPos = BlendUniversal(proxy, explosive.uPos, alpha) + explosive.uVel * seconds;
                visuals.explosiveUnitBombs.buffer[proxy.PoolId] = explosive;
                break;
            case BattleEffectKind.LiquidBomb:
                var liquid = (Bomb_Liquid)proxy.Sample;
                liquid.id = proxy.PoolId; liquid.life = remaining;
                liquid.uPos = BlendUniversal(proxy, liquid.uPos, alpha) + liquid.uVel * seconds;
                visuals.liquidBombs.buffer[proxy.PoolId] = liquid;
                break;
            case BattleEffectKind.EMBomb:
                var em = (Bomb_EMCapsule)proxy.Sample;
                em.id = proxy.PoolId; em.life = remaining;
                em.uPos = BlendUniversal(proxy, em.uPos, alpha) + em.uVel * seconds;
                visuals.emCapsuleBombs.buffer[proxy.PoolId] = em;
                break;
            case BattleEffectKind.RaiderLaser:
            case BattleEffectKind.GroundTowerLaser:
            case BattleEffectKind.MechaGroundLaser:
            case BattleEffectKind.GroundLaser:
                var laser = (LocalLaserOneShot)proxy.Sample;
                laser.id = proxy.PoolId; laser.life = -remaining;
                (proxy.Kind switch { BattleEffectKind.RaiderLaser => visuals.raiderLasers, BattleEffectKind.GroundTowerLaser => visuals.dfgTowerLasers, BattleEffectKind.MechaGroundLaser => visuals.mechaLocalLaserOneShots, _ => visuals.fighterLasers }).buffer[proxy.PoolId] = laser;
                break;
            case BattleEffectKind.MechaGroundGauss:
            case BattleEffectKind.TurretGauss:
            case BattleEffectKind.RangerPlasma:
            case BattleEffectKind.GuardianPlasma:
            case BattleEffectKind.GroundTowerPlasma:
            case BattleEffectKind.GroundPlasma:
            case BattleEffectKind.GroundShieldPlasma:
                var local = (LocalGeneralProjectile)proxy.Sample;
                local.id = proxy.PoolId; local.life = remaining;
                var currentPos = local.pos;
                local.pos = proxy.PreviousSample is LocalGeneralProjectile oldLocal ? Vector3.Lerp(oldLocal.pos, currentPos, alpha) : currentPos;
                local.pos = BattleEffectMotion.LocalPosition(local, age);
                (proxy.Kind == BattleEffectKind.RangerPlasma ? visuals.rangerPlasmas :
                    proxy.Kind == BattleEffectKind.GuardianPlasma ? visuals.guardianPlasmas :
                    proxy.Kind == BattleEffectKind.GroundTowerPlasma ? visuals.dfgTowerPlasmas :
                    proxy.Kind == BattleEffectKind.MechaGroundGauss ? visuals.mechaLocalGaussProjectiles :
                    proxy.Kind == BattleEffectKind.TurretGauss ? visuals.turretGaussProjectiles :
                    proxy.Kind == BattleEffectKind.GroundPlasma ? visuals.fighterPlasmas : visuals.fighterShieldPlasmas).buffer[proxy.PoolId] = local;
                break;
            case BattleEffectKind.MechaSpaceLaser:
            case BattleEffectKind.SpaceTowerLaser:
            case BattleEffectKind.LancerLaser:
            case BattleEffectKind.SpaceLaser:
                var spaceLaser = (SpaceLaserOneShot)proxy.Sample;
                spaceLaser.id = proxy.PoolId; spaceLaser.life = -remaining; spaceLaser.deltaPos = spaceLaser.endVelU * seconds;
                (proxy.Kind == BattleEffectKind.SpaceTowerLaser ? visuals.dfsTowerLasers : proxy.Kind == BattleEffectKind.LancerLaser ? visuals.lancerLaserOneShots : proxy.Kind == BattleEffectKind.MechaSpaceLaser ? visuals.mechaSpaceLaserOneShots : visuals.warshipTypeFLasers).buffer[proxy.PoolId] = spaceLaser;
                break;
            case BattleEffectKind.MechaMissile:
            case BattleEffectKind.TurretMissile:
                var missile = (GeneralMissile)proxy.Sample;
                missile.id = proxy.PoolId; missile.life += (int)age;
                if (proxy.PreviousSample is GeneralMissile oldMissile) missile.uRot = Quaternion.Slerp(oldMissile.uRot, missile.uRot, alpha);
                missile.uPos = BattleEffectMotion.UniversalPosition(BlendUniversal(proxy, missile.uPos, alpha), missile.uVel, age);
                missile.vel = rotation * missile.uVel / 60f;
                var missilePool = proxy.Kind == BattleEffectKind.MechaMissile ? visuals.mechaMissiles : visuals.turretMissiles;
                missilePool.buffer[proxy.PoolId] = missile;
                var trails = proxy.Kind == BattleEffectKind.MechaMissile ? visuals.mechaMissileTrails : visuals.turretMissileTrails;
                trails.SetTrailCapacity(missilePool.capacity);
                trails.trailCursor = missilePool.cursor;
                if (missile.life > 0 && missile.nearAstroId > 0)
                {
                    var starAstro = missile.nearAstroId / 100 * 100;
                    var tick = (uint)(GameMain.gameTick & 0x7fffffff);
                    trails.smokePool[proxy.PoolId * trails.trailStride + tick % trails.trailStride] = new SmokeData
                    {
                        type = (uint)Math.Max(0, missile.modelIndex - 431),
                        createTime = tick,
                        astroId = starAstro,
                        pos = missile.uPos - GameMain.galaxy.astrosData[starAstro].uPos,
                        vel = missile.uVel
                    };
                }
                break;
            case BattleEffectKind.MechaSpaceGauss:
            case BattleEffectKind.MechaPlasma:
            case BattleEffectKind.SpaceTowerPlasma:
            case BattleEffectKind.LancerPlasma:
            case BattleEffectKind.SpacePlasmaF:
            case BattleEffectKind.SpacePlasmaA:
            case BattleEffectKind.TurretLocalPlasma:
            case BattleEffectKind.TurretPlasma:
                var projectile = (GeneralProjectile)proxy.Sample;
                projectile.id = proxy.PoolId; projectile.life = remaining;
                projectile.uPos = BattleEffectMotion.UniversalPosition(BlendUniversal(proxy, projectile.uPos, alpha), projectile.uVel, age);
                projectile.rPos = rotation * (Vector3)(projectile.uPos - origin);
                projectile.rVelObj = rotation * projectile.uVelObj / 60f;
                var pool = proxy.Kind == BattleEffectKind.SpaceTowerPlasma ? visuals.dfsTowerPlasmas :
                    proxy.Kind == BattleEffectKind.LancerPlasma ? visuals.lancerSpacePlasma :
                    proxy.Kind == BattleEffectKind.MechaSpaceGauss ? visuals.mechaSpaceGaussProjectiles :
                    proxy.Kind == BattleEffectKind.MechaPlasma ? visuals.mechaPlasmas : proxy.Kind == BattleEffectKind.SpacePlasmaF ? visuals.warshipTypeFPlasmas :
                    proxy.Kind == BattleEffectKind.SpacePlasmaA ? visuals.warshipTypeAPlasmas : proxy.Kind == BattleEffectKind.TurretLocalPlasma ? visuals.turretLocalPlasmas : visuals.turretPlasmas;
                pool.buffer[proxy.PoolId] = projectile;
                break;
            case BattleEffectKind.TurretLaser:
                var continuous = (LocalLaserContinuous)proxy.Sample;
                continuous.id = proxy.PoolId;
                visuals.turretLaserContinuous.buffer[proxy.PoolId] = continuous;
                break;
            case BattleEffectKind.LancerSweep:
                var sweep = (SpaceLaserSweep)proxy.Sample;
                sweep.id = proxy.PoolId; sweep.life = remaining;
                visuals.lancerLaserSweeps.buffer[proxy.PoolId] = sweep;
                break;
            case BattleEffectKind.BomberProjectile:
                var bomber = (GeneralExpImpProjectile)proxy.Sample;
                bomber.id = proxy.PoolId; bomber.life = remaining;
                bomber.uPos = BattleEffectMotion.UniversalPosition(BlendUniversal(proxy, bomber.uPos, alpha), bomber.uVel, age);
                bomber.rPos = rotation * (Vector3)(bomber.uPos - origin); bomber.rVelObj = rotation * bomber.uVelObj / 60f;
                visuals.humpbackProjectiles.buffer[proxy.PoolId] = bomber;
                break;
            case BattleEffectKind.Impact:
                var particle = (ParticleData)proxy.Sample;
                particle.id = proxy.PoolId;
                particle.time = FlashClock(particle.time, particle.duration, age);
                // Exactly the guard vanilla SkillSystem.GameTick applies: only world-frame
                // particles are re-derived from upos, because only their render origin moves.
                // An astro-local particle keeps the coordinates the simulation produced and
                // the shader converts them from the global astro buffer, which is what carries
                // it along with its astro. Re-deriving it from a world-space upos instead pins
                // the flash in space while the render frame turns with the planet.
                if (particle.astroId == 0)
                {
                    particle.pos = rotation * (Vector3)(particle.upos - origin);
                    particle.dir = rotation * particle.dir;
                    particle.vel = rotation * particle.vel;
                }
                visuals.hitEffects[proxy.Subtype].buffer[proxy.PoolId] = particle;
                break;
        }
    }

    public void RemoveSource(int key)
    {
        if (!sources.TryGetValue(key, out var source)) return;

        expiredEffects.Clear(); expiredEffects.AddRange(source.Effects.Keys);
        foreach (var id in expiredEffects) RemoveEffect(source, id);
        foreach (var id in source.AudioIds) audio.Stop(id);
        sources.Remove(key);
    }

    private void RemoveEffect(Source source, (BattleEffectKind, int, long) id)
    {
        if (!source.Effects.TryGetValue(id, out var effect)) return;
        audio.Stop(effect.AudioId);
        switch (effect.Kind)
        {
            case BattleEffectKind.RaiderLaser: visuals.raiderLasers.Remove(effect.PoolId); break;
            case BattleEffectKind.GroundTowerLaser: visuals.dfgTowerLasers.Remove(effect.PoolId); break;
            case BattleEffectKind.RangerPlasma: visuals.rangerPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.GuardianPlasma: visuals.guardianPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.GroundTowerPlasma: visuals.dfgTowerPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.SpaceTowerLaser: visuals.dfsTowerLasers.Remove(effect.PoolId); break;
            case BattleEffectKind.LancerLaser: visuals.lancerLaserOneShots.Remove(effect.PoolId); break;
            case BattleEffectKind.SpaceTowerPlasma: visuals.dfsTowerPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.LancerPlasma: visuals.lancerSpacePlasma.Remove(effect.PoolId); break;
            case BattleEffectKind.MechaGroundLaser: visuals.mechaLocalLaserOneShots.Remove(effect.PoolId); break;
            case BattleEffectKind.MechaGroundGauss: visuals.mechaLocalGaussProjectiles.Remove(effect.PoolId); break;
            case BattleEffectKind.MechaSpaceLaser: visuals.mechaSpaceLaserOneShots.Remove(effect.PoolId); break;
            case BattleEffectKind.MechaSpaceGauss: visuals.mechaSpaceGaussProjectiles.Remove(effect.PoolId); break;
            case BattleEffectKind.MechaPlasma: visuals.mechaPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.MechaMissile: visuals.mechaMissiles.Remove(effect.PoolId); break;
            case BattleEffectKind.MechaLocalCannon: visuals.mechaLocalCannonades.Remove(effect.PoolId); break;
            case BattleEffectKind.MechaSpaceCannon: visuals.mechaSpaceCannonades.Remove(effect.PoolId); break;
            case BattleEffectKind.MechaShieldBurst: visuals.mechaShieldBursts.Remove(effect.PoolId); break;
            case BattleEffectKind.ExplosiveBomb: visuals.explosiveUnitBombs.Remove(effect.PoolId); break;
            case BattleEffectKind.LiquidBomb: visuals.liquidBombs.Remove(effect.PoolId); break;
            case BattleEffectKind.EMBomb: visuals.emCapsuleBombs.Remove(effect.PoolId); break;
            case BattleEffectKind.GroundLaser: visuals.fighterLasers.Remove(effect.PoolId); break;
            case BattleEffectKind.GroundPlasma: visuals.fighterPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.GroundShieldPlasma: visuals.fighterShieldPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.SpaceLaser: visuals.warshipTypeFLasers.Remove(effect.PoolId); break;
            case BattleEffectKind.SpacePlasmaF: visuals.warshipTypeFPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.SpacePlasmaA: visuals.warshipTypeAPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.TurretMissile:
                var trails = visuals.turretMissileTrails;
                if (trails.smokePool != null && (effect.PoolId + 1) * trails.trailStride <= trails.smokePool.Length)
                    Array.Clear(trails.smokePool, effect.PoolId * trails.trailStride, trails.trailStride);
                visuals.turretMissiles.Remove(effect.PoolId);
                break;
            case BattleEffectKind.TurretPlasma: visuals.turretPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.TurretGauss: visuals.turretGaussProjectiles.Remove(effect.PoolId); break;
            case BattleEffectKind.TurretCannon: visuals.turretCannonades.Remove(effect.PoolId); break;
            case BattleEffectKind.TurretLocalPlasma: visuals.turretLocalPlasmas.Remove(effect.PoolId); break;
            case BattleEffectKind.TurretLaser: visuals.turretLaserContinuous.Remove(effect.PoolId); break;
            case BattleEffectKind.TurretDisturb: visuals.turretDisturbingWave.Remove(effect.PoolId); break;
            case BattleEffectKind.LancerSweep: visuals.lancerLaserSweeps.Remove(effect.PoolId); break;
            case BattleEffectKind.BomberProjectile: visuals.humpbackProjectiles.Remove(effect.PoolId); break;
            case BattleEffectKind.Impact: visuals.hitEffects[effect.Subtype].Remove(effect.PoolId); break;
        }
        source.Effects.Remove(id);
    }

    public void Dispose()
    {
        audio.Dispose();
        visuals.Free();
        sources.Clear();
    }

    private sealed class Source
    {
        public long LastPruneTick = long.MinValue;
        public readonly BattleVisualClock Clock = new();
        public readonly BattleAudioGate AudioGate = new();
        public readonly HashSet<int> AudioIds = new();

        public readonly Dictionary<(BattleEffectKind, int, long), EffectProxy> Effects = new();
        public readonly Dictionary<(BattleEffectKind, int, long), long> NativeOwned = new();
    }
    private sealed class EffectProxy
    {
        public BattleEffectKind Kind;
        public int Subtype;
        public int SourceAstroId;
        public int SourceObjectId;
        public int PoolId;
        public int AudioId;
        public bool Silent;
        public object Sample;
        public object PreviousSample;
        public readonly BattleEffectTimeline Timeline = new();
    }
}
