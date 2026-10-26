#region

using System;
using System.Collections.Generic;

#endregion

namespace NebulaeModel.Authority;

/// <summary>Why a scope's replication needs to be rebuilt.</summary>
/// <remarks>
/// Every reason names an observed fact, not a guess about the cause: the recovery is planned from
/// what the replica could prove (a sequence gap, an unknown key, a digest that did not match), and
/// the same fact must always plan the same recovery so a failure is reproducible.
/// </remarks>
internal enum ScopeRecoveryReason : byte
{
    None = 0,

    /// <summary>A stream sequence was skipped; the events between are gone.</summary>
    StreamGap = 1,

    /// <summary>The canonical digest at a stream position both sides held did not match.</summary>
    DigestMismatch = 2,

    /// <summary>State or lifecycle referenced an object whose identity was never established.</summary>
    UnknownObject = 3,

    /// <summary>An object depends on a core topology (base/hive) the replica does not hold.</summary>
    UnknownCoreTopology = 4,

    /// <summary>A baseline could not be staged, decoded or installed.</summary>
    BaselineRefused = 5,

    /// <summary>The message violated the protocol rules (tombstone spawn, malformed bookkeeping).</summary>
    ProtocolViolation = 6,

    /// <summary>The events this subscriber still needed were evicted from the host log.</summary>
    SequenceEvicted = 7,

    /// <summary>The installed runtime object no longer agrees with its confirmed identity or protected state.</summary>
    RuntimeDrift = 8
}

/// <summary>One step of a recovery plan.</summary>
internal enum ScopeRecoveryActionKind : byte
{
    /// <summary>Stop applying this scope's input until a new baseline is installed.</summary>
    SuspendInput = 1,

    /// <summary>Request a fresh baseline for the broken scope itself.</summary>
    RequestScopeBaseline = 2,

    /// <summary>Request a fresh baseline for a scope the broken scope depends on, first.</summary>
    RequestDependencyBaseline = 3
}

/// <summary>One planned recovery step, addressed to a concrete scope.</summary>
internal readonly struct ScopeRecoveryAction
{
    public ScopeRecoveryAction(ScopeRecoveryActionKind kind, ScopeKey scope, ScopeRecoveryReason reason)
    {
        Kind = kind;
        Scope = scope;
        Reason = reason;
    }

    public ScopeRecoveryActionKind Kind { get; }

    public ScopeKey Scope { get; }

    public ScopeRecoveryReason Reason { get; }
}

/// <summary>The deterministic recovery a replica runs for one observed break.</summary>
internal readonly struct ScopeRecoveryPlan
{
    public ScopeRecoveryPlan(ScopeRecoveryAction[] actions)
    {
        Actions = actions;
    }

    public ScopeRecoveryAction[] Actions { get; }
}

/// <summary>
/// Plans the recovery for a broken scope: what to suspend, and which baselines to request.
/// </summary>
/// <remarks>
/// <para>
/// A stream gap only invalidates one scope's stream, so
/// only that scope is rebuilt. A missing dependency is different: an object whose core topology
/// (its base or hive) is unknown cannot be fixed by resending its own scope — the topology scope's
/// baseline is requested first, then the reporting scope's, so the dependency exists before the
/// object that references it is re-established.
/// </para>
/// <para>
/// The planner is pure and table-driven so the mapping stays one auditable place; a new break
/// reason added without a row here fails loudly instead of recovering by habit.
/// </para>
/// </remarks>
internal static class ScopeRecoveryPlanner
{
    /// <summary>
    /// Resolves the scope a pool kind's core topology lives in, or false when the kind has none.
    /// </summary>
    /// <remarks>
    /// A planetary base is displayed through the factory's entity graph, so its topology scope is
    /// the planet's entity scope. A hive's topology lives with the sector's space-enemy pool that
    /// carries its units. These are model-level statements about scope structure, not game reads.
    /// </remarks>
    public static bool TryGetDependencyScope(in ScopeKey scope, out ScopeKey dependency)
    {
        switch (scope.Kind)
        {
            case PoolKind.Base:
                dependency = new ScopeKey(PoolKind.Entity, scope.Scope);
                return dependency.IsValid;
            case PoolKind.Hive:
                dependency = new ScopeKey(PoolKind.SpaceEnemy, AuthorityScope.Sector);
                return dependency.IsValid;
            default:
                dependency = default;
                return false;
        }
    }

    /// <summary>Plans the recovery for one observed break. Same inputs, same plan, every time.</summary>
    public static ScopeRecoveryPlan Plan(ScopeRecoveryReason reason, in ScopeKey scope)
    {
        if (!scope.IsValid || reason == ScopeRecoveryReason.None)
        {
            return new ScopeRecoveryPlan(Array.Empty<ScopeRecoveryAction>());
        }

        var actions = new List<ScopeRecoveryAction>(3) { new(ScopeRecoveryActionKind.SuspendInput, scope, reason) };

        if (reason == ScopeRecoveryReason.UnknownCoreTopology &&
            TryGetDependencyScope(scope, out var dependency) && !dependency.Equals(scope))
        {
            actions.Add(new ScopeRecoveryAction(ScopeRecoveryActionKind.RequestDependencyBaseline, dependency, reason));
        }

        // Every reason ends with the broken scope's own baseline: a digest mismatch, an unknown
        // object, a refused baseline and a gap alike are all repaired by a complete restatement of
        // the scope, never by patching the suspected member.
        actions.Add(new ScopeRecoveryAction(ScopeRecoveryActionKind.RequestScopeBaseline, scope, reason));
        return new ScopeRecoveryPlan(actions.ToArray());
    }
}
