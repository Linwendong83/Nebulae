namespace NebulaeModel.Packets.Authority;

/// <summary>Bit-exact native input cache. Padding may cause a redundant export, never stale bytes.</summary>
internal sealed class NativeExportCache<T> where T : unmanaged, IPoolElement
{
    private T previous;
    private byte[] bytes;

    public byte[] Read(in T value)
    {
        if (bytes != null && SameBits(previous, value)) return bytes;
        var encoded = NativeComponentCodec<T>.Encode(value);
        previous = value;
        bytes = encoded;
        return bytes;
    }

    private static unsafe bool SameBits(T a, T b)
    {
        var left = (byte*)&a;
        var right = (byte*)&b;
        for (var i = 0; i < sizeof(T); i++)
            if (left[i] != right[i]) return false;
        return true;
    }
}
