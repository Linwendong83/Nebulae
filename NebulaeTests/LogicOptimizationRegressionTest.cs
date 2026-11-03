using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Routers;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class LogicOptimizationRegressionTest
{
    [TestMethod]
    public void SparseFieldsOnBothSidesOfADenseFieldKeepTheirOwnIndexes()
    {
        var fields = new[] { new WireField(0, 128, 1), new WireField(128, 256, 1), new WireField(384, 512, 1) };
        var before = new byte[896]; var after = (byte[])before.Clone(); var random = new Random(31);
        for (var round = 0; round < 30; round++)
        {
            after[round]++; after[500 + round]++;
            var dense = new byte[256]; random.NextBytes(dense); Buffer.BlockCopy(dense, 0, after, 128, dense.Length);
            TestAssert.IsTrue(FieldDeltaCodec.TryEncode(before, fields, after, fields, out var patch));
            CollectionAssert.AreEqual(after, FieldDeltaCodec.Apply(before, fields, patch));
        }
    }

    [TestMethod]
    public void DeltaBudgetIncludesTheExactPatchSizeAndNeverMutatesInputs()
    {
        var before = new byte[512]; var after = (byte[])before.Clone(); after[400] = 17;
        var fields = new[] { new WireField(0, 512, 1) };
        TestAssert.IsTrue(FieldDeltaCodec.TryEncode(before, fields, after, fields, out var reference));
        TestAssert.IsFalse(FieldDeltaCodec.TryEncode(before, fields, after, fields, reference.Length - 1, out var refused));
        TestAssert.IsNull(refused);
        TestAssert.IsTrue(FieldDeltaCodec.TryEncode(before, fields, after, fields, reference.Length, out var exact));
        CollectionAssert.AreEqual(reference, exact);
        CollectionAssert.AreEqual(after, FieldDeltaCodec.Apply(before, fields, exact));
        TestAssert.IsTrue(FieldDeltaCodec.TryEncode(before, fields.ToList(), after, fields.ToList(), out var fromList));
        CollectionAssert.AreEqual(reference, fromList);
        CollectionAssert.AreEqual(after, FieldDeltaCodec.Apply(before, fields.ToList(), fromList));
        TestAssert.IsTrue(before.All(value => value == 0));
        new Random(7).NextBytes(after);
        TestAssert.IsFalse(FieldDeltaCodec.TryEncode(before, fields, after, fields, 511, out refused));
        TestAssert.IsNull(refused);
    }

    [TestMethod]
    public void VariableFieldsAndSparseArraysRoundTripWithChangingWidths()
    {
        var random = new Random(77);
        for (var round = 0; round < 200; round++)
        {
            var length = round % 4 == 0 ? round : 256;
            var before = new byte[260]; var after = new byte[4 + length];
            random.NextBytes(before); Buffer.BlockCopy(before, 0, after, 0, Math.Min(before.Length, after.Length));
            if (after.Length > 4) after[after.Length - 1] ^= 17;
            var oldFields = new[] { new WireField(0, 4), new WireField(4, 256, 1) };
            var newFields = new[] { new WireField(0, 4), new WireField(4, length, 1) };
            TestAssert.IsTrue(FieldDeltaCodec.TryEncode(before, oldFields, after, newFields, out var patch));
            CollectionAssert.AreEqual(after, FieldDeltaCodec.Apply(before, oldFields, patch));
            TestAssert.Throws<InvalidDataException>(() => FieldDeltaCodec.Apply(before, oldFields, patch.Take(patch.Length - 1).ToArray()));
        }
    }

    [TestMethod]
    public void StringWriterKeepsUtf8TruncationAndResetSemantics()
    {
        var writer = new NetDataWriter();
        writer.Put(17); writer.Put((string?)null); writer.Put(""); writer.Put("中文🙂", 2);
        var reader = new NetDataReader(writer.CopyData());
        TestAssert.AreEqual(17, reader.GetInt()); TestAssert.AreEqual("", reader.GetString());
        TestAssert.AreEqual("", reader.GetString()); TestAssert.AreEqual("中文", reader.GetString());
        writer.Reset(); writer.Put("世界🙂");
        TestAssert.AreEqual("世界🙂", new NetDataReader(writer.CopyData()).GetString());
        TestAssert.Throws<ArgumentException>(() => writer.Put("\ud800"));
    }

    [TestMethod]
    public void UnchangedRoutingReturnsOriginalBytesAndPreservesEmptyChildren()
    {
        var writer = new NebulaeNetPacketProcessor(); PacketUtils.RegisterNestedTypes(writer);
        writer.SubscribeReusable<PlanetBroadcastPacket>(_ => { });
        writer.SubscribeReusable<global::NebulaeModel.Packets.Players.PlayerGiveItemPacket>(_ => { });
        var operation = writer.Write(new global::NebulaeModel.Packets.Players.PlayerGiveItemPacket());
        var bytes = writer.Write(new PlanetBroadcastPacket(new[] { operation, Array.Empty<byte>(), operation }, 101));
        var channel = new StateDeltaChannel();
        TestAssert.AreSame(bytes, channel.Prepare(bytes));
        TestAssert.AreSame(bytes, channel.Receive(bytes));
    }
}
