using System;
using System.Collections.Generic;
using System.IO;
using NebulaeModel.Networking;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Combat;
using NebulaeModel.Authority;

namespace NebulaeWorld.Combat;

internal sealed class BattleVisualManager : IDisposable
{
    private const int BatchSize = 512;
    private readonly BattleEffectStream stream = new();
    private readonly HashSet<int> observedStars = new();
    private readonly Dictionary<int, BattleVisualFrame> outgoing = new();
    private readonly Dictionary<(int Star, int Source, BattleEffectKind Kind), bool> replicationNeeds = new();
    private readonly Dictionary<int, CachedFrame> frames = new();
    private readonly List<int> expiredSources = new();
    private readonly List<(BattleEffectKind, int, long)> expiredEffects = new();
    private long nextSequence, lastTick = -1;
    private int renderedStar;
    public long DrawnEffectFrames => renderer?.DrawnEffectFrames ?? 0;
    public long PlayedAudioCount => renderer?.PlayedAudioCount ?? 0;
    public int ActiveAudioCount => renderer?.ActiveAudioCount ?? 0;
    private BattleVisualRenderer renderer;

    /// <summary>
    /// Reusable export buffer, one per thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every observed effect is exported here, and the stream copies the payload only when it
    /// actually publishes the effect. A suppressed effect — a stationary continuous beam, which the
    /// stream re-observes every correction interval only to discard it as unchanged — therefore
    /// costs an export and a comparison but no record and no owned array. Allocating a fresh stream,
    /// writer and array for each of those would be pure waste on the busiest path in combat.
    /// </para>
    /// <para>
    /// The writer is created with <c>leaveOpen</c> so disposing it would not matter, and the buffer
    /// is rewound rather than replaced, which is what keeps a long session from growing anything.
    /// The capture runs at the host frame boundary with the workers parked, so one buffer per thread
    /// is enough; the buffer's contents are never retained past the call that filled it.
    /// </para>
    /// </remarks>
    [ThreadStatic]
    private static MemoryStream exportBuffer;

    [ThreadStatic]
    private static BinaryWriter exportWriter;

    public void GameTick()
    {
        if (!Multiplayer.Session.IsGameLoaded || !GameMain.data.gameDesc.isCombatMode) return;
        var tick = GameMain.gameTick;
        if (lastTick == tick) return;
        lastTick = tick;
        if (Multiplayer.Session.IsServer) { CaptureWorld(); return; }
        var star = GameMain.localStar?.id ?? 0;
        expiredSources.Clear();
        foreach (var pair in frames)
        {
            expiredEffects.Clear();
            foreach (var effect in pair.Value.Effects)
                if (effect.Value.Kind != BattleEffectKind.TurretLaser && effect.Value.Tick + effect.Value.Life <= tick)
                    expiredEffects.Add(effect.Key);
            foreach (var id in expiredEffects) pair.Value.Effects.Remove(id);
            if (pair.Key != star && tick - pair.Value.Tick > 180) expiredSources.Add(pair.Key);
        }
        foreach (var id in expiredSources) frames.Remove(id);
        if (renderedStar != star)
        {
            if (renderedStar != 0) renderer?.RemoveSource(renderedStar);
            renderedStar = star;
            if (frames.TryGetValue(star, out var cached) && cached.Effects.Count > 0)
            {
                var initial = new BattleVisualFrame();
                initial.Effects.AddRange(cached.Effects.Values);
                Present(new BattleVisualPacket { StarId = star, Tick = tick }, initial);
            }
        }
    }

    private void CaptureWorld()
    {
        replicationNeeds.Clear();
        observedStars.Clear();
        foreach (var player in Multiplayer.Session.Server.Players.Connected.Values)
            if (player.Data.LocalStarId > 0) observedStars.Add(player.Data.LocalStarId);
        stream.BeginFrame();
        if (observedStars.Count == 0)
        {
            stream.Clear(); outgoing.Clear();
            Multiplayer.Session.Impacts.Append(observedStars, Enqueue);
            return;
        }
        var skills = GameMain.spaceSector?.skillSystem;
        if (skills == null) return;
        // Each native pool is scanned once, irrespective of the number of occupied stars.
        Collect(skills.raiderLasers.buffer, skills.raiderLasers.cursor, BattleEffectKind.RaiderLaser,
            x => x.id, x => x.life, x => StarOf(x.astroId), (x, w) => x.Export(w), x => x.astroId);
        Collect(skills.dfgTowerLasers.buffer, skills.dfgTowerLasers.cursor, BattleEffectKind.GroundTowerLaser,
            x => x.id, x => x.life, x => StarOf(x.astroId), (x, w) => x.Export(w), x => x.astroId);
        Collect(skills.rangerPlasmas.buffer, skills.rangerPlasmas.cursor, BattleEffectKind.RangerPlasma,
            x => x.id, x => x.life, x => StarOf(x.astroId), (x, w) => x.Export(w), x => x.astroId);
        Collect(skills.guardianPlasmas.buffer, skills.guardianPlasmas.cursor, BattleEffectKind.GuardianPlasma,
            x => x.id, x => x.life, x => StarOf(x.astroId), (x, w) => x.Export(w), x => x.astroId);
        Collect(skills.dfgTowerPlasmas.buffer, skills.dfgTowerPlasmas.cursor, BattleEffectKind.GroundTowerPlasma,
            x => x.id, x => x.life, x => StarOf(x.astroId), (x, w) => x.Export(w), x => x.astroId);
        Collect(skills.dfsTowerLasers.buffer, skills.dfsTowerLasers.cursor, BattleEffectKind.SpaceTowerLaser,
            x => x.id, x => x.life, x => StarOf(x.caster.astroId), (x, w) => x.Export(w), x => x.caster.astroId);
        Collect(skills.lancerLaserOneShots.buffer, skills.lancerLaserOneShots.cursor, BattleEffectKind.LancerLaser,
            x => x.id, x => x.life, x => StarOf(x.caster.astroId), (x, w) => x.Export(w), x => EnemyOrigin(x.caster.id, x.caster.astroId));
        Collect(skills.dfsTowerPlasmas.buffer, skills.dfsTowerPlasmas.cursor, BattleEffectKind.SpaceTowerPlasma,
            x => x.id, x => x.life, x => StarOf(x.caster.astroId), (x, w) => x.Export(w), x => x.caster.astroId);
        Collect(skills.lancerSpacePlasma.buffer, skills.lancerSpacePlasma.cursor, BattleEffectKind.LancerPlasma,
            x => x.id, x => x.life, x => StarOf(x.caster.astroId), (x, w) => x.Export(w), x => EnemyOrigin(x.caster.id, x.caster.astroId));
        Collect(skills.fighterLasers.buffer, skills.fighterLasers.cursor, BattleEffectKind.GroundLaser,
            x => x.id, x => x.life, x => StarOf(x.astroId), (x, w) => x.Export(w), x => x.astroId);
        Collect(skills.fighterPlasmas.buffer, skills.fighterPlasmas.cursor, BattleEffectKind.GroundPlasma,
            x => x.id, x => x.life, x => StarOf(x.astroId), (x, w) => x.Export(w), x => x.astroId);
        Collect(skills.fighterShieldPlasmas.buffer, skills.fighterShieldPlasmas.cursor, BattleEffectKind.GroundShieldPlasma,
            x => x.id, x => x.life, x => StarOf(x.astroId), (x, w) => x.Export(w), x => x.astroId);
        Collect(skills.warshipTypeFLasers.buffer, skills.warshipTypeFLasers.cursor, BattleEffectKind.SpaceLaser,
            x => x.id, x => x.life, x => StarOf(x.caster.astroId), (x, w) => x.Export(w), x => x.caster.astroId);
        Collect(skills.warshipTypeFPlasmas.buffer, skills.warshipTypeFPlasmas.cursor, BattleEffectKind.SpacePlasmaF,
            x => x.id, x => x.life, x => StarOf(x.caster.astroId), (x, w) => x.Export(w), x => x.caster.astroId);
        Collect(skills.warshipTypeAPlasmas.buffer, skills.warshipTypeAPlasmas.cursor, BattleEffectKind.SpacePlasmaA,
            x => x.id, x => x.life, x => StarOf(x.caster.astroId), (x, w) => x.Export(w), x => x.caster.astroId);
        Collect(skills.mechaLocalLaserOneShots.buffer, skills.mechaLocalLaserOneShots.cursor, BattleEffectKind.MechaGroundLaser,
            x => x.id, x => x.life, x => PlayerStar(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.mechaLocalGaussProjectiles.buffer, skills.mechaLocalGaussProjectiles.cursor, BattleEffectKind.MechaGroundGauss,
            x => x.id, x => x.life, x => PlayerStar(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.mechaSpaceLaserOneShots.buffer, skills.mechaSpaceLaserOneShots.cursor, BattleEffectKind.MechaSpaceLaser,
            x => x.id, x => x.life, x => PlayerStar(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.mechaSpaceGaussProjectiles.buffer, skills.mechaSpaceGaussProjectiles.cursor, BattleEffectKind.MechaSpaceGauss,
            x => x.id, x => x.life, x => PlayerStar(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.mechaPlasmas.buffer, skills.mechaPlasmas.cursor, BattleEffectKind.MechaPlasma,
            x => x.id, x => x.life, x => PlayerStar(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.mechaMissiles.buffer, skills.mechaMissiles.cursor, BattleEffectKind.MechaMissile,
            x => x.id, x => x.life, x => PlayerStar(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.mechaLocalCannonades.buffer, skills.mechaLocalCannonades.cursor, BattleEffectKind.MechaLocalCannon,
            x => x.id, x => x.life, x => PlayerStar(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.mechaSpaceCannonades.buffer, skills.mechaSpaceCannonades.cursor, BattleEffectKind.MechaSpaceCannon,
            x => x.id, x => x.life, x => PlayerStar(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.mechaShieldBursts.buffer, skills.mechaShieldBursts.cursor, BattleEffectKind.MechaShieldBurst,
            x => x.id, x => x.life, x => PlayerStar(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.explosiveUnitBombs.buffer, skills.explosiveUnitBombs.cursor, BattleEffectKind.ExplosiveBomb,
            x => x.id, x => x.life, x => PlayerStar(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.liquidBombs.buffer, skills.liquidBombs.cursor, BattleEffectKind.LiquidBomb,
            x => x.id, x => x.life, x => PlayerStar(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.emCapsuleBombs.buffer, skills.emCapsuleBombs.cursor, BattleEffectKind.EMBomb,
            x => x.id, x => x.life, x => PlayerStar(x.caster.id), (x, w) => x.Export(w));
        Collect(skills.turretMissiles.buffer, skills.turretMissiles.cursor, BattleEffectKind.TurretMissile,
            x => x.id, x => x.life, x => StarOf(x.caster.astroId), (x, w) => x.Export(w), x => x.caster.astroId);
        Collect(skills.turretGaussProjectiles.buffer, skills.turretGaussProjectiles.cursor, BattleEffectKind.TurretGauss,
            x => x.id, x => x.life, x => StarOf(x.astroId), (x, w) => x.Export(w), x => x.astroId);
        Collect(skills.turretCannonades.buffer, skills.turretCannonades.cursor, BattleEffectKind.TurretCannon,
            x => x.id, x => x.life, x => StarOf(x.astroId), (x, w) => x.Export(w), x => x.astroId);
        Collect(skills.turretLocalPlasmas.buffer, skills.turretLocalPlasmas.cursor, BattleEffectKind.TurretLocalPlasma,
            x => x.id, x => x.life, x => StarOf(x.caster.astroId), (x, w) => x.Export(w), x => x.caster.astroId);
        // Continuous beams end explicitly; unchanged beams need no repeated payload.
        Collect(skills.turretLaserContinuous.buffer, skills.turretLaserContinuous.cursor, BattleEffectKind.TurretLaser,
            x => x.id, x => x.fade > 0 ? (int)Math.Ceiling(x.fade * 1000000) : 0, x => StarOf(x.astroId), (x, w) => x.Export(w), x => x.astroId);
        Collect(skills.turretDisturbingWave.buffer, skills.turretDisturbingWave.cursor, BattleEffectKind.TurretDisturb,
            x => x.id, x => x.life, x => StarOf(x.astroId), (x, w) => x.Export(w), x => x.astroId);
        Collect(skills.turretPlasmas.buffer, skills.turretPlasmas.cursor, BattleEffectKind.TurretPlasma,
            x => x.id, x => x.life, x => StarOf(x.caster.astroId), (x, w) => x.Export(w), x => x.caster.astroId);
        Collect(skills.lancerLaserSweeps.buffer, skills.lancerLaserSweeps.cursor, BattleEffectKind.LancerSweep,
            x => x.id, x => x.life, x => StarOf(x.caster.astroId),
            (x, w) => { x.Export(w); w.Write(x.endPos.x); w.Write(x.endPos.y); w.Write(x.endPos.z); }, x => EnemyOrigin(x.caster.id, x.caster.astroId));
        Collect(skills.humpbackProjectiles.buffer, skills.humpbackProjectiles.cursor, BattleEffectKind.BomberProjectile,
            x => x.id, x => x.life, x => StarOf(x.caster.astroId), (x, w) => x.Export(w), x => EnemyOrigin(x.caster.id, x.caster.astroId));
        stream.EndFrame(GameMain.gameTick);
        foreach (var change in stream.Changes)
            if (observedStars.Contains(change.Star)) Enqueue(change.Star, change.Effect);
        Multiplayer.Session.Impacts.Append(observedStars, Enqueue);
        foreach (var pair in outgoing)
        {
            if (pair.Value.Effects.Count > 0) Broadcast(pair.Key, pair.Value);
            pair.Value.Effects.Clear();
        }
    }

    private static int StarOf(int astro)
    {
        if (astro > 1000000) return GameMain.spaceSector.GetHiveByAstroId(astro)?.starData.id ?? -1;
        return astro > 0 ? astro / 100 : -1;
    }

    private static int PlayerStar(int id)
    {
        var player = Multiplayer.Session.Server.Players.Get((ushort)id);
        return player?.Data.LocalStarId ?? (id == Multiplayer.Session.LocalPlayer.Id ? GameMain.localStar?.id ?? -1 : -1);
    }

    public static int RemainingLife(BattleEffectKind kind, int nativeLife)
    {
        if (nativeLife == int.MinValue) return 0;
        if (kind is BattleEffectKind.TurretMissile or BattleEffectKind.MechaMissile)
            return nativeLife > 0 ? 3600 - nativeLife : -nativeLife;
        return BattleEffectData.IsOneShotLaser(kind) ? Math.Abs(nativeLife) : nativeLife;
    }

    private void Collect<T>(T[] buffer, int cursor, BattleEffectKind kind,
        Func<T, int> id, Func<T, int> life, Func<T, int> starOf, Action<T, BinaryWriter> export, Func<T, int> sourceAstro = null) where T : struct
    {
        if (buffer == null) return;
        for (var i = 1; i < Math.Min(cursor, buffer.Length); i++)
        {
            var item = buffer[i];
            if (id(item) != i || Multiplayer.Session.PlayerWeapons.Owns(kind, i)) continue;
            var nativeLife = life(item);
            var remaining = RemainingLife(kind, nativeLife);
            if (remaining <= 0) continue;
            var star = starOf(item);
            if (!observedStars.Contains(star)) continue;
            var source = sourceAstro?.Invoke(item) ?? 0;
            if ((BattleEffectData.IsTurret(kind) || BattleEffectData.IsEnemyUnit(kind)) && source > 0 && !NeedsNativeEffect(star, source, kind)) continue;
            var tick = GameMain.gameTick;
            if (!stream.Observe(kind, i, star, nativeLife, tick, out var generation)) continue;
            if (kind is BattleEffectKind.TurretCannon or BattleEffectKind.MechaLocalCannon or
                    BattleEffectKind.MechaSpaceCannon or BattleEffectKind.TurretDisturb)
                remaining = item switch
                {
                    LocalCannonade cannon => BattleEffectMotion.CannonRemaining(cannon.progress, cannon.distance, cannon.speed),
                    GeneralCannonade cannon => BattleEffectMotion.CannonRemaining(cannon.progress, cannon.distance, cannon.speed),
                    LocalDisturbingWave wave => BattleEffectMotion.WaveRemaining(wave),
                    _ => remaining
                };
            var effectLife = kind == BattleEffectKind.TurretLaser ? 0 : remaining;
            var scratch = exportBuffer ??= new MemoryStream();
            var writer = exportWriter ??= new BinaryWriter(scratch, System.Text.Encoding.UTF8, true);
            // Rewind rather than replace: the previous effect's bytes are simply overwritten, and
            // the stream copies the payload only for an effect it actually publishes.
            scratch.SetLength(0);
            scratch.Position = 0;
            export(item, writer);
            writer.Flush();
            var caster = item switch
            {
                LocalLaserOneShot p => p.caster.id, LocalGeneralProjectile p => p.caster.id,
                SpaceLaserOneShot p => p.caster.id, GeneralProjectile p => p.caster.id, _ => 0
            };
            stream.Capture(kind, i, generation, tick, effectLife, source, scratch.GetBuffer(), (int)scratch.Length, caster);
        }
    }

    private void Enqueue(int star, BattleEffectData effect)
    {
        if (!outgoing.TryGetValue(star, out var frame)) outgoing[star] = frame = new BattleVisualFrame();
        frame.Effects.Add(effect);
        if (frame.Effects.Count < BatchSize) return;
        Broadcast(star, frame);
        frame.Effects.Clear();
    }

    private void Broadcast(int star, BattleVisualFrame frame)
    {
        var sequence = ++nextSequence;
        var encoded = new Dictionary<(int Planet, bool Turrets, bool Ground, bool Space), (byte[] Bytes, int Count)>();
        foreach (var player in Multiplayer.Session.Server.Players.Connected.Values)
            if (player.Data.LocalStarId == star)
            {
                var planet = player.Data.LocalPlanetId;
                var native = SimulatesTurrets(player.Id, planet);
                var ground = SimulatesEnemies(player.Id, planet, false);
                var space = SimulatesEnemies(player.Id, planet, true);
                var key = (planet, native, ground, space);
                if (!encoded.TryGetValue(key, out var data))
                {
                    var selected = new BattleVisualFrame();
                    foreach (var effect in frame.Effects)
                        if (effect.NeedsReplication(planet, native) && !NativeEnemyEffect(effect.Kind, effect.SourceAstroId, planet, ground, space)) selected.Effects.Add(effect);
                    data = (selected.Export(), selected.Effects.Count);
                    encoded.Add(key, data);
                }
                if (data.Count == 0) continue;
                var packet = new BattleVisualPacket
                { StarId = star, Tick = GameMain.gameTick, Sequence = sequence, Data = data.Bytes };
                player.SendPacket(packet);
            }
    }

    private static bool SimulatesTurrets(ushort playerId, int planet)
    {
        var host = Multiplayer.Session.AuthorityRuntime?.HostReplicator;
        return planet > 0 && host != null &&
            host.HasLiveSubscription(playerId, new ScopeKey(PoolKind.Turret, planet)) &&
            host.HasLiveSubscription(playerId, new ScopeKey(PoolKind.GroundEnemy, planet)) &&
            host.HasLiveSubscription(playerId, new ScopeKey(PoolKind.Base, planet)) &&
            host.HasLiveSubscription(playerId, new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector)) &&
            host.HasLiveSubscription(playerId, new ScopeKey(PoolKind.HiveSummary, AuthorityScope.Sector));
    }

    private static int EnemyOrigin(int id, int fallback)
    {
        var pool = GameMain.spaceSector?.enemyPool;
        return pool != null && id > 0 && id < pool.Length && pool[id].id == id ? pool[id].originAstroId : fallback;
    }

    private static bool SimulatesEnemies(ushort id, int planet, bool space)
    {
        var host = Multiplayer.Session.AuthorityRuntime?.HostReplicator;
        return host != null && (space || planet > 0) &&
            host.HasLiveSubscription(id, new ScopeKey(space ? PoolKind.SpaceEnemy : PoolKind.GroundEnemy, space ? 0 : planet)) &&
            host.HasLiveSubscription(id, new ScopeKey(space ? PoolKind.HiveSummary : PoolKind.Base, space ? 0 : planet)) &&
            host.HasLiveSubscription(id, new ScopeKey(space ? PoolKind.SpaceCraft : PoolKind.GroundCraft, space ? 0 : planet));
    }

    internal static bool NativeEnemyEffect(BattleEffectKind kind, int source, int planet, bool groundReady, bool spaceReady) =>
        BattleEffectData.IsEnemyUnit(kind) && source > 0 &&
        (SpaceSector.IsPlanetAstro(source) ? source == planet && groundReady : spaceReady);

    private bool NeedsNativeEffect(int star, int sourceAstro, BattleEffectKind kind)
    {
        var key = (star, sourceAstro, kind);
        if (replicationNeeds.TryGetValue(key, out var needed)) return needed;
        foreach (var player in Multiplayer.Session.Server.Players.Connected.Values)
            if (player.Data.LocalStarId == star)
            {
                var planet = player.Data.LocalPlanetId;
                var computed = BattleEffectData.IsTurret(kind) ? planet == sourceAstro && SimulatesTurrets(player.Id, planet) :
                    NativeEnemyEffect(kind, sourceAstro, planet, SimulatesEnemies(player.Id, planet, false), SimulatesEnemies(player.Id, planet, true));
                if (!computed) { replicationNeeds[key] = true; return true; }
            }
        replicationNeeds[key] = false; return false;
    }

    public void Receive(BattleVisualPacket packet, BattleVisualFrame frame)
    {
        if (!frames.TryGetValue(packet.StarId, out var cached)) frames[packet.StarId] = cached = new CachedFrame();
        if (packet.Sequence <= cached.Sequence) return;
        cached.Sequence = packet.Sequence;
        cached.Tick = packet.Tick;
        if (packet.Reset) cached.Effects.Clear();
        foreach (var effect in frame.Effects)
        {
            var key = (effect.Kind, effect.Id, effect.Generation);
            if (effect.End) cached.Effects.Remove(key);
            else cached.Effects[key] = effect;
        }
        // Cache join-time state even before Unity has loaded the world. The first local tick
        // installs the surviving effects; absence of periodic full frames must not lose a baseline.
        if (!Multiplayer.Session.IsGameLoaded || packet.StarId != GameMain.localStar?.id) return;
        if (renderedStar != packet.StarId)
        {
            if (renderedStar != 0) renderer?.RemoveSource(renderedStar);
            renderedStar = packet.StarId;
            var initial = new BattleVisualFrame();
            initial.Effects.AddRange(cached.Effects.Values);
            Present(packet, initial);
        }
        else Present(packet, frame);
    }

    private void Present(BattleVisualPacket packet, BattleVisualFrame frame)
    {
        if (Multiplayer.Session.IsDedicated) return;
        if (renderer == null && !frame.Effects.Exists(effect => !effect.End)) return;
        renderer ??= new BattleVisualRenderer();
        renderer.Receive(packet.StarId, packet, frame);
    }

    public void SendInitial(INebulaeConnection connection, int star)
    {
        if (star <= 0) return;
        var frame = new BattleVisualFrame();
        var reset = true;
        foreach (var effect in stream.Snapshot(star, GameMain.gameTick)) Add(effect);
        foreach (var effect in Multiplayer.Session.Impacts.Snapshot(star, GameMain.gameTick)) Add(effect);
        if (reset || frame.Effects.Count > 0) Send();

        void Add(BattleEffectData effect)
        {
            frame.Effects.Add(effect);
            if (frame.Effects.Count >= BatchSize) Send();
        }
        void Send()
        {
            var data = frame.Export();
            connection.SendPacket(new BattleVisualPacket
            { StarId = star, Tick = GameMain.gameTick, Sequence = ++nextSequence, Reset = reset, Data = data });
            reset = false;
            frame.Effects.Clear();
        }
    }

    public void Draw() => renderer?.Draw();

    public void Dispose()
    {
        renderer?.Dispose();
        renderer = null;
        frames.Clear(); stream.Clear(); outgoing.Clear(); observedStars.Clear();
    }

    private sealed class CachedFrame
    {
        public long Sequence, Tick;
        public readonly Dictionary<(BattleEffectKind, int, long), BattleEffectData> Effects = new();
    }
}
