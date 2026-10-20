#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Authority;

#endregion

namespace NebulaeModel.Packets.Authority;

/// <summary>
/// One entity's canonical combat and construct state, as the host publishes it.
/// </summary>
/// <remarks>
/// <para>
/// A building's hitpoints live in the vanilla global skill pool (<c>SkillSystem.combatStats</c>)
/// while its damage record lives in the factory's own construction system
/// (<c>ConstructStat</c>). The two are a pair of facts about one <see cref="ObjectKey"/>, so the
/// replication state ships them together: the client rebuilds the entity → combat/construct
/// references from this record instead of trusting raw host pool indexes, which are meaningless on
/// a different installation.
/// </para>
/// <para>
/// <c>HasCombatStat == false</c> is a statement, not an absence: it means the host says this entity
/// is at full health with no damage record. It is the explicit signal the client's binding uses to
/// clear the entity's local reference and recycle any client-local stat it allocated for the key. The same
/// reading
/// applies to <c>HasConstructStat</c>.
/// </para>
/// <para>
/// The layout is fixed and field-by-field: no reflection over names, no per-record framing beyond
/// the flags byte. The whole struct is 33 bytes, well under
/// <see cref="AuthorityLimits.StateRecordMaxBytes"/>, which is what lets a whole planet's buildings
/// ship as one baseline.
/// </para>
/// </remarks>
internal readonly struct FactoryCombatState
{
    public FactoryCombatState(bool hasCombatStat, int hp, int hpMax, int hpRecover, int hpIncoming,
        bool hasConstructStat, float damageRate, int repairerCount, int repairerModuleId, float repairerValue)
    {
        HasCombatStat = hasCombatStat;
        Hp = hp;
        HpMax = hpMax;
        HpRecover = hpRecover;
        HpIncoming = hpIncoming;
        HasConstructStat = hasConstructStat;
        DamageRate = damageRate;
        RepairerCount = repairerCount;
        RepairerModuleId = repairerModuleId;
        RepairerValue = repairerValue;
    }

    /// <summary>The host says this entity has a live combat stat with these hitpoints.</summary>
    public bool HasCombatStat { get; }

    /// <summary>Absolute hitpoints. Never a delta, never a client-accumulated value.</summary>
    public int Hp { get; }

    public int HpMax { get; }

    /// <summary>Vanilla natural recovery, replicated for display; the host's tick is what applies it.</summary>
    public int HpRecover { get; }

    /// <summary>Incoming (not yet applied) HP change, replicated so the bar can preview honestly.</summary>
    public int HpIncoming { get; }

    /// <summary>The host says this entity has a live construct (damage) record.</summary>
    public bool HasConstructStat { get; }

    public float DamageRate { get; }

    /// <summary>The repairer count published in the host's construct state.</summary>
    public int RepairerCount { get; }

    public int RepairerModuleId { get; }

    public float RepairerValue { get; }
}

/// <summary>
/// Encoding of <see cref="FactoryCombatState"/> into one bounded state blob.
/// </summary>
/// <remarks>
/// The layout is fixed and field-by-field (flag byte, four HP ints, construct block), like the
/// other authority codecs: no reflection over names, no per-record framing. A state blob is 33
/// bytes, well under <see cref="AuthorityLimits.StateRecordMaxBytes"/>.
/// </remarks>
internal static class FactoryCombatStateCodec
{
    private const byte HasCombatFlag = 1;
    private const byte HasConstructFlag = 2;

    internal static bool HasValidLayout(byte[] state) => state != null && state.Length == 33 &&
        (state[0] & ~(HasCombatFlag | HasConstructFlag)) == 0;

    /// <summary>
    /// Encodes the fixed layout, refusing an out-of-range value rather than truncating it.
    /// </summary>
    public static bool TryEncode(in FactoryCombatState state, out byte[] data)
    {
        data = null;
        var flags = (byte)((state.HasCombatStat ? HasCombatFlag : 0) | (state.HasConstructStat ? HasConstructFlag : 0));
        var writer = new AuthorityPayloadWriter();
        writer.WriteByte(flags);
        writer.WriteInt(state.Hp);
        writer.WriteInt(state.HpMax);
        writer.WriteInt(state.HpRecover);
        writer.WriteInt(state.HpIncoming);
        // net472 has no SingleToInt32Bits; GetBytes round-trips the exact bit pattern.
        writer.WriteInt(WireBits.FromSingle(state.DamageRate));
        writer.WriteInt(state.RepairerCount);
        writer.WriteInt(state.RepairerModuleId);
        writer.WriteInt(WireBits.FromSingle(state.RepairerValue));
        data = writer.ToArray();
        return data.Length <= AuthorityLimits.StateRecordMaxBytes;
    }

    /// <summary>
    /// Decodes the fixed layout. Unknown flag bits are refused: a layout the sender and receiver
    /// disagree about must fail loudly, not decode as "no combat stat".
    /// </summary>
    public static bool TryDecode(byte[] source, int offset, int length, out FactoryCombatState state,
        out AuthorityReject reject)
    {
        state = default;
        reject = AuthorityReject.Accepted;
        if (!AuthorityPayloadReader.TryCreate(source, offset, length, out var reader, out reject)) return false;
        if (!reader.TryReadByte(out var flags))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing combat flags");
            return false;
        }
        if ((flags & ~(HasCombatFlag | HasConstructFlag)) != 0)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "flags=" + flags);
            return false;
        }
        if (!reader.TryReadInt(out var hp) || !reader.TryReadInt(out var hpMax) ||
            !reader.TryReadInt(out var hpRecover) || !reader.TryReadInt(out var hpIncoming) ||
            !reader.TryReadInt(out var damageRateBits) || !reader.TryReadInt(out var repairerCount) ||
            !reader.TryReadInt(out var repairerModuleId) || !reader.TryReadInt(out var repairerValueBits))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated combat state");
            return false;
        }
        if (!reader.EndOfPayload)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "trailing bytes");
            return false;
        }
        state = new FactoryCombatState(
            (flags & HasCombatFlag) != 0, hp, hpMax, hpRecover, hpIncoming,
            (flags & HasConstructFlag) != 0,
            WireBits.ToSingle(damageRateBits), repairerCount, repairerModuleId,
            WireBits.ToSingle(repairerValueBits));
        return true;
    }
}
