using System;

namespace NebulaeModel.Authority;

/// <summary>
/// Session identity of one loaded host world, 128 bit.
/// </summary>
/// <remarks>
/// <para>
/// A new epoch is generated every time a world is loaded or the host restarts, so packets from a
/// previous session cannot be applied to the current one even though object slot numbers repeat.
/// This is what makes "the same ID across a reload" two different objects.
/// </para>
/// <para>
/// The value is deliberately not a random-per-packet nonce and not derived from wall clock: it is
/// drawn once per world load and then travels in the welcome message. A zero epoch is invalid, so a
/// default-constructed epoch cannot accidentally authorize anything.
/// </para>
/// </remarks>
internal readonly struct AuthorityEpoch : IEquatable<AuthorityEpoch>, IComparable<AuthorityEpoch>
{
    /// <summary>Fixed wire size of an epoch, in bytes.</summary>
    public const int SizeBytes = 16;

    private readonly ulong high;
    private readonly ulong low;

    public AuthorityEpoch(ulong high, ulong low)
    {
        this.high = high;
        this.low = low;
    }

    public ulong High => high;

    public ulong Low => low;

    /// <summary>False for the default value and for any epoch whose two halves are both zero.</summary>
    public bool IsValid => high != 0 || low != 0;

    /// <summary>
    /// Generates a fresh epoch. Uniqueness only has to hold across the sessions a client can
    /// remember, so a cryptographic random source is enough and is cheaper to reason about than a
    /// counter that a restarted host would have to persist.
    /// </summary>
    public static AuthorityEpoch New()
    {
        var bytes = new byte[16];
        using (var random = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            random.GetBytes(bytes);
        }
        var high = BitConverter.ToUInt64(bytes, 0);
        var low = BitConverter.ToUInt64(bytes, 8);
        // Both halves zero is astronomically unlikely but would produce an invalid epoch, which
        // would make the host reject its own world. Re-roll rather than hand back a broken value.
        if (high == 0 && low == 0) low = 1;
        return new AuthorityEpoch(high, low);
    }

    /// <summary>Rebuilds an epoch from the 16 bytes it was serialized as.</summary>
    public static AuthorityEpoch FromBytes(byte[] bytes, int offset = 0)
    {
        if (bytes == null || offset < 0 || offset + SizeBytes > bytes.Length)
        {
            throw new ArgumentException("AuthorityEpoch needs 16 bytes.", nameof(bytes));
        }
        return new AuthorityEpoch(BitConverter.ToUInt64(bytes, offset),
            BitConverter.ToUInt64(bytes, offset + 8));
    }

    /// <summary>Writes the epoch as the 16 bytes <see cref="FromBytes"/> reads.</summary>
    public void WriteTo(byte[] destination, int offset = 0)
    {
        if (destination == null || offset < 0 || offset + SizeBytes > destination.Length)
        {
            throw new ArgumentException("AuthorityEpoch needs 16 bytes.", nameof(destination));
        }
        Buffer.BlockCopy(BitConverter.GetBytes(high), 0, destination, offset, 8);
        Buffer.BlockCopy(BitConverter.GetBytes(low), 0, destination, offset + 8, 8);
    }

    public byte[] ToBytes()
    {
        var bytes = new byte[SizeBytes];
        WriteTo(bytes);
        return bytes;
    }

    public bool Equals(AuthorityEpoch other) => high == other.high && low == other.low;

    public override bool Equals(object obj) => obj is AuthorityEpoch other && Equals(other);

    public override int GetHashCode() => (high.GetHashCode() * 397) ^ low.GetHashCode();

    /// <summary>
    /// Ordering exists so an epoch can be part of a deterministic sort key; it carries no meaning
    /// about which epoch is newer, because epochs are random rather than monotonic.
    /// </summary>
    public int CompareTo(AuthorityEpoch other)
    {
        var byHigh = high.CompareTo(other.high);
        return byHigh != 0 ? byHigh : low.CompareTo(other.low);
    }

    public static bool operator ==(AuthorityEpoch left, AuthorityEpoch right) => left.Equals(right);

    public static bool operator !=(AuthorityEpoch left, AuthorityEpoch right) => !left.Equals(right);

    public override string ToString() => IsValid ? high.ToString("x16") + low.ToString("x16") : "-";
}

/// <summary>
/// Identity of one client connection to a host.
/// </summary>
/// <remarks>
/// The session <c>PlayerId</c> is reused after a disconnect, so it cannot key a dedup window:
/// a reconnecting player would replay a command whose sequence number the host still remembers.
/// A fresh connection epoch moves the dedup space, and commands carrying an old connection epoch
/// are rejected instead of being re-executed.
/// </remarks>
internal readonly struct ConnectionEpoch : IEquatable<ConnectionEpoch>, IComparable<ConnectionEpoch>
{
    private readonly ulong value;

    public ConnectionEpoch(ulong value)
    {
        this.value = value;
    }

    public ulong Value => value;

    /// <summary>False for the default value, which no real connection ever carries.</summary>
    public bool IsValid => value != 0;

    /// <summary>
    /// Allocates the next connection epoch for a player. The counter is host-side and monotonic per
    /// player, so a reconnecting client can tell that the host considers it a new connection.
    /// </summary>
    public static ConnectionEpoch Next(ulong previous) => new(previous == ulong.MaxValue ? 1 : previous + 1);

    public bool Equals(ConnectionEpoch other) => value == other.value;

    public override bool Equals(object obj) => obj is ConnectionEpoch other && Equals(other);

    public override int GetHashCode() => value.GetHashCode();

    public int CompareTo(ConnectionEpoch other) => value.CompareTo(other.value);

    public static bool operator ==(ConnectionEpoch left, ConnectionEpoch right) => left.Equals(right);

    public static bool operator !=(ConnectionEpoch left, ConnectionEpoch right) => !left.Equals(right);

    public override string ToString() => IsValid ? value.ToString("x16") : "-";
}
