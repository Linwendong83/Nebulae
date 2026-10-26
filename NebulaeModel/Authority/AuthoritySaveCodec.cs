#region

using System;
using System.Text;

#endregion

namespace NebulaeModel.Authority;

/// <summary>
/// Size ceilings of the sidecar file. Every count a record block declares is checked against its
/// ceiling before anything is decoded, so a corrupt or hostile file cannot make the loader
/// allocate without bound.
/// </summary>
internal static class AuthoritySidecarLimits
{
    /// <summary>Maximum players in one sidecar. Matches a full room with headroom.</summary>
    public const int PlayersMax = 64;

    /// <summary>Maximum balances in one sidecar.</summary>
    public const int AccountsMax = 8192;

    /// <summary>Maximum drone budgets in one sidecar.</summary>
    public const int BudgetsMax = 1024;

    /// <summary>Maximum task records in one sidecar.</summary>
    public const int TasksMax = 4096;

    /// <summary>Maximum bytes of one persistent id (a SHA-256 hash as text is 64).</summary>
    public const int PersistentIdMaxBytes = 128;

    /// <summary>Maximum bytes of the world identity string (a GUID "N" form is 32).</summary>
    public const int WorldIdMaxBytes = 64;

    /// <summary>Hard ceiling on the whole file, so a truncated length prefix fails fast.</summary>
    public const int FileMaxBytes = 8 * 1024 * 1024;
}

/// <summary>
/// Reads historical authority sidecars for save migration.
/// </summary>
/// <remarks>
/// <para>
/// File layout: a 4-byte magic (<c>NBAS</c>), an int32 schema, then the bounded payload: world id,
/// saved epoch, saved host tick, and the four record blocks (players, accounts, budgets, tasks),
/// each introduced by an explicit count. Every string is length-prefixed and capped, every enum
/// byte is checked against its defined members, and the reader refuses a payload that ends before
/// the declared records do.
/// </para>
/// <para>
/// The reader runs on plain byte arrays so historical fixtures and rejection cases are testable
/// without a game process.
/// </para>
/// </remarks>
internal static class AuthoritySaveCodec
{
    /// <summary>Magic at offset 0 of every sidecar file: "Nebulae Authority Save".</summary>
    public const string Magic = "NBAS";

    private const int HeaderBytes = 8; // magic (4) + schema (4)

    /// <summary>
    /// Decodes a sidecar file. Refuses with a reason instead of guessing: a wrong magic, an
    /// unknown schema, a truncated record, an undefined enum byte or a count above its ceiling all
    /// reject the whole file.
    /// </summary>
    public static bool TryDecode(byte[] bytes, out AuthoritySaveState state, out string reason)
    {
        state = null;
        if (bytes == null || bytes.Length < HeaderBytes)
        {
            reason = "file is smaller than the sidecar header";
            return false;
        }
        if (bytes.Length > AuthoritySidecarLimits.FileMaxBytes)
        {
            reason = "file exceeds the sidecar size ceiling";
            return false;
        }
        if (Encoding.ASCII.GetString(bytes, 0, Magic.Length) != Magic)
        {
            reason = "file does not carry the authority sidecar magic";
            return false;
        }
        var schema = BitConverter.ToInt32(bytes, 4);
        if (schema != AuthoritySidecarSchema.Current)
        {
            reason = "unknown sidecar schema " + schema + " (this build supports schema " +
                     AuthoritySidecarSchema.Current + "); refusing to read a newer or foreign format";
            return false;
        }

        if (!AuthorityPayloadReader.TryCreate(bytes, HeaderBytes, bytes.Length - HeaderBytes,
                out var reader, out _))
        {
            reason = "sidecar payload does not fit the file";
            return false;
        }

        state = new AuthoritySaveState { Schema = schema };
        if (!reader.TryReadString(AuthoritySidecarLimits.WorldIdMaxBytes, out state.WorldId) ||
            !TryReadEpoch(ref reader, out state.SavedEpoch) ||
            !reader.TryReadLong(out state.SavedHostTick))
        {
            state = null;
            reason = "sidecar header payload is truncated";
            return false;
        }

        if (!reader.TryReadInt(out var playerCount) || playerCount < 0 ||
            playerCount > AuthoritySidecarLimits.PlayersMax)
        {
            state = null;
            reason = "player count " + playerCount + " is negative or above the ceiling";
            return false;
        }
        for (var i = 0; i < playerCount; i++)
        {
            if (!reader.TryReadString(AuthoritySidecarLimits.PersistentIdMaxBytes, out var persistentId) ||
                !reader.TryReadEnumByte<HostPlayerRole>(out var role) ||
                !reader.TryReadInt(out var planetId) ||
                !reader.TryReadInt(out var starId) ||
                !reader.TryReadByte(out var alive) ||
                !reader.TryReadByte(out var repair) ||
                !reader.TryReadByte(out var build) ||
                !reader.TryReadInt(out var droneTotal))
            {
                state = null;
                reason = "player record " + i + " is truncated or carries an unknown role";
                return false;
            }
            state.Players.Add(new AuthoritySavePlayer
            {
                PersistentId = persistentId,
                Role = role,
                PlanetId = planetId,
                StarId = starId,
                IsAlive = alive != 0,
                RepairEnabled = repair != 0,
                BuildEnabled = build != 0,
                DroneTotal = droneTotal
            });
        }

        if (!reader.TryReadInt(out var accountCount) || accountCount < 0 ||
            accountCount > AuthoritySidecarLimits.AccountsMax)
        {
            state = null;
            reason = "account count " + accountCount + " is negative or above the ceiling";
            return false;
        }
        for (var i = 0; i < accountCount; i++)
        {
            if (!reader.TryReadString(AuthoritySidecarLimits.PersistentIdMaxBytes, out var persistentId) ||
                !reader.TryReadEnumByte<LedgerOwnerKind>(out var ownerKind) ||
                !reader.TryReadInt(out var baseScope) ||
                !reader.TryReadInt(out var baseNativeId) ||
                !reader.TryReadLong(out var baseGeneration) ||
                !reader.TryReadEnumByte<LedgerResourceKind>(out var resourceKind) ||
                !reader.TryReadInt(out var itemId) ||
                !reader.TryReadByte(out var isDouble) ||
                !reader.TryReadLong(out var balance))
            {
                state = null;
                reason = "account record " + i + " is truncated or carries an unknown kind";
                return false;
            }
            var account = new AuthoritySaveAccount
            {
                PersistentId = persistentId,
                OwnerKind = ownerKind,
                BaseScope = baseScope,
                BaseNativeId = baseNativeId,
                BaseGeneration = baseGeneration,
                ResourceKind = resourceKind,
                ItemId = itemId,
                IsDouble = isDouble != 0
            };
            if (account.IsDouble)
            {
                account.DoubleBalance = BitConverter.Int64BitsToDouble(balance);
            }
            else
            {
                account.LongBalance = balance;
            }
            state.Accounts.Add(account);
        }

        if (!reader.TryReadInt(out var budgetCount) || budgetCount < 0 ||
            budgetCount > AuthoritySidecarLimits.BudgetsMax)
        {
            state = null;
            reason = "budget count " + budgetCount + " is negative or above the ceiling";
            return false;
        }
        for (var i = 0; i < budgetCount; i++)
        {
            if (!reader.TryReadString(AuthoritySidecarLimits.PersistentIdMaxBytes, out var persistentId) ||
                !reader.TryReadEnumByte<ConstructionOwnerKind>(out var ownerKind) ||
                !reader.TryReadInt(out var baseScope) ||
                !reader.TryReadInt(out var baseNativeId) ||
                !reader.TryReadLong(out var baseGeneration) ||
                !reader.TryReadInt(out var total))
            {
                state = null;
                reason = "budget record " + i + " is truncated or carries an unknown owner kind";
                return false;
            }
            state.Budgets.Add(new AuthoritySaveBudget
            {
                PersistentId = persistentId,
                OwnerKind = ownerKind,
                BaseScope = baseScope,
                BaseNativeId = baseNativeId,
                BaseGeneration = baseGeneration,
                Total = total
            });
        }

        if (!reader.TryReadInt(out var taskCount) || taskCount < 0 ||
            taskCount > AuthoritySidecarLimits.TasksMax)
        {
            state = null;
            reason = "task count " + taskCount + " is negative or above the ceiling";
            return false;
        }
        for (var i = 0; i < taskCount; i++)
        {
            if (!reader.TryReadString(AuthoritySidecarLimits.PersistentIdMaxBytes, out var persistentId) ||
                !reader.TryReadLong(out var sequence) ||
                !reader.TryReadEnumByte<ConstructionOwnerKind>(out var ownerKind) ||
                !reader.TryReadInt(out var baseScope) ||
                !reader.TryReadInt(out var baseNativeId) ||
                !reader.TryReadLong(out var baseGeneration) ||
                !reader.TryReadEnumByte<PoolKind>(out var targetKind) ||
                !reader.TryReadInt(out var targetScope) ||
                !reader.TryReadInt(out var targetNativeId) ||
                !reader.TryReadLong(out var targetGeneration) ||
                !reader.TryReadEnumByte<ConstructionTaskKind>(out var taskKind) ||
                !reader.TryReadEnumByte<ConstructionTaskStage>(out var stage) ||
                !reader.TryReadLong(out var revision) ||
                !reader.TryReadLong(out var lastHostTick) ||
                !reader.TryReadEnumByte<ConstructionCancelReason>(out var cancelReason))
            {
                state = null;
                reason = "task record " + i + " is truncated or carries an unknown enum value";
                return false;
            }
            state.Tasks.Add(new AuthoritySaveTask
            {
                PersistentId = persistentId,
                Sequence = sequence,
                OwnerKind = ownerKind,
                BaseScope = baseScope,
                BaseNativeId = baseNativeId,
                BaseGeneration = baseGeneration,
                TargetKind = targetKind,
                TargetScope = targetScope,
                TargetNativeId = targetNativeId,
                TargetGeneration = targetGeneration,
                TaskKind = taskKind,
                Stage = stage,
                Revision = revision,
                LastHostTick = lastHostTick,
                CancelReason = cancelReason
            });
        }

        if (!reader.EndOfPayload)
        {
            state = null;
            reason = "sidecar payload has " + reader.Remaining + " trailing bytes";
            return false;
        }
        reason = "decoded";
        return true;
    }

    private static bool TryReadEpoch(ref AuthorityPayloadReader reader, out AuthorityEpoch epoch)
    {
        epoch = default;
        if (!reader.TryReadRaw(AuthorityEpoch.SizeBytes, out var bytes) ||
            bytes.Length != AuthorityEpoch.SizeBytes)
        {
            return false;
        }
        epoch = AuthorityEpoch.FromBytes(bytes);
        return epoch.IsValid;
    }
}
