using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UnityEngine;

namespace NebulaePatcher.Patches.Misc;

// In-process D3D11 compute device, independent of Unity's GfxDevice. Under Proton this uses DXVK.
// No process, window, swapchain, IPC listener, or service survives the game.
internal sealed class ShieldNativeKernel : IDisposable
{
    private const string ShaderHash = "a35923d287b3d6c5b02558f6fcadba04e157601c9cf84c24bdb03f3e467cff29";
    private static readonly object ModuleLock = new();
    private static IntPtr module;
    private readonly ContextHandle context;
    internal string Adapter { get; }

    internal ShieldNativeKernel()
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Native shields require a 64-bit process.");
        var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var shader = File.ReadAllBytes(Path.Combine(directory, "planet-shield.dxbc"));
        using (var sha = SHA256.Create())
        {
            var hash = BitConverter.ToString(sha.ComputeHash(shader)).Replace("-", "").ToLowerInvariant();
            if (hash != ShaderHash) throw new InvalidDataException("Shield shader is not the verified original kernel; refusing its unknown layout.");
        }
        lock (ModuleLock)
        {
            if (module == IntPtr.Zero)
            {
                module = LoadLibraryEx(Path.Combine(directory, "NebulaeShieldNative.dll"), IntPtr.Zero, 8);
                if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Load native shield module");
            }
        }
        if (Native.Abi() != 1) throw new InvalidDataException("Native shield ABI mismatch.");
        var error = new StringBuilder(1024);
        var adapter = new StringBuilder(256);
        var result = Native.Create(shader, shader.Length, out var handle, adapter, adapter.Capacity, error, error.Capacity);
        if (result != 0) throw new InvalidOperationException(error.ToString());
        context = new ContextHandle(handle);
        Adapter = adapter.ToString();
    }

    internal void Compute(Vector3[] source, Vector4[] generators, int count, float radius,
        float altitude, float scale, float blend, Vector3[] vertices, Vector3[] normals, uint[] args)
    {
        ShieldCpuKernel.Validate(source, generators, count, radius, altitude, scale, blend, vertices, normals, args);
        var error = new StringBuilder(1024);
        var result = Native.Run(context, source, source.Length, generators, count, radius, altitude, scale, blend,
            vertices, normals, args, error, error.Capacity);
        if (result != 0) throw new InvalidOperationException(error.ToString());
    }

    public void Dispose() => context?.Dispose();

    private sealed class ContextHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal ContextHandle(IntPtr value) : base(true) { SetHandle(value); }
        protected override bool ReleaseHandle() { Native.Destroy(handle); return true; }
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

    private static class Native
    {
        private const string Library = "NebulaeShieldNative.dll";
        [DllImport(Library, EntryPoint = "NebulaeShieldAbi", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Abi();
        [DllImport(Library, EntryPoint = "NebulaeShieldCreate", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int Create([In] byte[] shader, int size, out IntPtr context, StringBuilder adapter,
            int adapterSize, StringBuilder error, int errorSize);
        [DllImport(Library, EntryPoint = "NebulaeShieldRun", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int Run(ContextHandle context, [In] Vector3[] source, int vertices, [In] Vector4[] generators,
            int count, float radius, float altitude, float scale, float blend, [Out] Vector3[] output,
            [Out] Vector3[] normals, [Out] uint[] args, StringBuilder error, int errorSize);
        [DllImport(Library, EntryPoint = "NebulaeShieldDestroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(IntPtr context);
    }
}
