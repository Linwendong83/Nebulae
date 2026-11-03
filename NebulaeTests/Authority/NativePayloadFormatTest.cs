using NebulaeModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class NativePayloadFormatTest
{
    [TestMethod]
    public void NativeComponentsRequireTheHeaderWrittenByTheirEncoder()
    {
        RequireHeader<EnemyUnitComponent>();
        RequireHeader<DFGTurretComponent>();
        RequireHeader<DFSTurretComponent>();
    }

    private static void RequireHeader<T>() where T : struct, IPoolElement
    {
        var bytes = NativeComponentCodec<T>.Encode(default);
        TestAssert.IsTrue(NativeComponentCodec<T>.TryDecode(bytes, out _));
        var header = bytes[0];
        for (var value = 0; value <= byte.MaxValue; value++)
        {
            if (value == header) continue;
            var invalid = bytes.ToArray(); invalid[0] = (byte)value;
            TestAssert.IsFalse(NativeComponentCodec<T>.TryDecode(invalid, out _));
        }
    }

    [TestMethod]
    public void CraftInputsRequireTheirFixedHeader()
    {
        var fleet = CraftNativeStateCodec.Encode(new FleetComponent { id = 1, craftId = 2 });
        var unit = CraftNativeStateCodec.Encode(new UnitComponent { id = 1, craftId = 2, protoId = 5101 });
        TestAssert.IsTrue(CraftNativeStateCodec.TryFleet(fleet, out _));
        TestAssert.IsTrue(CraftNativeStateCodec.TryUnit(unit, out _));
        foreach (var header in new[] { -1, 1, int.MaxValue })
        {
            var invalidFleet = fleet.ToArray(); BitConverter.GetBytes(header).CopyTo(invalidFleet, 0);
            var invalidUnit = unit.ToArray(); BitConverter.GetBytes(header).CopyTo(invalidUnit, 0);
            TestAssert.IsFalse(CraftNativeStateCodec.TryFleet(invalidFleet, out _));
            TestAssert.IsFalse(CraftNativeStateCodec.TryUnit(invalidUnit, out _));
        }
    }
}
