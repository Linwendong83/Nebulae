using System;
using System.Collections.Generic;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;

namespace NebulaeWorld.Authority.Adapters;

internal sealed class TurretReplicaBinding : IReplicaMirrorObserver, IReplicaBaselineReadiness
{
    private readonly TurretMirror mirror;
    private readonly Dictionary<(int Planet, int Entity), (ObjectKey Key, TurretComponent[] Pool)> installed = new();
    public TurretReplicaBinding(AuthorityEpoch epoch) => mirror = new TurretMirror(epoch, Apply, Clear);
    public int PendingCount => mirror.PendingCount;
    public bool CanSimulatePlanet(PlanetFactory factory) => factory?.planet.factoryLoaded == true && Replica != null &&
        IsLive(PoolKind.Turret, factory.planetId) && IsLive(PoolKind.GroundEnemy, factory.planetId) &&
        IsLive(PoolKind.Base, factory.planetId) && IsLive(PoolKind.SpaceEnemy, 0) && IsLive(PoolKind.HiveSummary, 0);
    private bool IsLive(PoolKind kind, int scope) => Replica.Versions.TryGetScope(new ScopeKey(kind, scope), out var state) &&
        state.Phase == SubscriptionPhase.Live;
    public bool CanSimulate(PlanetFactory factory, int entity, long tick) =>
        CanSimulatePlanet(factory) &&
        HasInstalled(factory, entity) &&
        mirror.TryGet(factory.planetId, entity, out _, out var state) && tick > state.SampleTick &&
        TurretSnapshotAdapter.TryTurret(factory, entity, out var id) && factory.entityPool[entity].modelIndex == state.ModelIndex &&
        (byte)factory.defenseSystem.turrets.buffer[id].type == state.Type;
    public bool HasInstalled(PlanetFactory factory, int entity) => factory?.defenseSystem?.turrets != null &&
        installed.TryGetValue((factory.planetId, entity), out var entry) &&
        ReferenceEquals(entry.Pool, factory.defenseSystem.turrets.buffer);
    public void ClearScope(int planet) => mirror.ClearScope(planet);
    public ClientWorldReplica Replica { get; set; }
    public bool IsReadyForBaseline(ScopeKey scope) => scope.Kind != PoolKind.Turret ||
        GameMain.galaxy?.PlanetById(scope.Scope)?.factory?.planet.factoryLoaded == true;
    private static void RequireApply()
    {
        if (Multiplayer.Session?.AuthorityRuntime?.ApplyContext.IsActiveOnCurrentThread != true)
            throw new InvalidOperationException("Turret facts outside replica apply.");
    }
    public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] bytes)
    {
        if (scope.Kind != PoolKind.Turret) return;
        RequireApply();
        if (!key.IsValid || key.Kind != scope.Kind || key.Scope != scope.Scope || !TurretStateCodec.TryDecode(bytes, out var state) || state.EntityId != key.NativeId)
            throw new InvalidOperationException("Invalid turret state.");
        mirror.Put(key, state);
    }
    public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
    {
        if (scope.Kind != PoolKind.Turret) return;
        RequireApply();
        var decoded = new List<(ObjectKey, TurretState)>();
        foreach (var member in members)
        {
            if (!TurretStateCodec.TryDecode(member.State, out var state) || !member.Key.IsValid || member.Key.Kind != scope.Kind || member.Key.Scope != scope.Scope || state.EntityId != member.Key.NativeId)
                throw new InvalidOperationException("Invalid turret baseline.");
            decoded.Add((member.Key, state));
        }
        mirror.Install(scope.Scope, decoded);
    }
    public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
    {
        if (scope.Kind != PoolKind.Turret) return;
        RequireApply(); mirror.Remove(key);
    }
    private void Clear(ObjectKey key)
    {
        var f = GameMain.galaxy?.PlanetById(key.Scope)?.factory;
        var wasInstalled = installed.TryGetValue((key.Scope, key.NativeId), out var entry) && entry.Key == key &&
            ReferenceEquals(entry.Pool, f?.defenseSystem?.turrets?.buffer);
        installed.Remove((key.Scope, key.NativeId));
        if (!TurretSnapshotAdapter.TryTurret(f, key.NativeId, out var id)) return;
        ref var t = ref f.defenseSystem.turrets.buffer[id];
        if (wasInstalled && t.projectileId > 0) t.StopContinuousSkill(f.skillSystem, false);
        t.projectileId = 0; t.target = new SkillTarget { type = ETargetType.Enemy }; t.hatred0 = default; t.isAiming = t.isLockingTarget = false;
        t.searchPairBeginIndex = t.searchPairCount = t.muzzleFire = t.roundFire = 0;
    }
    public void RetryPending()
    {
        var context = Multiplayer.Session.AuthorityRuntime.ApplyContext;
        foreach (var planet in mirror.PendingPlanets)
            context.Run(new ApplyScope(new ScopeKey(PoolKind.Turret, planet)), () => mirror.Retry(planet));
    }

    private bool Apply(ObjectKey key, TurretState s)
    {
        var f = GameMain.galaxy?.PlanetById(key.Scope)?.factory;
        if (f?.planet.factoryLoaded != true || !TurretSnapshotAdapter.TryTurret(f, key.NativeId, out var id)) return false;
        if (f.entityPool[s.EntityId].modelIndex != s.ModelIndex || (byte)f.defenseSystem.turrets.buffer[id].type != s.Type) return false;
        ref var t = ref f.defenseSystem.turrets.buffer[id];
        var wasInstalled = installed.TryGetValue((key.Scope, key.NativeId), out var entry) && entry.Key == key &&
            ReferenceEquals(entry.Pool, f.defenseSystem.turrets.buffer);
        if (!wasInstalled)
        {
            t.projectileId = 0;
            t.searchPairBeginIndex = t.searchPairCount = 0;
            t.hatred0 = default;
            // Native construction already matched this turret, but those offsets belong
            // to the pre-install pool view. Rebuild after all replica streams settle.
            Multiplayer.Session?.AuthorityRuntime?.DarkFogReplica?.MarkSearchDirty(key.Scope);
        }
        else if (t.projectileId > 0 && (t.target.id != s.TargetId || t.target.astroId != s.TargetAstro))
        {
            t.StopContinuousSkill(f.skillSystem, false);
        }
        t.isAiming = (s.Flags & 1) != 0; t.isLockingTarget = (s.Flags & 2) != 0; t.activeSearch = (s.Flags & 4) != 0;
        var targetReady = TargetReady(s);
        t.target = targetReady ? new SkillTarget { type = ETargetType.Enemy, id = s.TargetId, astroId = s.TargetAstro } : new SkillTarget { type = ETargetType.Enemy };
        t.localDir = s.Direction; t.localTargetDir = s.TargetDirection; t.group = s.Group; t.vsSettings = (VSLayerMask)s.VsSettings;
        t.itemId = s.ItemId; t.itemCount = s.ItemCount; t.itemInc = s.ItemInc; t.bulletCount = s.BulletCount; t.itemBulletCount = s.ItemBulletCount;
        t.bulletDamage = s.BulletDamage; t.currentBulletInc = s.BulletInc; t.supernovaTick = s.SupernovaTick; t.supernovaStrength = s.SupernovaStrength;
        t.roundFire = s.RoundFire; t.muzzleFire = s.MuzzleFire; t.muzzleReset = s.MuzzleReset; t.muzzleIndex = s.MuzzleIndex; t.phasePos = s.PhasePos; t.aimt = s.AimTime;
        t.totalKillCount = t.lastTotalKillCount = s.KillCount; t.totalDamage = s.TotalDamage;
        installed[(key.Scope, key.NativeId)] = (key, f.defenseSystem.turrets.buffer);
        if (targetReady) NebulaeWorld.Combat.EnemyTargetIdentity.Observe(f, t, s.TargetGeneration);
        else
        {
            if (t.projectileId > 0) t.StopContinuousSkill(f.skillSystem, false);
            t.projectileId = 0; t.isAiming = t.isLockingTarget = false;
        }
        Replica?.SetLocalComponentBinding(key, id); return targetReady;
    }

    private static bool TargetReady(TurretState state)
    {
        if (state.TargetId == 0) return true;
        return NebulaeWorld.Combat.EnemyTargetIdentity.TryResolve(new SkillTarget
        { type = (ETargetType)state.TargetType, id = state.TargetId, astroId = state.TargetAstro }, out var key) &&
            key.Generation == state.TargetGeneration;
    }
}
