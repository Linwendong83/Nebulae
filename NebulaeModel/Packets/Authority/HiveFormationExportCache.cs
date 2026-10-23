using System;
using System.IO;

namespace NebulaeModel.Packets.Authority;

/// <summary>Exact port comparison avoids repacking an idle hive on every summary sample.</summary>
internal sealed class HiveFormationExportCache
{
    private readonly int[][] previous = new int[3][];
    private readonly byte[][] packed = new byte[3][];

    public void Write(BinaryWriter writer, int form, int[] units)
    {
        var count = HiveFormationStateCodec.PortCount(form);
        if (units?.Length != count + 1) throw new InvalidDataException("Invalid hive formation port count.");
        if (!SamePorts(previous[form], units))
        {
            // Use the canonical writer, including its validation and padding rules.
            using var stream = new MemoryStream((count + 3) / 4);
            using var binary = new BinaryWriter(stream);
            HiveFormationStateCodec.Write(binary, form, units);
            var bytes = stream.ToArray();
            previous[form] ??= new int[units.Length];
            Array.Copy(units, previous[form], units.Length);
            packed[form] = bytes;
        }
        writer.Write(packed[form]);
    }

    private static unsafe bool SamePorts(int[] a, int[] b)
    {
        if (a == null || a.Length != b.Length) return false;
        // Port zero is not exported. Compare pairs without boxing or enumerable adapters.
        fixed (int* left = a, right = b)
        {
            var i = 1;
            for (; i + 1 < b.Length; i += 2)
                if (*(long*)(left + i) != *(long*)(right + i)) return false;
            return i == b.Length || left[i] == right[i];
        }
    }
}
