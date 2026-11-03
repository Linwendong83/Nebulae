using System.Runtime.CompilerServices;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic.World;

// Fixed-width comparisons avoid generic struct loops in Mono's hot pose paths.
internal static class PoseBits
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe bool Equal(float a, float b) => *(int*)&a == *(int*)&b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe bool Equal(double a, double b) => *(long*)&a == *(long*)&b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Equal(Vector3 a, Vector3 b) => Equal(a.x, b.x) && Equal(a.y, b.y) && Equal(a.z, b.z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe bool Equal(Quaternion a, Quaternion b) =>
        ((long*)&a)[0] == ((long*)&b)[0] && ((long*)&a)[1] == ((long*)&b)[1];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe bool Equal(VectorLF3 a, VectorLF3 b) =>
        ((long*)&a)[0] == ((long*)&b)[0] && ((long*)&a)[1] == ((long*)&b)[1] && ((long*)&a)[2] == ((long*)&b)[2];
}
