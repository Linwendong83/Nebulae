
#region
using System.Collections.Generic;

#endregion

namespace NebulaeModel.Authority;

/// <summary>Owner classification stored in historical authority records.</summary>
internal enum ConstructionOwnerKind : byte
{
    Unknown = 0,
    Player = 1,
    BattleBase = 2
}

/// <summary>Historical NBAS schema, retained unchanged for existing authority records.</summary>
/// <remarks>
/// The optional NBAS block is now embedded after the players in the .server file. Its contents
/// are preserved for compatibility and never restore a second live resource/task/player model.
/// </remarks>
internal static class AuthoritySidecarSchema
{
    /// <summary>The only schema this build writes and reads.</summary>
    public const int Current = 1;
}

/// <summary>
/// One host-tracked player persisted in the sidecar.
/// </summary>
/// <remarks>
/// Only the durable identity and the presence the host believes are saved. The session seat and
/// the connection epoch are deliberately absent: they die with the session, and a reconnecting
/// player is re-seated by the registry with fresh values. A restored entry is offline until the
/// player actually joins, so it can never own combat or drone work.
/// </remarks>
internal sealed class AuthoritySavePlayer
{
    public string PersistentId;
    public HostPlayerRole Role;
    public int PlanetId;
    public int StarId;
    public bool IsAlive;
    public bool RepairEnabled;
    public bool BuildEnabled;
    public int DroneTotal;
}

/// <summary>
/// One persisted ledger balance.
/// </summary>
/// <remarks>
/// <para>
/// Player balances key by the durable <c>PersistentId</c> and restore unchanged under the new
/// epoch — that is the structural form of "之后重连取主机账本，不从旧本地存档覆盖".
/// </para>
/// <para>
/// Base balances key by the host's base <see cref="ObjectKey"/>. The saved epoch is not written
/// per account: it is stored once in the state header as provenance, and the restore re-keys the
/// base identity under the new epoch at the same scope, slot and generation. If the reloaded
/// world's adapter mints a different generation for that slot, re-seeding from the vanilla facts
///  reconciles; a wrong guess can never mix two worlds because the epoch differs.
/// </para>
/// </remarks>
internal sealed class AuthoritySaveAccount
{
    public LedgerOwnerKind OwnerKind;
    public string PersistentId;
    public int BaseScope;
    public int BaseNativeId;
    public long BaseGeneration;
    public LedgerResourceKind ResourceKind;
    public int ItemId;
    public bool IsDouble;
    public double DoubleBalance;
    public long LongBalance;
}

/// <summary>One persisted drone budget.</summary>
/// <remarks>
/// Only the owner and capacity <c>total</c> are saved. All restored slots are idle because task positions are
/// not persisted; stale <c>repairerCount</c> values are cleared on load.
/// </remarks>
internal sealed class AuthoritySaveBudget
{
    public ConstructionOwnerKind OwnerKind;
    public string PersistentId;
    public int BaseScope;
    public int BaseNativeId;
    public long BaseGeneration;
    public int Total;
}

/// <summary>
/// One task that was live when the world was saved.
/// </summary>
/// <remarks>
/// <para>
/// Tasks are provenance, not live state: the restore never re-inserts them into the task ledger.
/// Their owners cannot be seated yet (players have no session seat at load) and their target keys
/// belong to the saved epoch, so "restoring" them would either strand a budget slot on a seatless
/// owner or guess a new generation for the target, reviving stale work.
/// </para>
/// <para>
/// The load transaction reclaims them once (counted in the restore report), returns every slot to
/// idle, and the host re-derives repair work from its real damage records after load. That is what
/// makes "可恢复任务不丢" (re-dispatch from restored damage facts) hold without "不复制" breaking
/// (no double occupancy).
/// </para>
/// </remarks>
internal sealed class AuthoritySaveTask
{
    public long Sequence;
    public ConstructionOwnerKind OwnerKind;
    public string PersistentId;
    public int BaseScope;
    public int BaseNativeId;
    public long BaseGeneration;
    public PoolKind TargetKind;
    public int TargetScope;
    public int TargetNativeId;
    public long TargetGeneration;
    public ConstructionTaskKind TaskKind;
    public ConstructionTaskStage Stage;
    public long Revision;
    public long LastHostTick;
    public ConstructionCancelReason CancelReason;
}

/// <summary>
/// The host facts captured into one sidecar.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is host-only by construction: only a host authority session can capture, and
/// only the host's load applies. A client never reads or writes one.
/// </para>
/// <para>
/// The saved epoch is provenance only. The load always mints a fresh world epoch, so nothing in this file can
/// authorize anything in
/// the new one; packets from the saved epoch are refused by the envelope gate's epoch check.
/// </para>
/// </remarks>
internal sealed class AuthoritySaveState
{
    /// <summary>Schema this state was decoded from or should be written as.</summary>
    public int Schema;

    /// <summary>Persistent world identity the sidecar belongs to. A mismatch refuses the file.</summary>
    public string WorldId;

    /// <summary>Epoch of the world that saved this file. Provenance only, never reused.</summary>
    public AuthorityEpoch SavedEpoch;

    /// <summary>Host tick at save time, for diagnostics.</summary>
    public long SavedHostTick;

    public readonly List<AuthoritySavePlayer> Players = [];
    public readonly List<AuthoritySaveAccount> Accounts = [];
    public readonly List<AuthoritySaveBudget> Budgets = [];
    public readonly List<AuthoritySaveTask> Tasks = [];
}
