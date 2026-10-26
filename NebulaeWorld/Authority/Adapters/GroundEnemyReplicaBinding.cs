#region

using System;
using System.Collections.Generic;
using NebulaeModel.Authority;

#endregion

namespace NebulaeWorld.Authority.Adapters;

/// <summary>
/// Writes host ground-enemy facts into the client's local pools.
/// </summary>
/// <remarks>
/// <para>
/// The game-side <see cref="IReplicaMirrorObserver"/> for <see cref="PoolKind.GroundEnemy"/>. Thin by
/// design: it guards the apply window, tracks which factory instance a scope's binding belongs to,
/// and delegates membership decisions to the pure <see cref="GroundEnemyBinding"/> core. Received
/// facts are installed inside the frame-boundary apply window; local motion advances separately.
/// </para>
/// <para>
/// A re-imported factory is a new pool with new indexes. An instance change replaces the core
/// wholesale — without releasing the old shells into the fresh pool — because the old local slot
/// numbers are lies about it. The next baseline or state record converges the scope; nothing is
/// guessed in between.
/// </para>
/// <para>
/// Unit components resume native behavior from complete server checkpoints. Base manufacture,
/// final damage and lifecycle commits remain server-owned. Despawn uses the pure removal path; the binding core
/// never calls a Kill path
/// that would generate drops, statistics or experience.
/// </para>
/// </remarks>
internal sealed class GroundEnemyReplicaBinding : IReplicaMirrorObserver, IReplicaDeathObserver, IReplicaBaselineReadiness, IReplicaFrameObserver
{
    private sealed class ScopeBinding
    {
        public PlanetFactory Factory;
        public EnemyData[] EnemyPool;
        public GroundEnemyBinding Core;
        public GroundEnemyReplicaPools Pools;
        public long CheckedFrame;
    }

    private readonly Func<bool> applyWindowOpen;
    private readonly Dictionary<int, ScopeBinding> scopes = [];
    private bool frameOpen;
    private long frame;

    public void BeginReplicaFrame() { frameOpen = true; frame++; }
    public void EndReplicaFrame(Action<ScopeKey, Action> apply)
    {
        frameOpen = false;
        Exception failure = null;
        foreach (var pair in scopes)
            if (pair.Value.Pools?.BatchOpen == true)
                try { apply(new ScopeKey(PoolKind.GroundEnemy, pair.Key), pair.Value.Pools.EndBatch); }
                catch (Exception error) { failure ??= error; }
        if (failure != null) throw failure;
    }

    public ClientWorldReplica Replica { get; set; }
    public long RefusalsOutsideApplyWindow { get; private set; }
    public long DeferredBaselines { get; private set; }

    public bool IsReadyForBaseline(ScopeKey scope) => scope.Kind != PoolKind.GroundEnemy ||
        (FactoryFor(scope.Scope)?.planet.factoryLoaded == true && GameMain.spaceSector?.skillSystem != null);

    public GroundEnemyReplicaBinding(Func<bool> applyWindowOpen = null)
    {
        this.applyWindowOpen = applyWindowOpen ?? ReplicaApplyContext.IsSessionApplyActive;
    }

    public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state)
    {
        if (scope.Kind != PoolKind.GroundEnemy) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            BindingDiagnostics.OutsideWindow(nameof(GroundEnemyReplicaBinding), scope);
            return;
        }
        var binding = BindingFor(scope);
        if (binding.Factory == null || binding.Core == null) return;
        binding.Core.ApplyState(key, state);
    }

    public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
    {
        if (scope.Kind != PoolKind.GroundEnemy) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            BindingDiagnostics.OutsideWindow(nameof(GroundEnemyReplicaBinding), scope);
            return;
        }
        var factory = FactoryFor(scope.Scope);
        if (factory == null)
        {
            DeferredBaselines++;
            return;
        }
        var binding = BindingFor(scope);
        var keep = new HashSet<int>();
        factory.enemySystem.units.Reset();
        for (var id = 1; id < factory.enemyCursor; id++) factory.enemyPool[id].unitId = 0;
        foreach (var member in members) keep.Add(member.Key.NativeId);
        var pools = new GroundEnemyReplicaPools(scope.Scope);
        for (var id = 1; id < factory.enemyCursor; id++)
            if (factory.enemyPool[id].id == id && !keep.Contains(id)) pools.RemoveEnemyShell(id);
        binding.Core.ReconcileBaseline(members);
    }

    public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
    {
        if (scope.Kind != PoolKind.GroundEnemy) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            BindingDiagnostics.OutsideWindow(nameof(GroundEnemyReplicaBinding), scope);
            return;
        }
        if (scopes.TryGetValue(scope.Scope, out var binding) && binding.Core != null)
        {
            binding.Core.RemoveMember(key);
        }
    }

    /// <summary>
    /// A death the host published. Presented before <see cref="OnMemberRemoved"/> releases the
    /// shell, so the wreckage spawns at the pose the last state record installed. A member that left
    /// the pool without dying — a unit going home — never reaches here.
    /// </summary>
    public void OnMemberDied(ScopeKey scope, in ObjectKey key)
    {
        if (scope.Kind != PoolKind.GroundEnemy) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            BindingDiagnostics.OutsideWindow(nameof(GroundEnemyReplicaBinding), scope);
            return;
        }
        if (scopes.TryGetValue(scope.Scope, out var binding) && binding.Core != null)
        {
            binding.Core.PlayDeath(key);
        }
    }

    private ScopeBinding BindingFor(ScopeKey scope)
    {
        if (!scopes.TryGetValue(scope.Scope, out var binding))
        {
            binding = new ScopeBinding();
            scopes.Add(scope.Scope, binding);
        }
        // Factory replacement is handled before this boundary's inbox is drained. Capacity
        // growth can still change the pool during installation, so retain that cheap guard.
        if (frameOpen && binding.CheckedFrame == frame && binding.Factory != null &&
            ReferenceEquals(binding.EnemyPool, binding.Factory.enemyPool)) return binding;
        var factory = FactoryFor(scope.Scope);
        if (binding.Factory != factory || binding.EnemyPool != factory?.enemyPool || binding.Core == null && factory != null)
        {
            if (binding.Factory == factory) binding.Pools?.EndBatch();
            else binding.Pools?.CancelBatch();
            binding.Factory = factory;
            binding.EnemyPool = binding.Factory?.enemyPool;
            binding.Pools = binding.Factory != null ? new GroundEnemyReplicaPools(scope.Scope) : null;
            binding.Core = binding.Pools != null ? new GroundEnemyBinding(scope.Scope, binding.Pools,
                (key, localId) => Replica?.SetLocalComponentBinding(key, localId)) : null;
        }
        if (frameOpen) { binding.Pools?.BeginBatch(factory); binding.CheckedFrame = frame; }
        return binding;
    }

    private static PlanetFactory FactoryFor(int planetId) => GameMain.galaxy?.PlanetById(planetId)?.factory;
}
