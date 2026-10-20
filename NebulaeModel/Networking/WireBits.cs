using System.Runtime.CompilerServices;

namespace NebulaeModel.Networking;

/// <summary>Bit-preserving scalar conversion on the supported little-endian runtimes.</summary>
internal static class WireBits
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe int FromSingle(float value) => *(int*)&value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe float ToSingle(int value) => *(float*)&value;
}
