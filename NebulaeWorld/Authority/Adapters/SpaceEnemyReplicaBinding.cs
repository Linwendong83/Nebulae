#region

using System;
using System.Collections.Generic;
using NebulaeModel.Authority;

#endregion

namespace NebulaeWorld.Authority.Adapters;

/// <summary>
/// Writes host space-enemy facts into the client's sector pool.
/// </summary>
/// <remarks>
/// <para>
/// The game-side <see cref="IReplicaMirrorObserver"/> for <see cref="PoolKind.SpaceEnemy"/>. Thin by
/// design: it guards the apply window and delegates every decision to the pure
/// <see cref="SpaceEnemyBinding"/> core. All writes land inside the replica's apply call, which the
/// session only opens at the frame boundary.
/// </para>
/// <para>
/// A sector or enemy-pool replacement rebuilds local binding state. A missing sector defers the
/// baseline instead of guessing.
/// </para>
/// <para>
/// Unit components retain their native motion and picking fields, and the hive's unit behavior runs
/// locally while its scopes are live; the hive's decision AI (<c>DecisionAI</c>,
/// <c>InterLearningFromLocalSystem</c>, <c>InterLearningFromOtherSystem</c>), hatred and attacks
/// remain server-owned. Despawn uses the pure removal path; the binding core never calls a Kill path that would
/// generate drops, statistics or experience.
/// </para>
/// </remarks>
internal sealed class SpaceEnemyReplicaBinding : IReplicaMirrorObserver, IReplicaDeathObserver, IReplicaBaselineReadiness
{
    private readonly Func<bool> applyWindowOpen;
    private SpaceEnemyBinding core;
    private SpaceSector sector;
    private EnemyData[] enemyPool;

    public ClientWorldReplica Replica { get; set; }
    public long RefusalsOutsideApplyWindow { get; private set; }
    public long DeferredBaselines { get; private set; }

    public bool IsReadyForBaseline(ScopeKey scope) => scope.Kind != PoolKind.SpaceEnemy ||
        GameMain.spaceSector?.skillSystem != null;

    public SpaceEnemyReplicaBinding(Func<bool> applyWindowOpen = null)
    {
        this.applyWindowOpen = applyWindowOpen ?? DefaultApplyWindowOpen;
    }

    public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state)
    {
        if (scope.Kind != PoolKind.SpaceEnemy) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        var binding = CoreFor();
        if (binding == null) return;
        binding.ApplyState(key, state);
    }

    public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
    {
        if (scope.Kind != PoolKind.SpaceEnemy) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        if (GameMain.spaceSector == null)
        {
            DeferredBaselines++;
            return;
        }
        var binding = CoreFor();
        if (binding == null) return;
        var sector = GameMain.spaceSector;
        if (sector.dfHivesByAstro != null)
            foreach (var hive in sector.dfHivesByAstro) hive?.units?.Reset();
        for (var id = 1; id < sector.enemyCursor; id++) sector.enemyPool[id].unitId = 0;
        var keep = new HashSet<int>();
        foreach (var member in members) keep.Add(member.Key.NativeId);
        var pools = new SpaceEnemyReplicaPools();
        for (var id = 1; id < GameMain.spaceSector.enemyCursor; id++)
            if (GameMain.spaceSector.enemyPool[id].id == id && !keep.Contains(id)) pools.RemoveEnemyShell(id);
        binding.ReconcileBaseline(members);
    }

    public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
    {
        if (scope.Kind != PoolKind.SpaceEnemy) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        core?.RemoveMember(key);
    }

    /// <summary>
    /// A death the host published. Presented before <see cref="OnMemberRemoved"/> releases the
    /// shell, so the wreckage spawns at the pose the last state record installed. A member that left
    /// the pool without dying — a unit going home, a relay that landed — never reaches here.
    /// </summary>
    public void OnMemberDied(ScopeKey scope, in ObjectKey key)
    {
        if (scope.Kind != PoolKind.SpaceEnemy) return;
        if (!applyWindowOpen())
        {
            RefusalsOutsideApplyWindow++;
            return;
        }
        core?.PlayDeath(key);
    }

    private SpaceEnemyBinding CoreFor()
    {
        if (core == null || !ReferenceEquals(sector, GameMain.spaceSector) || enemyPool != GameMain.spaceSector?.enemyPool)
        {
            if (GameMain.spaceSector == null) return null;
            sector = GameMain.spaceSector;
            enemyPool = sector.enemyPool;
            core = new SpaceEnemyBinding(new SpaceEnemyReplicaPools());
        }
        return core;
    }

    private static bool DefaultApplyWindowOpen()
    {
        var session = Multiplayer.Session;
        return session?.AuthorityRuntime?.ApplyContext?.IsActiveOnCurrentThread ?? false;
    }
}
