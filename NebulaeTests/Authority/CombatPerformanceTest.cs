using HarmonyLib;
using NebulaePatcher.Patches.Authority;
using NebulaeNetwork;
using NebulaeModel.Authority;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class CombatPerformanceTest
{
    [TestMethod]
    public void LowFrameRateKeepsNetworkServiceBoundedWithoutStarvingAuthoritativeRemovalPackets()
    {
        TestAssert.AreEqual(2d, Client.PacketBudget(1d / 60, false));
        TestAssert.AreEqual(12d, Client.PacketBudget(.1, false));
        TestAssert.AreEqual(50d, Client.PacketBudget(1, false));
        TestAssert.AreEqual(8d, Client.PacketBudget(1d / 60, true));
        TestAssert.AreEqual(2d, Client.PacketBudget(double.NaN, false));
    }
    [TestMethod]
    public void MultiplayerOneUpsUsesTheNativeExplicitRateBranchInsteadOfDefaultSixty()
    {
        var method = AccessTools.Method(typeof(FPSController), "Update");
        var code = FPSControllerClock_Patch.Transpiler(PatchProcessor.GetOriginalInstructions(method)).ToList();
        TestAssert.AreEqual(1, code.Count(x => x.Calls(AccessTools.Method(typeof(FPSControllerClock_Patch), nameof(FPSControllerClock_Patch.ExplicitRateThreshold)))));
    }

    private static EnemyUnitComponent Unit(int id = 9) => new()
    {
        id = id,
        enemyId = 3,
        planetId = 101,
        protoId = 8128,
        level = 8,
        behavior = EEnemyBehavior.Engage,
        stateTick = 17,
        fire0 = 20,
        hatred = new HatredList { max = new HatredTarget { target = ((int)ETargetType.Player << 26) | 7, value = 100000 } }
    };

    private static byte[] Ground(int hp = 500)
    {
        var body = EnemyUnitStateCodec.Encode(Unit());
        var state = new GroundEnemyState(GroundEnemyKind.GroundUnit, true, true, 8128, 45, 2, 3, 0,
            101, 101, 7, 0, 8, 10, 0, 20, 0, 0, 0, 1, 1, 2, 3, hp, 900, 2, 0,
            sampleTick: 10, nativeUnit: body);
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(state, out var bytes));
        return bytes;
    }

    [TestMethod]
    public void ScalarBitConversionsPreserveSignedZeroInfinityAndNanPayloads()
    {
        foreach (var bits in new[] { 0, int.MinValue, 1, 0x3f800000, 0x7f800000, 0x7fc00001, unchecked((int)0xff800000) })
        {
            var value = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
            TestAssert.AreEqual(bits, WireBits.FromSingle(value));
            TestAssert.AreEqual(bits, BitConverter.ToInt32(BitConverter.GetBytes(WireBits.ToSingle(bits)), 0));
        }
    }

    [TestMethod]
    public void NativeScratchMatchesVanillaAndRecoversAfterTruncationOnEveryThread()
    {
        Parallel.For(0, 32, worker =>
        {
            for (var repeat = 0; repeat < 20; repeat++)
            {
                var unit = Unit(5 + worker * 256 + repeat);
                using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
                unit.Export(writer);
                var encoded = EnemyUnitStateCodec.Encode(unit);
                CollectionAssert.AreEqual(stream.ToArray(), encoded);
                TestAssert.IsFalse(EnemyUnitStateCodec.TryDecode(encoded.Take(encoded.Length - 1).ToArray(), out _));
                TestAssert.IsTrue(EnemyUnitStateCodec.TryDecode(encoded, out var decoded));
                TestAssert.AreEqual(unit.id, decoded.id);
                TestAssert.AreEqual(unit.hatred.max.value, decoded.hatred.max.value);
            }
        });
    }

    [TestMethod]
    public void EnemyLayoutsAreSharedAndCanonicalNativeInputsAreParsedOnlyOnce()
    {
        var first = Ground(); var second = Ground(400);
        var fields = AuthorityStateFields.Describe(PoolKind.GroundEnemy, first);
        TestAssert.IsNotNull(fields);
        TestAssert.AreSame(fields, AuthorityStateFields.Describe(PoolKind.GroundEnemy, second));
        var incoming = (byte[])first.Clone();
        TestAssert.IsNotNull(AuthorityStateFields.Describe(PoolKind.GroundEnemy, incoming));
        TestAssert.IsTrue(AuthorityStateFields.TryReadGround(incoming, out var one));
        TestAssert.IsTrue(AuthorityStateFields.TryReadGround(incoming, out var two));
        TestAssert.AreSame(one.NativeUnit, two.NativeUnit, "Validation and binding reuse the owned canonical parse.");
        TestAssert.IsTrue(one.DecodedNativeUnit.HasValue);
        TestAssert.AreEqual(9, one.DecodedNativeUnit.Value.id);
    }

    [TestMethod]
    public void SharedLayoutNeverBypassesIncomingValidationAndRawDecodersNeverCacheMutableInput()
    {
        var bytes = Ground();
        var incoming = (byte[])bytes.Clone(); incoming[1] |= 0x80;
        TestAssert.IsNull(AuthorityStateFields.Describe(PoolKind.GroundEnemy, incoming));
        TestAssert.IsFalse(AuthorityStateFields.TryReadGround(incoming, out _));
        TestAssert.IsTrue(GroundEnemyStateCodec.TryDecode(bytes, 0, bytes.Length, out _, out _));
        bytes[1] |= 0x80;
        TestAssert.IsFalse(GroundEnemyStateCodec.TryDecode(bytes, 0, bytes.Length, out _, out _));
    }

    [TestMethod]
    public void CompletedWireBufferIsImmutableAndNoLongerWritable()
    {
        var writer = new AuthorityPayloadWriter(4); writer.WriteInt(0x12345678);
        var bytes = writer.Finish();
        CollectionAssert.AreEqual(new byte[] { 0x78, 0x56, 0x34, 0x12 }, bytes);
        TestAssert.ThrowsExactly<InvalidOperationException>(() => writer.WriteByte(1));
        CollectionAssert.AreEqual(new byte[] { 0x78, 0x56, 0x34, 0x12 }, bytes);
    }
}
