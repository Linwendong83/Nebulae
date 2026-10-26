using System;

namespace NebulaeModel.Authority;

/// <summary>
/// Fixed-size header every authority envelope starts with.
/// </summary>
/// <remarks>
/// <para>
/// The header is validated on its own, before any payload buffer is allocated. That ordering is the
/// point of the type: a malformed or oversized message is rejected from 47 bytes of input, so a
/// hostile peer cannot make the receiver allocate a large buffer by declaring one.
/// </para>
/// <para>
/// <see cref="ClaimedPlayerId"/> is an assertion, not identity. The player identity comes from the connection;
/// this field only exists so a forged id is detectable and can be counted rather than
/// silently ignored.
/// </para>
/// </remarks>
internal readonly struct AuthorityEnvelopeHeader
{
    public AuthorityEnvelopeHeader(AuthorityFamily family, AuthorityEpoch epoch,
        ConnectionEpoch connection, long sequence, long hostTick, ushort claimedPlayerId, int payloadLength)
    {
        Family = family;
        Epoch = epoch;
        Connection = connection;
        Sequence = sequence;
        HostTick = hostTick;
        ClaimedPlayerId = claimedPlayerId;
        PayloadLength = payloadLength;
    }

    public AuthorityFamily Family { get; }

    /// <summary>World the message belongs to. Must equal the session's loaded epoch.</summary>
    public AuthorityEpoch Epoch { get; }

    /// <summary>Connection the message belongs to. Zero is legal for host-originated streams.</summary>
    public ConnectionEpoch Connection { get; }

    /// <summary>Per-connection, per-scope stream position. Never a wall clock.</summary>
    public long Sequence { get; }

    /// <summary>Host rule tick the message was produced on. Zero when not applicable.</summary>
    public long HostTick { get; }

    /// <summary>Player id the sender claims. Checked against the connection, never trusted.</summary>
    public ushort ClaimedPlayerId { get; }

    /// <summary>Declared payload size in bytes. Negative is a rejection, not a zero-length payload.</summary>
    public int PayloadLength { get; }

    public override string ToString() =>
        "family=" + Family + "|epoch=" + Epoch + "|conn=" + Connection +
        "|seq=" + Sequence + "|tick=" + HostTick + "|player=" + ClaimedPlayerId + "|len=" + PayloadLength;
}

/// <summary>
/// The single ordered gate every authority message passes before anything else happens.
/// </summary>
/// <remarks>
/// <para>
/// The checks run in a fixed order, cheapest and most fundamental first, and each one has its own
/// reject code so a refusal is diagnosable and countable. Nothing here touches game state, which is
/// what makes the whole gate unit-testable and what keeps the socket thread from needing the world.
/// </para>
/// <para>
/// Validation happens on the header alone. Payload parsing is a separate step that only runs once
/// the header passed, so no payload buffer is ever allocated for a rejected message.
/// </para>
/// </remarks>
internal static class AuthorityEnvelopeGate
{
    /// <summary>
    /// Validates a header against the session context and the direction it arrived from.
    /// </summary>
    /// <param name="context">What the local session believes about itself.</param>
    /// <param name="direction">Which way the message actually traveled.</param>
    /// <param name="header">The parsed fixed header.</param>
    public static AuthorityReject Validate(in AuthoritySessionContext context,
        AuthorityDirection direction, in AuthorityEnvelopeHeader header)
    {
        // World packets require a completed mod version handshake.
        if (!context.IsHandshakeComplete)
        {
            return new AuthorityReject(AuthorityRejectCode.SessionNotReady, "session handshake is not complete");
        }

        // The family must be known, otherwise direction and limits are undefined.
        if (!AuthorityFamilyDirection.TryGetDirection(header.Family, out _))
        {
            return new AuthorityReject(AuthorityRejectCode.UnknownFamily, "family=" + (byte)header.Family);
        }

        // Direction. A command that arrives from the host, or a snapshot pushed at the host, is
        //    refused here rather than being routed and rejected later.
        if (!AuthorityFamilyDirection.IsAllowed(header.Family, direction))
        {
            return new AuthorityReject(AuthorityRejectCode.WrongDirection,
                "family=" + header.Family + " from=" + direction);
        }

        // Epoch. A message from another world load must never touch this one.
        if (!header.Epoch.IsValid || !header.Epoch.Equals(context.Epoch))
        {
            return new AuthorityReject(AuthorityRejectCode.EpochMismatch,
                "packet=" + header.Epoch + " session=" + context.Epoch);
        }

        // Negative length is rejected explicitly instead of being treated as an empty payload,
        //    so a truncation bug cannot masquerade as a valid short message.
        if (header.PayloadLength < 0)
        {
            return new AuthorityReject(AuthorityRejectCode.NegativeLength,
                "len=" + header.PayloadLength + " family=" + header.Family);
        }

        // Size ceilings. Snapshot chunks get their own code because they are the family that
        //    carries bulk data and whose limit the design names separately.
        var max = AuthorityLimits.PayloadMaxBytes(header.Family);
        if (header.PayloadLength > max)
        {
            var code = header.Family == AuthorityFamily.SnapshotChunk
                ? AuthorityRejectCode.FragmentTooLarge
                : AuthorityRejectCode.PayloadTooLarge;
            return new AuthorityReject(code, "len=" + header.PayloadLength + " max=" + max);
        }

        // Identity. The claimed player id is only ever an assertion, so a mismatch is refused
        //    rather than resolved in the packet's favour.
        if (direction == AuthorityDirection.ClientToServer &&
            header.ClaimedPlayerId != context.ConnectedPlayerId)
        {
            return new AuthorityReject(AuthorityRejectCode.ForgedPlayer,
                "claimed=" + header.ClaimedPlayerId + " connection=" + context.ConnectedPlayerId);
        }

        return AuthorityReject.Accepted;
    }

    /// <summary>
    /// Validates the one message that may arrive before the session knows its world epoch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The welcome is the bootstrap: it is what tells a client which world it joined, so requiring
    /// it to name an already-known epoch would make the first message unmatchable. This entry point
    /// checks everything the ordinary gate checks <em>except</em> epoch equality, and additionally
    /// requires that the message really is a welcome traveling server to client and that the session
    /// has no epoch yet.
    /// </para>
    /// <para>
    /// The epoch named is still required to be valid, so a welcome cannot install a zero identity.
    /// After the client adopts it, every later message goes through <see cref="Validate"/> and must
    /// match. That keeps the bootstrap a single, auditable exception instead of a weakened gate.
    /// </para>
    /// </remarks>
    public static AuthorityReject ValidateBootstrapWelcome(in AuthoritySessionContext context,
        AuthorityDirection direction, in AuthorityEnvelopeHeader header)
    {
        if (!context.IsHandshakeComplete)
        {
            return new AuthorityReject(AuthorityRejectCode.SessionNotReady, "session handshake is not complete");
        }
        if (context.Epoch.IsValid)
        {
            // Once a world is known the welcome is an ordinary message; routing it here would let a
            // second host rebind a running client, which is exactly the mixed-room case.
            return new AuthorityReject(AuthorityRejectCode.EpochMismatch, "session already has an epoch");
        }
        if (header.Family != AuthorityFamily.Welcome || direction != AuthorityDirection.ServerToClient)
        {
            return new AuthorityReject(AuthorityRejectCode.WrongDirection,
                "family=" + header.Family + " from=" + direction);
        }
        if (!header.Epoch.IsValid)
        {
            return new AuthorityReject(AuthorityRejectCode.EpochMismatch, "welcome epoch=" + header.Epoch);
        }
        if (header.PayloadLength < 0)
        {
            return new AuthorityReject(AuthorityRejectCode.NegativeLength, "len=" + header.PayloadLength);
        }
        var max = AuthorityLimits.PayloadMaxBytes(header.Family);
        if (header.PayloadLength > max)
        {
            return new AuthorityReject(AuthorityRejectCode.PayloadTooLarge,
                "len=" + header.PayloadLength + " max=" + max);
        }
        return AuthorityReject.Accepted;
    }

    /// <summary>
    /// Validates a decoded object key against the session epoch and its pool's scope rule.
    /// </summary>
    /// <remarks>
    /// Scope validity reuses <see cref="AuthorityScope"/> rather than restating the planet/sector
    /// rule, so the protocol and the shipped generation code cannot drift apart.
    /// </remarks>
    public static AuthorityReject ValidateObjectKey(in AuthoritySessionContext context, in ObjectKey key)
    {
        if (!key.Epoch.IsValid || !key.Epoch.Equals(context.Epoch))
        {
            return new AuthorityReject(AuthorityRejectCode.EpochMismatch, "key=" + key);
        }
        if (!AuthorityScope.IsValidScope(key.Kind, key.Scope))
        {
            return new AuthorityReject(AuthorityRejectCode.IllegalScope,
                "kind=" + key.Kind + " scope=" + key.Scope);
        }
        if (key.NativeId <= 0 || key.Generation <= 0)
        {
            return new AuthorityReject(AuthorityRejectCode.InvalidObjectKey, "key=" + key);
        }
        return AuthorityReject.Accepted;
    }

    /// <summary>Validates a decoded command key against the session epoch and connection.</summary>
    public static AuthorityReject ValidateCommandKey(in AuthoritySessionContext context, in CommandKey key)
    {
        if (!key.Epoch.IsValid || !key.Epoch.Equals(context.Epoch))
        {
            return new AuthorityReject(AuthorityRejectCode.EpochMismatch, "key=" + key);
        }
        if (!key.Connection.IsValid || !key.Connection.Equals(context.Connection))
        {
            return new AuthorityReject(AuthorityRejectCode.InvalidCommandKey,
                "connection=" + key.Connection + " session=" + context.Connection);
        }
        if (key.Sequence <= 0)
        {
            return new AuthorityReject(AuthorityRejectCode.InvalidCommandKey, "sequence=" + key.Sequence);
        }
        return AuthorityReject.Accepted;
    }
}

/// <summary>Wire encoding of <see cref="ObjectKey"/>.</summary>
internal static class ObjectKeyCodec
{
    /// <summary>Writes a key in its fixed order: epoch, kind, scope, native id, generation.</summary>
    public static void WriteTo(byte[] destination, int offset, in ObjectKey key)
    {
        if (destination == null || offset < 0 || offset + AuthorityLimits.ObjectKeyBytes > destination.Length)
        {
            throw new ArgumentException("ObjectKey needs " + AuthorityLimits.ObjectKeyBytes + " bytes.",
                nameof(destination));
        }
        key.Epoch.WriteTo(destination, offset);
        var position = offset + AuthorityEpoch.SizeBytes;
        destination[position] = (byte)key.Kind;
        Buffer.BlockCopy(BitConverter.GetBytes(key.Scope), 0, destination, position + 1, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(key.NativeId), 0, destination, position + 5, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(key.Generation), 0, destination, position + 9, 8);
    }

    /// <summary>
    /// Decodes a key without a session context. The caller owns validating it.
    /// </summary>
    /// <remarks>
    /// Context-free decoding exists for payloads that validate keys against a scope they carry
    /// themselves, like the snapshot codec. It never accepts a malformed key; it just leaves the
    /// session-specific checks (epoch match, scope rule) to the caller.
    /// </remarks>
    public static ObjectKey ReadRaw(byte[] source, int offset)
    {
        if (source == null || offset < 0 || offset + AuthorityLimits.ObjectKeyBytes > source.Length)
        {
            return default;
        }
        var epoch = AuthorityEpoch.FromBytes(source, offset);
        var position = offset + AuthorityEpoch.SizeBytes;
        var kind = (PoolKind)source[position];
        var scope = BitConverter.ToInt32(source, position + 1);
        var nativeId = BitConverter.ToInt32(source, position + 5);
        var generation = BitConverter.ToInt64(source, position + 9);
        return new ObjectKey(epoch, kind, scope, nativeId, generation);
    }
}

/// <summary>
/// A bounded cursor over one already-validated payload buffer.
/// </summary>
/// <remarks>
/// <para>
/// Every read is length-checked against the declared payload size, and every variable-length read
/// takes an explicit maximum. This is the mechanism behind "序列化显式限定数组长度/枚举/字节数":
/// a payload cannot declare a length the envelope did not already bound, so a small message can
/// never make the reader allocate a large array.
/// </para>
/// <para>
/// The type works on a plain byte array and knows nothing about the network stack or Unity, so the
/// whole codec runs in a normal test process.
/// </para>
/// </remarks>
internal struct AuthorityPayloadReader
{
    private readonly byte[] source;
    private readonly int start;
    private readonly int length;
    private int position;

    private AuthorityPayloadReader(byte[] source, int start, int length)
    {
        this.source = source;
        this.start = start;
        this.length = length;
        position = 0;
    }

    /// <summary>Bytes not yet consumed.</summary>
    public int Remaining => length - position;

    /// <summary>True when the whole declared payload was consumed.</summary>
    public bool EndOfPayload => position == length;

    /// <summary>
    /// Opens a reader over the payload region of a buffer.
    /// </summary>
    /// <remarks>
    /// The declared length must fit the buffer; a payload that claims more bytes than were received
    /// is a truncation and is refused here rather than at the first over-read.
    /// </remarks>
    public static bool TryCreate(byte[] source, int offset, int declaredLength, out AuthorityPayloadReader reader,
        out AuthorityReject reject)
    {
        reader = default;
        reject = AuthorityReject.Accepted;
        if (source == null)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "null payload buffer");
            return false;
        }
        if (declaredLength < 0)
        {
            reject = new AuthorityReject(AuthorityRejectCode.NegativeLength, "len=" + declaredLength);
            return false;
        }
        if (offset < 0 || offset + declaredLength > source.Length)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope,
                "payload " + declaredLength + " bytes does not fit buffer " + source.Length);
            return false;
        }
        reader = new AuthorityPayloadReader(source, offset, declaredLength);
        return true;
    }

    private bool Has(int count) => count >= 0 && Remaining >= count;

    public bool TryReadByte(out byte value)
    {
        if (!Has(1))
        {
            value = 0;
            return false;
        }
        value = source[start + position];
        AuthorityStateFields.Read(source, start + position, 1);
        position++;
        return true;
    }

    public bool TryReadUShort(out ushort value)
    {
        if (!Has(2))
        {
            value = 0;
            return false;
        }
        value = BitConverter.ToUInt16(source, start + position);
        AuthorityStateFields.Read(source, start + position, 2);
        position += 2;
        return true;
    }

    public bool TryReadInt(out int value)
    {
        if (!Has(4))
        {
            value = 0;
            return false;
        }
        value = BitConverter.ToInt32(source, start + position);
        AuthorityStateFields.Read(source, start + position, 4);
        position += 4;
        return true;
    }

    public bool TryReadLong(out long value)
    {
        if (!Has(8))
        {
            value = 0;
            return false;
        }
        value = BitConverter.ToInt64(source, start + position);
        AuthorityStateFields.Read(source, start + position, 8);
        position += 8;
        return true;
    }

    /// <summary>
    /// Reads an enum stored as a byte, refusing a value that is not one of the known members.
    /// </summary>
    /// <remarks>
    /// Checking membership is the difference between "unknown family is rejected" and "unknown
    /// family is silently treated as the zero value", which is how a discriminator turns into a
    /// default branch that nobody intended.
    /// </remarks>
    public bool TryReadEnumByte<TEnum>(out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        if (!TryReadByte(out var raw)) return false;
        if (!Enum.IsDefined(typeof(TEnum), raw)) return false;
        value = (TEnum)Enum.ToObject(typeof(TEnum), raw);
        return true;
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> raw bytes with no length prefix, for fixed-size
    /// fields such as an epoch.
    /// </summary>
    public bool TryReadRaw(int count, out byte[] value)
    {
        value = null;
        if (count < 0 || !Has(count)) return false;
        var result = new byte[count];
        Buffer.BlockCopy(source, start + position, result, 0, count);
        AuthorityStateFields.Read(source, start + position, count, 1);
        position += count;
        value = result;
        return true;
    }

    /// <summary>
    /// Reads a length-prefixed byte array, refusing any declared length above <paramref name="maxBytes"/>.
    /// </summary>
    /// <remarks>
    /// A negative declared length is refused, and the array is allocated only after the length is
    /// known to fit both the payload and the caller's ceiling.
    /// </remarks>
    public bool TryReadBytes(int maxBytes, out byte[] value)
    {
        value = null;
        if (!TryReadInt(out var declared)) return false;
        if (declared < 0) return false;
        if (declared > maxBytes) return false;
        if (!Has(declared)) return false;
        var result = new byte[declared];
        Buffer.BlockCopy(source, start + position, result, 0, declared);
        AuthorityStateFields.Read(source, start + position, declared, 1);
        position += declared;
        value = result;
        return true;
    }

    /// <summary>Reads a bounded UTF-8 string. The byte ceiling is explicit, not implied by the payload size.</summary>
    public bool TryReadString(int maxBytes, out string value)
    {
        value = null;
        if (!TryReadUShort(out var declared)) return false;
        if (declared == 0)
        {
            value = string.Empty;
            return true;
        }
        if (declared > maxBytes) return false;
        if (!Has(declared)) return false;
        value = System.Text.Encoding.UTF8.GetString(source, start + position, declared);
        AuthorityStateFields.Read(source, start + position, declared, 1);
        position += declared;
        return true;
    }
}

/// <summary>Bounded writer for an authority payload, mirroring <see cref="AuthorityPayloadReader"/>.</summary>
internal sealed class AuthorityPayloadWriter
{
    private byte[] buffer;
    private int length;

    public AuthorityPayloadWriter(int capacity = 64)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        buffer = new byte[capacity];
    }

    public int Length => length;

    private void Ensure(int additional)
    {
        if (buffer.Length == 0) throw new InvalidOperationException("The completed buffer has been transferred.");
        if (length + additional <= buffer.Length) return;
        var size = buffer.Length;
        while (size < length + additional) size *= 2;
        Array.Resize(ref buffer, size);
    }

    public void WriteByte(byte value)
    {
        Ensure(1);
        buffer[length++] = value;
    }

    public void WriteUShort(ushort value)
    {
        Ensure(2);
        buffer[length++] = (byte)value;
        buffer[length++] = (byte)(value >> 8);
    }

    public void WriteInt(int value)
    {
        WriteUInt(unchecked((uint)value));
    }

    public void WriteUInt(uint value)
    {
        Ensure(4);
        buffer[length++] = (byte)value;
        buffer[length++] = (byte)(value >> 8);
        buffer[length++] = (byte)(value >> 16);
        buffer[length++] = (byte)(value >> 24);
    }

    public void WriteLong(long value)
    {
        Ensure(8);
        var bits = unchecked((ulong)value);
        for (var shift = 0; shift < 64; shift += 8)
            buffer[length++] = (byte)(bits >> shift);
    }

    /// <summary>Writes a length-prefixed byte array after checking the caller's ceiling.</summary>
    public bool TryWriteBytes(byte[] value, int maxBytes)
    {
        if (value == null || value.Length > maxBytes) return false;
        WriteInt(value.Length);
        Ensure(value.Length);
        Buffer.BlockCopy(value, 0, buffer, length, value.Length);
        length += value.Length;
        return true;
    }

    /// <summary>Copies out the written bytes.</summary>
    public byte[] ToArray()
    {
        var result = new byte[length];
        Buffer.BlockCopy(buffer, 0, result, 0, length);
        return result;
    }

    /// <summary>Transfers a completed, exactly sized buffer. No further writes are permitted.</summary>
    public byte[] Finish()
    {
        var result = length == buffer.Length ? buffer : ToArray();
        buffer = Array.Empty<byte>();
        return result;
    }
}
