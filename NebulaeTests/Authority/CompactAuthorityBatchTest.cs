using NebulaeModel.Authority;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class CompactAuthorityBatchTest
{
    private static readonly AuthorityEpoch Epoch = new(ulong.MaxValue, 0x0123456789ABCDEF);
    private static readonly ScopeKey Scope = new(PoolKind.GroundEnemy, 101);
    private static readonly AuthoritySessionContext Context = new(true, Epoch, new ConnectionEpoch(1), 1, true);
    private static ObjectKey Key(int id) => ObjectKey.Create(Epoch, Scope.Kind, Scope.Scope, id, long.MaxValue);

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(17)]
    [DataRow(4096)]
    public void CompactBatchesPreserveFullIdentityRevisionAndOpaqueBytes(int count)
    {
        var random = new Random(1016);
        var records = Enumerable.Range(1, count).Select(id =>
        {
            var bytes = new byte[id % 33]; random.NextBytes(bytes);
            return new AuthorityWorldStateRecord(Key(id), long.MaxValue - id, bytes);
        }).ToArray();
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryEncode(records, out var data));
        TestAssert.HasCount(4 + (count == 0 ? 0 : 21) + records.Sum(r => 25 + r.State.Length), data);
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryDecode(data, 0, data.Length, count, Context, out var decoded, out _));
        for (var i = 0; i < count; i++)
        {
            TestAssert.AreEqual(records[i].Key, decoded[i].Key);
            TestAssert.AreEqual(records[i].Revision, decoded[i].Revision);
            CollectionAssert.AreEqual(records[i].State, decoded[i].State);
        }
        var lifecycle = records.Select((r, i) => new AuthorityLifecycleRecord(
            (AuthorityLifecycleOp)(i % 3 + 1), r.Key, r.Revision)).ToArray();
        TestAssert.IsTrue(AuthorityLifecycleCodec.TryEncode(lifecycle, out var events));
        TestAssert.HasCount(4 + (count == 0 ? 0 : 21) + 21 * count, events);
        TestAssert.IsTrue(AuthorityLifecycleCodec.TryDecode(events, 0, events.Length, count, Context, out var changes, out _));
        for (var i = 0; i < count; i++)
        {
            TestAssert.AreEqual(lifecycle[i].Key, changes[i].Key);
            TestAssert.AreEqual(lifecycle[i].Revision, changes[i].Revision);
            TestAssert.AreEqual(lifecycle[i].Op, changes[i].Op);
        }
    }

    [TestMethod]
    public void BatchesCannotSilentlyRewriteMixedEpochsPoolsOrScopes()
    {
        var others = new[]
        {
            ObjectKey.Create(new AuthorityEpoch(1, 2), Scope.Kind, Scope.Scope, 2, 1),
            ObjectKey.Create(Epoch, PoolKind.Entity, Scope.Scope, 2, 1),
            ObjectKey.Create(Epoch, Scope.Kind, 102, 2, 1)
        };
        foreach (var other in others)
        {
            TestAssert.IsFalse(AuthorityWorldStateCodec.TryEncode(
                [new(Key(1), 1, [1]), new(other, 2, [2])], out _));
            TestAssert.IsFalse(AuthorityLifecycleCodec.TryEncode(
                [new(AuthorityLifecycleOp.Spawn, Key(1), 1), new(AuthorityLifecycleOp.Spawn, other, 2)], out _));
            TestAssert.IsFalse(AuthoritySnapshotCodec.TryEncode(new FrozenScopeSnapshot(Scope, 1, 1, 0, 0,
                [new(Key(1), 1, [1]), new(other, 2, [2])]), out _));
        }
    }

    [TestMethod]
    public void EveryTruncationIsRefused()
    {
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryEncode([new(Key(1), 1, [1, 2, 3])], out var state));
        TestAssert.IsTrue(AuthorityLifecycleCodec.TryEncode([new(AuthorityLifecycleOp.Death, Key(1), 2)], out var events));
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(new FrozenScopeSnapshot(Scope, 1, 1, 0, 0,
            [new(Key(1), 1, [1, 2, 3])]), out var image));
        for (var size = 0; size < state.Length; size++)
            TestAssert.IsFalse(AuthorityWorldStateCodec.TryDecode(state, 0, size, 1, Context, out _, out _));
        for (var size = 0; size < events.Length; size++)
            TestAssert.IsFalse(AuthorityLifecycleCodec.TryDecode(events, 0, size, 1, Context, out _, out _));
        for (var size = 0; size < image.Length; size++)
            TestAssert.IsFalse(AuthoritySnapshotCodec.TryDecode(image.Take(size).ToArray(), Epoch, Scope, out _, out _));

    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(513)]
    [DataRow(int.MaxValue)]
    public void StateLengthsCannotEscapeTheirRecord(int length)
    {
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryEncode([new(Key(1), 1, [1])], out var data));
        BitConverter.GetBytes(length).CopyTo(data, 46); // shared header + native id + generation + revision + encoding
        TestAssert.IsFalse(AuthorityWorldStateCodec.TryDecode(data, 0, data.Length, 1, Context, out var records, out _));
        TestAssert.IsNull(records);
    }

    [TestMethod]
    public void TamperedIdentityAndInflatedCountAreRefusedBeforeApplication()
    {
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryEncode([new(Key(1), 1, [1])], out var data));
        var foreign = (byte[])data.Clone(); foreign[4] ^= 1;
        TestAssert.IsFalse(AuthorityWorldStateCodec.TryDecode(foreign, 0, foreign.Length, 1, Context, out _, out var reject));
        TestAssert.AreEqual(AuthorityRejectCode.EpochMismatch, reject.Code);
        foreach (var offset in new[] { 25, 29, 37 }) // id, generation, revision
        {
            var invalid = (byte[])data.Clone();
            Array.Clear(invalid, offset, offset == 25 ? 4 : 8);
            TestAssert.IsFalse(AuthorityWorldStateCodec.TryDecode(invalid, 0, invalid.Length, 1, Context, out _, out _));
        }
        BitConverter.GetBytes(4096).CopyTo(data, 0);
        TestAssert.IsFalse(AuthorityWorldStateCodec.TryDecode(data, 0, data.Length, 4096, Context, out _, out _));
    }

    [TestMethod]
    public void SnapshotNullAndEmptyStatesStayDistinctAndOtherNegativeLengthsFail()
    {
        var snapshot = new FrozenScopeSnapshot(Scope, 1, 1, 0, 0,
            [new(Key(1), 1, null), new(Key(2), 2, Array.Empty<byte>())]);
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(snapshot, out var image));
        TestAssert.HasCount(36 + 21 + 2 * 24, image);
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryDecode(image, Epoch, Scope, out var decoded, out _));
        TestAssert.IsNull(decoded.Members[0].State);
        TestAssert.IsNotNull(decoded.Members[1].State);
        TestAssert.IsEmpty(decoded.Members[1].State);
        BitConverter.GetBytes(-2).CopyTo(image, 77);
        TestAssert.IsFalse(AuthoritySnapshotCodec.TryDecode(image, Epoch, Scope, out _, out _));
    }

    [TestMethod]
    public void CompactedBatchBelowOneKiBRemainsCompressed()
    {
        var records = Enumerable.Range(1, 12).Select(id => new AuthorityWorldStateRecord(Key(id), 1, new byte[33])).ToArray();
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryEncode(records, out var data));
        var processor = new NebulaeNetPacketProcessor();
        var bytes = processor.Write(new AuthorityWorldStatePacket { Data = data, RecordCount = records.Length });
        TestAssert.IsLessThan(1024, bytes.Length);
        var framed = PacketCompression.WrapForSend(bytes);
        TestAssert.AreEqual(PacketCompression.FlagDeflate, framed[PacketCompression.FlagOffset]);
        CollectionAssert.AreEqual(bytes, PacketCompression.UnwrapForReceive(framed));
    }
}
