#region

using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using NebulaeModel.Logger;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Combat;
using NebulaeModel.Packets.Planet;
using NebulaeModel.Packets.Session;
using NebulaeModel.Packets.Routers;

#endregion

namespace NebulaeModel.Networking;

/// <summary>
/// Wire framing for repetitive state batches and large mixed payloads.
/// </summary>
/// <remarks>
/// <para>
/// One binary websocket message carries one frame:
/// <c>[NBLE:4][flag:1][decoded packet length:4][packet data]</c>.
/// Integers are little-endian. Packet data contains one fixed packet identifier and its payload.
/// </para>
/// <para>
/// State batches repeat object keys, scope identifiers and component fields. They use a lower threshold
/// so that ordinary batches and 64 KiB snapshot chunks can shrink too. OrderedSendQueue performs this
/// work off the game thread. Other packets keep the large-message threshold, and a compression attempt
/// that does not save bytes falls back to the original packet.
/// </para>
/// <para>
/// No negotiation packet is needed: the handshake refuses a peer whose mod version differs, so both
/// ends of a live connection always agree on the framing.
/// </para>
/// </remarks>
internal static class PacketCompression
{
    public const uint Signature = 0x454C424E;
    public const int FlagOffset = sizeof(uint);
    public const int LengthOffset = FlagOffset + sizeof(byte);
    public const int HeaderBytes = LengthOffset + sizeof(int);

    private static readonly ulong planetRuntimeId = WireContract.PacketId(typeof(PlanetDataResponse));
    private static readonly ulong globalDataId = WireContract.PacketId(typeof(GlobalGameDataResponse));
    private static readonly ulong joinChunkId = WireContract.PacketId(typeof(JoinDataTransferChunk));
    private static readonly ulong facilityDirectoryId = WireContract.PacketId(typeof(NebulaeModel.Packets.Logistics.ControlPanel.FacilityDirectoryPacket));
    private static readonly ulong snapshotChunkId = WireContract.PacketId(typeof(AuthoritySnapshotChunkPacket));
    private static readonly ulong stateDeltaId = WireContract.PacketId(typeof(StateDeltaPacket));
    private static readonly ulong worldStateId = WireContract.PacketId(typeof(AuthorityWorldStatePacket));
    private static readonly ulong playerCombatStateId = WireContract.PacketId(typeof(AuthorityPlayerCombatStatePacket));
    private static readonly ulong lifecycleId = WireContract.PacketId(typeof(AuthorityLifecyclePacket));
    private static readonly ulong battleVisualId = WireContract.PacketId(typeof(BattleVisualPacket));
    private static readonly ulong playerWeaponId = WireContract.PacketId(typeof(PlayerWeaponPacket));
    private static readonly ulong transportBatchId = WireContract.PacketId(typeof(StarBroadcastPacket));

    private static long latencyBypasses;
    internal static long LatencyBypasses => Interlocked.Read(ref latencyBypasses);

    private static bool ShouldCompress(byte[] packet, bool favorLatency)
    {
        if (packet.Length < StateCompressionThresholdBytes) return false;
        if (IsPrecompressed(packet)) return false;
        if (packet.Length >= CompressionThresholdBytes) return true;
        var id = BitConverter.ToUInt64(packet, 0);
        // These already carry bounded realtime updates (authority states use field deltas).
        // If preparation cannot keep the socket fed, favor timely raw frames over DEFLATE
        // setup on small batches. Snapshot chunks and large transfers retain compression.
        if (favorLatency && (id == worldStateId || id == playerCombatStateId || id == lifecycleId ||
            id == battleVisualId || id == playerWeaponId || id == stateDeltaId))
        { Interlocked.Increment(ref latencyBypasses); return false; }
        return id == transportBatchId || id == stateDeltaId || id == snapshotChunkId || id == worldStateId || id == playerCombatStateId || id == lifecycleId ||
               id == battleVisualId || id == playerWeaponId;
    }

    internal static bool IsPrecompressed(byte[] packet)
    {
        if (packet.Length < 9) return false;
        var reader = new NetDataReader(packet);
        var id = reader.GetULong();
        if (id == planetRuntimeId || id == facilityDirectoryId || id == joinChunkId) return true;
        if (id != globalDataId) return false;
        // Goals and KillStatistics use their own uncompressed encoders. FactoryData is mixed too.
        var type = (GlobalGameDataResponse.EDataType)reader.GetByte();
        return type is GlobalGameDataResponse.EDataType.History or GlobalGameDataResponse.EDataType.GalacticTransport
            or GlobalGameDataResponse.EDataType.SpaceSector or GlobalGameDataResponse.EDataType.MilestoneSystem
            or GlobalGameDataResponse.EDataType.TrashSystem or GlobalGameDataResponse.EDataType.GalacticDigital
            or GlobalGameDataResponse.EDataType.Ready or GlobalGameDataResponse.EDataType.Session or GlobalGameDataResponse.EDataType.PlanetBaseline;
    }
    /// <summary>Frame payload is the packet exactly as serialized.</summary>
    public const byte FlagRaw = 0;

    /// <summary>Frame payload is one raw DEFLATE stream.</summary>
    public const byte FlagDeflate = 1;

    /// <summary>
    /// Default threshold for packets other than the explicitly selected state batches.
    /// </summary>
    public const int CompressionThresholdBytes = 128 * 1024;

    /// <summary>
    /// Minimum serialized size for repetitive batches. Compact 300-500 byte batches still shrink
    /// substantially; smaller events save little in aggregate but pay the same DEFLATE setup cost.
    /// </summary>
    public const int StateCompressionThresholdBytes = 256;

    /// <summary>
    /// Ceiling on one decompressed message, 64 MiB, matching the largest authority payload ceiling.
    /// </summary>
    /// <remarks>
    /// Both the declared packet length and the inflated output must stay within this bound.
    /// </remarks>
    public const int MaxDecompressedMessageBytes = 64 * 1024 * 1024;

    private static long rawMessages;
    private static long rawBytes;
    private static long compressedMessages;
    private static long compressedBytesBefore;
    private static long compressedBytesAfter;
    private static long rejectedMessages;

    /// <summary>Messages that crossed the wire as serialized, frame header excluded.</summary>
    public static long RawMessages => Interlocked.Read(ref rawMessages);

    /// <summary>Serialized bytes inside <see cref="RawMessages"/>.</summary>
    public static long RawBytes => Interlocked.Read(ref rawBytes);

    /// <summary>Messages that crossed the wire deflated.</summary>
    public static long CompressedMessages => Interlocked.Read(ref compressedMessages);

    /// <summary>Serialized bytes inside <see cref="CompressedMessages"/>, before compression.</summary>
    public static long CompressedBytesBefore => Interlocked.Read(ref compressedBytesBefore);

    /// <summary>Wire bytes of <see cref="CompressedMessages"/>, frame header and DEFLATE output included.</summary>
    public static long CompressedBytesAfter => Interlocked.Read(ref compressedBytesAfter);

    /// <summary>Messages refused on arrival: bad flag, failed inflate, or past the size ceiling.</summary>
    public static long RejectedMessages => Interlocked.Read(ref rejectedMessages);

    /// <summary>True once any message has crossed either counter, so a report can stay silent otherwise.</summary>
    public static bool HasTraffic => RawMessages + CompressedMessages > 0;

    /// <summary>
    /// Frames one serialized packet for the socket.
    /// </summary>
    /// <remarks>
    /// A failure to compress must not cost the packet, so the serialized bytes are sent as they are;
    /// the same applies when DEFLATE grows the message instead of shrinking it.
    /// </remarks>
    public static byte[] WrapForSend(byte[] packet, bool favorLatency = false)
    {
        if (packet == null) throw new ArgumentNullException(nameof(packet));
        if (packet.Length < sizeof(ulong) || packet.Length > MaxDecompressedMessageBytes)
            throw new InvalidDataException("Invalid packet length.");

        if (!ShouldCompress(packet, favorLatency))
        {
            CountRaw(packet.Length);
            return Frame(packet, FlagRaw, packet.Length);
        }

        byte[] deflated;
        try
        {
            using var input = new MemoryStream(packet, writable: false);
            using var output = new MemoryStream();
            using (var deflater = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                input.CopyTo(deflater);
            }
            deflated = output.ToArray();
        }
        catch (Exception exception)
        {
            Log.Warn($"Packet compression failed, sending {packet.Length} bytes as they are: {exception.Message}");
            CountRaw(packet.Length);
            return Frame(packet, FlagRaw, packet.Length);
        }

        if (deflated.Length >= packet.Length)
        {
            CountRaw(packet.Length);
            return Frame(packet, FlagRaw, packet.Length);
        }

        Interlocked.Increment(ref compressedMessages);
        Interlocked.Add(ref compressedBytesBefore, packet.Length);
        Interlocked.Add(ref compressedBytesAfter, deflated.Length + HeaderBytes);
        return Frame(deflated, FlagDeflate, packet.Length);
    }

    /// <summary>
    /// Validates one frame and returns its complete decoded packet.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The signature, flag, declared length or payload is invalid.
    /// </exception>
    public static byte[] UnwrapForReceive(byte[] message)
    {
        try
        {
            if (message == null || message.Length <= HeaderBytes || BitConverter.ToUInt32(message, 0) != Signature)
                throw new InvalidDataException("Invalid packet frame header.");
            var length = BitConverter.ToInt32(message, LengthOffset);
            if (length < sizeof(ulong) || length > MaxDecompressedMessageBytes)
                throw new InvalidDataException("Invalid decoded packet length.");
            switch (message[FlagOffset])
            {
                case FlagRaw:
                    if (message.Length - HeaderBytes != length)
                        throw new InvalidDataException("Raw packet length does not match its frame header.");
                    var raw = new byte[length];
                    Array.Copy(message, HeaderBytes, raw, 0, raw.Length);
                    return raw;
                case FlagDeflate:
                    return Inflate(message, length);
                default:
                    throw new InvalidDataException($"Unknown packet compression flag {message[FlagOffset]}.");
            }
        }
        catch (InvalidDataException)
        {
            Interlocked.Increment(ref rejectedMessages);
            throw;
        }
    }

    /// <summary>One line of process-lifetime totals, for the perf report.</summary>
    /// <remarks>
    /// Process-lifetime rather than per-session on purpose: the counters sit on the transport, where
    /// host and client traffic of a self-hosted session share one process. They answer "how much did
    /// compression save", which is what tuning the threshold needs, not a per-role session metric.
    /// </remarks>
    public static string Describe()
    {
        var before = CompressedBytesBefore;
        var after = CompressedBytesAfter;
        var saved = before > after ? 100.0 * (before - after) / before : 0.0;
        return "wire compression rawMessages=" + RawMessages + " rawBytes=" + RawBytes +
               " compressedMessages=" + CompressedMessages + " rawBytesCompressed=" + before +
               " wireBytesCompressed=" + after +
               " savedPercent=" + saved.ToString("F1", CultureInfo.InvariantCulture) +
               " rejected=" + RejectedMessages + " " + StateDeltaMetrics.Describe();
    }

    private static void CountRaw(int bytes)
    {
        Interlocked.Increment(ref rawMessages);
        Interlocked.Add(ref rawBytes, bytes);
    }

    private static byte[] Frame(byte[] payload, byte flag, int packetLength)
    {
        var framed = new byte[payload.Length + HeaderBytes];
        BitConverter.GetBytes(Signature).CopyTo(framed, 0);
        framed[FlagOffset] = flag;
        BitConverter.GetBytes(packetLength).CopyTo(framed, LengthOffset);
        Array.Copy(payload, 0, framed, HeaderBytes, payload.Length);
        return framed;
    }

    private static byte[] Inflate(byte[] message, int packetLength)
    {
        using var input = new MemoryStream(message, HeaderBytes, message.Length - HeaderBytes, writable: false);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        long total = 0;
        int read;
        while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > packetLength)
                throw new InvalidDataException("Decoded packet exceeds its declared length.");
            output.Write(buffer, 0, read);
        }
        if (total != packetLength)
            throw new InvalidDataException("Decoded packet length does not match its frame header.");
        return output.ToArray();
    }
}
