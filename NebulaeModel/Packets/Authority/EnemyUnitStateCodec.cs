using System;
using System.IO;

namespace NebulaeModel.Packets.Authority;

/// <summary>Preserves the native behavior, targeting and firing inputs needed to resume a unit.</summary>
internal static class EnemyUnitStateCodec
{
    public const int MaxBytes = 256;
    public const int MaxPoolId = 65536;

    public static byte[] Encode(in EnemyUnitComponent unit)
    {
        var bytes = NativeComponentCodec<EnemyUnitComponent>.Encode(unit);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Enemy unit state exceeds its bound.");
        return bytes;
    }

    public static bool TryDecode(byte[] bytes, out EnemyUnitComponent unit)
    {
        unit = default;
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes) return false;
        try
        {
            return NativeComponentCodec<EnemyUnitComponent>.TryDecode(bytes, out unit) && IsValid(unit);
        }
        catch (IOException) { return false; }
        catch (ArgumentException) { return false; }
    }

    internal static bool IsValid(in EnemyUnitComponent unit) => unit.id > 0 && unit.id <= MaxPoolId &&
                unit.enemyId > 0 && unit.protoId > 0 && unit.level >= 0 && unit.level <= 100 &&
                (byte)unit.behavior <= GroundEnemyStateCodec.MaxUnitBehavior &&
                unit.assaults.count >= 0 && unit.assaults.count <= 2 &&
                Finite(unit.repulsion.x) && Finite(unit.repulsion.y) && Finite(unit.repulsion.z) &&
                Finite(unit.disturbValue);

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
