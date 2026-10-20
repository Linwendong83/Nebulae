using System;
using System.IO;

namespace NebulaeModel.Packets.Authority;

internal static class EnemyTurretStateCodec
{
    public const int MaxBytes = 512;
    public static bool Valid(byte[] bytes, bool space)
        => Valid(bytes, space, out _);

    public static bool Valid(byte[] bytes, bool space, out bool counterAttack)
    {
        counterAttack = false;
        if (bytes == null || bytes.Length == 0) return true;
        if (bytes.Length > MaxBytes) return false;
        try
        {
            if (space)
            {
                if (!NativeComponentCodec<DFSTurretComponent>.TryDecode(bytes, out var t)) return false;
                return t.id > 0 && t.enemyId > 0 && t.level >= 0 && t.level <= 100 &&
                    (byte)t.state <= 2 && t.type is EDFTurretType.Laser or EDFTurretType.Plasma && t.target.type is ETargetType.None or ETargetType.Craft or ETargetType.Player && t.target.id >= 0 &&
                    Finite(t.localDir.x) && Finite(t.localDir.y) && Finite(t.localDir.z) && Finite(t.muzzleY) && Finite(t.attackRange) && Finite(t.sensorRange);
            }
            else
            {
                if (!NativeComponentCodec<DFGTurretComponent>.TryDecode(bytes, out var t)) return false;
                counterAttack = t.state == EDFTurretState.Aiming && t.target.type == ETargetType.Player && t.target.id > 0;
                return t.id > 0 && t.enemyId > 0 && t.level >= 0 && t.level <= 100 &&
                    (byte)t.state <= 2 && t.type is EDFTurretType.Laser or EDFTurretType.Plasma && t.target.type is ETargetType.None or ETargetType.Craft or ETargetType.Player && t.target.id >= 0 &&
                    Finite(t.localDir.x) && Finite(t.localDir.y) && Finite(t.localDir.z) && Finite(t.muzzleY) && Finite(t.attackRange) && Finite(t.sensorRange);
            }
        }
        catch (IOException) { return false; }
        catch (ArgumentException) { return false; }
    }
    private static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
}
