using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Combat;
using NebulaeModel.Packets.Planet;
using NebulaeModel.Packets.Session;

namespace NebulaeModel.Networking;

/// <summary>Counts the same prepared message bytes on both peers, excluding transfer envelopes.</summary>
internal sealed class JoinDataTransfer
{
    public const int ChunkBytes = 64 * 1024;
    public const int MaxMessages = 64;
    private const int MaxMessageBytes = PacketCompression.MaxDecompressedMessageBytes + PacketCompression.HeaderBytes;
    private int[] lengths;
    private byte[] message;
    private int index, offset;
    private long generation, transferId;
    public JoinDataStage Stage { get; private set; }
    public int PlanetId { get; private set; }
    public long ReceivedBytes { get; private set; }
    public long TotalBytes { get; private set; }
    public bool Active => lengths != null;

    // Called by the ordered sender, after each original message has been prepared exactly once.
    public static IEnumerable<byte[]> Encode(long generation, long transferId, JoinDataStage stage,
        int planetId, byte[][] prepared)
    {
        var codec = new NebulaeNetPacketProcessor();
        var begin = new JoinDataTransferBegin
        {
            Generation = generation,
            TransferId = transferId,
            Stage = (byte)stage,
            PlanetId = planetId,
            MessageLengths = prepared.Select(bytes => bytes.Length).ToArray()
        };
        Validate(begin, generation);
        yield return PacketCompression.WrapForSend(codec.Write(begin));
        for (var i = 0; i < prepared.Length; i++)
        {
            var bytes = prepared[i];
            for (var start = 0; start < bytes.Length; start += ChunkBytes)
            {
                var data = new byte[Math.Min(ChunkBytes, bytes.Length - start)];
                Buffer.BlockCopy(bytes, start, data, 0, data.Length);
                yield return PacketCompression.WrapForSend(codec.Write(new JoinDataTransferChunk
                {
                    Generation = generation,
                    TransferId = transferId,
                    MessageIndex = i,
                    Offset = start,
                    Data = data
                }));
            }
            // A transfer retains at most its unsent messages, plus the current chunk.
            prepared[i] = null;
        }
    }

    public void Begin(JoinDataTransferBegin begin, long expectedGeneration)
    {
        Validate(begin, expectedGeneration);
        if (Active || begin.TransferId <= transferId) throw new InvalidDataException("Overlapping or repeated join transfer.");
        generation = begin.Generation;
        transferId = begin.TransferId;
        Stage = (JoinDataStage)begin.Stage;
        PlanetId = begin.PlanetId;
        lengths = (int[])begin.MessageLengths.Clone();
        ReceivedBytes = 0;
        TotalBytes = lengths.Sum(length => (long)length);
        index = offset = 0;
        message = null;
    }

    private static void Validate(JoinDataTransferBegin begin, long expectedGeneration)
    {
        if (expectedGeneration <= 0 || begin.Generation != expectedGeneration || begin.TransferId <= 0 ||
            begin.Stage > (byte)JoinDataStage.Factory ||
            (begin.Stage == (byte)JoinDataStage.World ? begin.PlanetId != 0 : begin.PlanetId <= 0) ||
            begin.MessageLengths == null || begin.MessageLengths.Length == 0 || begin.MessageLengths.Length > MaxMessages ||
            begin.Stage == (byte)JoinDataStage.Factory && begin.MessageLengths.Length != 1 ||
            begin.MessageLengths.Any(length => length <= PacketCompression.HeaderBytes || length > MaxMessageBytes))
            throw new InvalidDataException("Invalid join transfer description.");
    }

    // Only complete messages are returned. The caller dispatches them inline, in their original order.
    public byte[] Receive(JoinDataTransferChunk chunk)
    {
        if (!Active || chunk.Generation != generation || chunk.TransferId != transferId ||
            chunk.MessageIndex != index || chunk.Offset != offset || chunk.Data == null ||
            chunk.Data.Length == 0 || chunk.Data.Length > ChunkBytes || chunk.Data.Length > lengths[index] - offset)
            throw new InvalidDataException("Invalid join transfer chunk.");
        message ??= new byte[lengths[index]];
        Buffer.BlockCopy(chunk.Data, 0, message, offset, chunk.Data.Length);
        offset += chunk.Data.Length;
        ReceivedBytes += chunk.Data.Length;
        if (offset != message.Length) return null;
        var complete = message;
        message = null;
        offset = 0;
        if (++index == lengths.Length) lengths = null;
        return complete;
    }

    public byte[] Decode(byte[] prepared)
    {
        var data = PacketCompression.UnwrapForReceive(prepared);
        if (data.Length < 8) throw new InvalidDataException("Missing join snapshot identity.");
        var id = BitConverter.ToUInt64(data, 0);
        var valid = Stage == JoinDataStage.World
            ? id == WireContract.PacketId(typeof(GlobalGameDataResponse)) || id == WireContract.PacketId(typeof(CombatGenerationPacket))
            : id == WireContract.PacketId(typeof(FactoryData)) && data.Length >= 12 && BitConverter.ToInt32(data, 8) == PlanetId;
        if (!valid) throw new InvalidDataException("Unexpected message in join snapshot transfer.");
        return data;
    }

    public void Clear()
    {
        lengths = null;
        message = null;
        generation = transferId = ReceivedBytes = TotalBytes = 0;
        index = offset = PlanetId = 0;
        Stage = JoinDataStage.World;
    }
}
