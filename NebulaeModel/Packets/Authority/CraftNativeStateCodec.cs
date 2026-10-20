using System;
using System.IO;

namespace NebulaeModel.Packets.Authority;

/// <summary>Native fleet/unit inputs; world identity remains the enclosing craft key.</summary>
internal static class CraftNativeStateCodec
{
    public const int MaxBytes = 256;
    public const int MaxPoolId = 65536;

    public static byte[] Encode(in FleetComponent fleet)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        fleet.Export(writer); return stream.ToArray();
    }
    public static byte[] Encode(in UnitComponent unit)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        unit.Export(writer); writer.Write(unit.anim); writer.Write(unit.steering); writer.Write(unit.speed);
        return stream.ToArray();
    }
    public static bool TryFleet(byte[] bytes, out FleetComponent fleet)
    {
        fleet = default;
        if (bytes == null || bytes.Length < sizeof(int) || bytes.Length > MaxBytes || BitConverter.ToInt32(bytes, 0) != 0) return false;
        try
        {
            using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
            fleet.Import(reader);
            return stream.Position == stream.Length && fleet.id > 0 && fleet.id <= MaxPoolId && fleet.craftId > 0 &&
                Finite(fleet.targetPos.x) && Finite(fleet.targetPos.y) && Finite(fleet.targetPos.z);
        }
        catch (IOException) { return false; }
        catch (ArgumentException) { return false; }
    }
    public static bool TryUnit(byte[] bytes, out UnitComponent unit)
    {
        unit = default;
        if (bytes == null || bytes.Length < sizeof(int) || bytes.Length > MaxBytes || BitConverter.ToInt32(bytes, 0) != 0) return false;
        try
        {
            using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
            unit.Import(reader); unit.anim = reader.ReadSingle(); unit.steering = reader.ReadSingle(); unit.speed = reader.ReadSingle();
            return stream.Position == stream.Length && unit.id > 0 && unit.id <= MaxPoolId && unit.craftId > 0 &&
                unit.protoId > 0 && (byte)unit.behavior <= (byte)EUnitBehavior.Engage &&
                unit.currentInitializeValue >= 0 && unit.currentInitializeValue <= 600000 &&
                Finite(unit.anim) && Finite(unit.steering) && Finite(unit.speed);
        }
        catch (IOException) { return false; }
        catch (ArgumentException) { return false; }
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
