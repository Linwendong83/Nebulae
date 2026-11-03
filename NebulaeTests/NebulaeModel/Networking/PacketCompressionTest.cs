using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Combat;
using NebulaeModel.Packets.Planet;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel.Networking;

/// <summary>
/// Frames contain one complete packet, raw or compressed, with a mandatory signature and length.
/// </summary>
[TestClass]
[DoNotParallelize]
internal class PacketCompressionTest
{
    [TestMethod]
    public void SmallMessagesUseRawFrames()
    {
        var packet = Pattern(1024);

        var framed = PacketCompression.WrapForSend(packet);

        TestAssert.AreEqual(PacketCompression.FlagRaw, framed[PacketCompression.FlagOffset]);
        TestAssert.HasCount(packet.Length + PacketCompression.HeaderBytes, framed);
        CollectionAssert.AreEqual(packet, PacketCompression.UnwrapForReceive(framed));
    }

    [TestMethod]
    public void LargeCompressibleMessagesAreDeflatedAndRoundTrip()
    {
        var packet = Pattern(PacketCompression.CompressionThresholdBytes * 2);

        var framed = PacketCompression.WrapForSend(packet);

        TestAssert.AreEqual(PacketCompression.FlagDeflate, framed[PacketCompression.FlagOffset]);
        TestAssert.IsLessThan(packet.Length, framed.Length, "A compressible message must shrink on the wire.");
        CollectionAssert.AreEqual(packet, PacketCompression.UnwrapForReceive(framed));
    }

    [TestMethod]
    public void TheThresholdIsExclusiveAtItsBoundary()
    {
        var justBelow = PacketCompression.WrapForSend(Pattern(PacketCompression.CompressionThresholdBytes - 1));
        var atThreshold = PacketCompression.WrapForSend(Pattern(PacketCompression.CompressionThresholdBytes));

        TestAssert.AreEqual(PacketCompression.FlagRaw, justBelow[PacketCompression.FlagOffset]);
        TestAssert.AreEqual(PacketCompression.FlagDeflate, atThreshold[PacketCompression.FlagOffset]);
    }

    [TestMethod]
    public void LargeIncompressibleMessagesAreNotGrownByCompression()
    {
        var packet = RandomBytes(PacketCompression.CompressionThresholdBytes * 2);

        var framed = PacketCompression.WrapForSend(packet);

        TestAssert.AreEqual(PacketCompression.FlagRaw, framed[PacketCompression.FlagOffset],
            "DEFLATE cannot shrink random bytes, so they must not be grown by it.");
        CollectionAssert.AreEqual(packet, PacketCompression.UnwrapForReceive(framed));
    }

    [TestMethod]
    public void AMessageTheFramingCannotReadIsRefused()
    {
        TestAssert.Throws<InvalidDataException>(() => PacketCompression.UnwrapForReceive(Array.Empty<byte>()));
        TestAssert.Throws<InvalidDataException>(() => PacketCompression.UnwrapForReceive(Frame(7, 8, new byte[8])));
        TestAssert.Throws<InvalidDataException>(() =>
            PacketCompression.UnwrapForReceive(Frame(PacketCompression.FlagDeflate, 8, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })));
    }

    [TestMethod]
    public void AMessageThatExpandsPastTheCeilingIsRefused()
    {
        foreach (var length in new[] { -1, 0, 7, PacketCompression.MaxDecompressedMessageBytes + 1 })
            TestAssert.Throws<InvalidDataException>(() => PacketCompression.UnwrapForReceive(
                Frame(PacketCompression.FlagDeflate, length, new byte[] { 3, 0 })));
        TestAssert.Throws<InvalidDataException>(() => PacketCompression.WrapForSend(
            new byte[PacketCompression.MaxDecompressedMessageBytes + 1]));
    }

    [TestMethod]
    public void FrameSignatureAndEveryHeaderByteAreRequired()
    {
        var packet = Pattern(64);
        var framed = PacketCompression.WrapForSend(packet);
        CollectionAssert.AreEqual(new byte[] { (byte)'N', (byte)'B', (byte)'L', (byte)'E' }, framed.Take(4).ToArray());
        TestAssert.AreEqual(packet.Length, BitConverter.ToInt32(framed, PacketCompression.LengthOffset));
        for (var length = 0; length <= PacketCompression.HeaderBytes; length++)
            TestAssert.Throws<InvalidDataException>(() => PacketCompression.UnwrapForReceive(framed.Take(length).ToArray()));
        for (var index = 0; index < PacketCompression.FlagOffset; index++)
        {
            var invalid = framed.ToArray();
            invalid[index] ^= 0x80;
            TestAssert.Throws<InvalidDataException>(() => PacketCompression.UnwrapForReceive(invalid));
        }
        TestAssert.Throws<InvalidDataException>(() => PacketCompression.UnwrapForReceive(packet));
    }

    [TestMethod]
    public void BothPayloadEncodingsMustMatchTheDeclaredLength()
    {
        foreach (var size in new[] { 64, PacketCompression.CompressionThresholdBytes * 2 })
        {
            var framed = PacketCompression.WrapForSend(Pattern(size));
            foreach (var declared in new[] { size - 1, size + 1 })
            {
                var invalid = framed.ToArray();
                BitConverter.GetBytes(declared).CopyTo(invalid, PacketCompression.LengthOffset);
                TestAssert.Throws<InvalidDataException>(() => PacketCompression.UnwrapForReceive(invalid));
            }
            TestAssert.Throws<InvalidDataException>(() => PacketCompression.UnwrapForReceive(
                framed.Take(framed.Length - 8).ToArray()));
        }
        var raw = PacketCompression.WrapForSend(Pattern(64));
        TestAssert.Throws<InvalidDataException>(() => PacketCompression.UnwrapForReceive(raw.Concat(new byte[] { 0 }).ToArray()));
    }

    [TestMethod]
    public void FramePayloadContainsOneSerializedPacket()
    {
        var processor = new NebulaeNetPacketProcessor();
        CompressionProbePacket? received = null;
        processor.SubscribeReusable<CompressionProbePacket>(p => received = p);
        var payload = Pattern(PacketCompression.CompressionThresholdBytes * 2);
        var serialized = processor.Write(new CompressionProbePacket { Bytes = payload });

        var framed = PacketCompression.WrapForSend(serialized);
        var unwrapped = PacketCompression.UnwrapForReceive(framed);

        CollectionAssert.AreEqual(serialized, unwrapped);
        processor.ReadPacket(new NetDataReader(unwrapped));
        TestAssert.IsNotNull(received);
        CollectionAssert.AreEqual(payload, received.Bytes);
    }

    [TestMethod]
    public void StateAndCombatBatchesUseTheLowerThresholdAndRoundTrip()
    {
        var processor = new NebulaeNetPacketProcessor();
        var payload = Pattern(4096);
        var packets = new[]
        {
            processor.Write(new AuthoritySnapshotChunkPacket { Data = payload }),
            processor.Write(new AuthorityWorldStatePacket { Data = payload }),
            processor.Write(new AuthorityPlayerCombatStatePacket { CombatData = payload }),
            processor.Write(new AuthorityLifecyclePacket { Data = payload }),
            processor.Write(new BattleVisualPacket { Data = payload }),
            processor.Write(new PlayerWeaponPacket { Data = payload })
        };
        foreach (var packet in packets)
        {
            TestAssert.IsLessThan(PacketCompression.CompressionThresholdBytes, packet.Length);
            var framed = PacketCompression.WrapForSend(packet);
            TestAssert.AreEqual(PacketCompression.FlagDeflate, framed[PacketCompression.FlagOffset]);
            CollectionAssert.AreEqual(packet, PacketCompression.UnwrapForReceive(framed));
        }
    }

    [TestMethod]
    public void StateThresholdPreservesSmallPacketsAndRejectsCompressionThatGrowsThem()
    {
        foreach (var size in new[] { PacketCompression.StateCompressionThresholdBytes - 1,
                     PacketCompression.StateCompressionThresholdBytes })
        {
            // Eligibility and compressibility are independent; use a repetitive body for the boundary.
            var packet = new byte[size];
            BitConverter.GetBytes(WireContract.PacketId(typeof(AuthorityWorldStatePacket))).CopyTo(packet, 0);
            var framed = PacketCompression.WrapForSend(packet);
            TestAssert.AreEqual(size < PacketCompression.StateCompressionThresholdBytes
                ? PacketCompression.FlagRaw : PacketCompression.FlagDeflate, framed[PacketCompression.FlagOffset]);
            CollectionAssert.AreEqual(packet, PacketCompression.UnwrapForReceive(framed));
        }
        foreach (var size in new[] { PacketCompression.StateCompressionThresholdBytes,
                     PacketCompression.StateCompressionThresholdBytes * 2, 16 * 1024 })
        {
            var random = RandomBytes(size);
            BitConverter.GetBytes(WireContract.PacketId(typeof(AuthorityWorldStatePacket))).CopyTo(random, 0);
            var unchanged = PacketCompression.WrapForSend(random);
            TestAssert.AreEqual(PacketCompression.FlagRaw, unchanged[PacketCompression.FlagOffset]);
            CollectionAssert.AreEqual(random, PacketCompression.UnwrapForReceive(unchanged));
        }
    }

    [TestMethod]
    public void CompactStateBatchBelow512BytesStillCompresses()
    {
        var epoch = new AuthorityEpoch(1, 2);
        var records = Enumerable.Range(1, 4).Select(id => new AuthorityWorldStateRecord(
            ObjectKey.Create(epoch, PoolKind.GroundEnemy, 104, id, 1), id, new byte[33])).ToArray();
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryEncode(records, out var data));
        var processor = new NebulaeNetPacketProcessor();
        var packet = processor.Write(new AuthorityWorldStatePacket
        {
            EpochHigh = epoch.High,
            EpochLow = epoch.Low,
            ScopeKind = (byte)PoolKind.GroundEnemy,
            Scope = 104,
            RecordCount = records.Length,
            Data = data
        });
        TestAssert.IsTrue(packet.Length >= 256 && packet.Length < 512,
            "Compact state batches in this size range must not lose compression eligibility.");

        var framed = PacketCompression.WrapForSend(packet);

        TestAssert.AreEqual(PacketCompression.FlagDeflate, framed[PacketCompression.FlagOffset]);
        TestAssert.IsLessThan(packet.Length, framed.Length);
        CollectionAssert.AreEqual(packet, PacketCompression.UnwrapForReceive(framed));
    }

    [TestMethod]
    public void MediumUnknownAndPrecompressedPacketsUseRawFrames()
    {
        var processor = new NebulaeNetPacketProcessor();
        foreach (var packet in new[]
                 {
                     processor.Write(new CompressionProbePacket { Bytes = Pattern(8192) }),
                     processor.Write(new PlanetDataResponse(101, Pattern(8192)))
                 })
        {
            var framed = PacketCompression.WrapForSend(packet);
            TestAssert.AreEqual(PacketCompression.FlagRaw, framed[PacketCompression.FlagOffset]);
            CollectionAssert.AreEqual(packet, PacketCompression.UnwrapForReceive(framed));
        }
    }

    [TestMethod]
    public void FullSnapshotChunkIsCompressedAndStillDeserializes()
    {
        var processor = new NebulaeNetPacketProcessor();
        AuthoritySnapshotChunkPacket? received = null;
        processor.SubscribeReusable<AuthoritySnapshotChunkPacket>(p => received = p);
        var data = Pattern(AuthorityLimits.ChunkMaxBytes);
        var packet = processor.Write(new AuthoritySnapshotChunkPacket
        {
            ScopeKind = (byte)PoolKind.GroundEnemy,
            Scope = 101,
            BaselineId = 7,
            SubscriptionEpoch = 3,
            ChunkIndex = 1,
            Sequence = 4,
            Data = data
        });
        var framed = PacketCompression.WrapForSend(packet);
        TestAssert.AreEqual(PacketCompression.FlagDeflate, framed[PacketCompression.FlagOffset]);
        processor.ReadPacket(new NetDataReader(PacketCompression.UnwrapForReceive(framed)));
        TestAssert.IsNotNull(received);
        TestAssert.AreEqual(7L, received.BaselineId);
        TestAssert.AreEqual(3L, received.SubscriptionEpoch);
        TestAssert.AreEqual(1, received.ChunkIndex);
        CollectionAssert.AreEqual(data, received.Data);
    }

    [TestMethod]
    public void PlayerCombatCompressionPreservesSettlementAndResourceLedgers()
    {
        var processor = new NebulaeNetPacketProcessor();
        AuthorityPlayerCombatStatePacket? received = null;
        processor.SubscribeReusable<AuthorityPlayerCombatStatePacket>(p => received = p);
        var sent = new AuthorityPlayerCombatStatePacket
        {
            PlayerId = 2,
            ActorConnection = ulong.MaxValue,
            CombatRevision = long.MaxValue,
            PersonalRevision = 17,
            LastCommandSequence = 99,
            CoreEnergyDebitTotal = 123456789.125,
            IsAlive = false,
            DeathCount = 3,
            InvincibleTicks = 180,
            HpRecoverCD = 60,
            ShieldRecoverCD = 120,
            DebitItems = [1001, 1601],
            DebitTotals = [4, 7],
            SettledAttacks = [98, 99],
            CombatData = Pattern(10800)
        };
        var bytes = processor.Write(sent);
        var framed = PacketCompression.WrapForSend(bytes);
        TestAssert.AreEqual(PacketCompression.FlagDeflate, framed[PacketCompression.FlagOffset]);
        var restored = PacketCompression.UnwrapForReceive(framed);
        CollectionAssert.AreEqual(bytes, restored);
        processor.ReadPacket(new NetDataReader(restored));
        TestAssert.IsNotNull(received);
        TestAssert.AreEqual(sent.ActorConnection, received.ActorConnection);
        TestAssert.AreEqual(sent.CombatRevision, received.CombatRevision);
        TestAssert.AreEqual(sent.PersonalRevision, received.PersonalRevision);
        TestAssert.AreEqual(sent.CoreEnergyDebitTotal, received.CoreEnergyDebitTotal);
        TestAssert.AreEqual(sent.IsAlive, received.IsAlive);
        CollectionAssert.AreEqual(sent.DebitItems, received.DebitItems);
        CollectionAssert.AreEqual(sent.DebitTotals, received.DebitTotals);
        CollectionAssert.AreEqual(sent.SettledAttacks, received.SettledAttacks);
        CollectionAssert.AreEqual(sent.CombatData, received.CombatData);
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private static byte[] Frame(byte flag, int length, byte[] payload)
    {
        var frame = new byte[PacketCompression.HeaderBytes + payload.Length];
        BitConverter.GetBytes(PacketCompression.Signature).CopyTo(frame, 0);
        frame[PacketCompression.FlagOffset] = flag;
        BitConverter.GetBytes(length).CopyTo(frame, PacketCompression.LengthOffset);
        payload.CopyTo(frame, PacketCompression.HeaderBytes);
        return frame;
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(1016).NextBytes(bytes);
        return bytes;
    }

    [global::NebulaeModel.Networking.PacketId(0xff0000aaUL)]
    internal class CompressionProbePacket
    {
        [global::NebulaeModel.Networking.WireOrder(0)]
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
    }
}
