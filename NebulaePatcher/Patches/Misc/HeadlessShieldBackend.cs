using System;
using System.Globalization;
using NebulaeModel.Logger;
using UnityEngine;

namespace NebulaePatcher.Patches.Misc;

internal enum ShieldBackendKind { Native, Cpu }
internal enum ShieldBackendMode { Auto, Native, Cpu }

internal static class HeadlessShieldBackend
{
    private static readonly object Sync = new();
    private static bool initialized, stopped;
    private static ShieldBackendMode mode;
    private static ShieldBackendKind kind;
    private static ShieldNativeKernel native;

    // Unity's Null renderer returns zero for Material.GetFloat, including serialized _K=2.1.
    // This is the verified shader blend parameter, NOT an assumed coverage percentage.
    internal static float ResolveBlend(float materialValue, bool nullRenderer, string overrideValue)
    {
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            if (!float.TryParse(overrideValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                value <= 0 || float.IsInfinity(value) || float.IsNaN(value))
                throw new ArgumentException("NEBULAE_SHIELD_BLEND must be finite and positive.");
            return value;
        }
        if (materialValue > 0 && !float.IsInfinity(materialValue)) return materialValue;
        if (nullRenderer && materialValue == 0) return 2.1f;
        throw new InvalidOperationException("Shield material has no valid _K blend parameter.");
    }

    internal static float GetBlend(PlanetATField field)
    {
        var materialValue = field.displayMaterial.GetFloat("_K");
        return ResolveBlend(materialValue, SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null,
            Environment.GetEnvironmentVariable("NEBULAE_SHIELD_BLEND"));
    }

    internal static ShieldBackendMode ParseMode(string value)
    {
        switch ((value ?? "auto").Trim().ToLowerInvariant())
        {
            case "": case "auto": return ShieldBackendMode.Auto;
            case "native": return ShieldBackendMode.Native;
            case "cpu": return ShieldBackendMode.Cpu;
            default: throw new ArgumentException("NEBULAE_SHIELD_BACKEND must be auto, native or cpu.");
        }
    }

    internal static void Initialize()
    {
        lock (Sync)
        {
            if (stopped) throw new ObjectDisposedException(nameof(HeadlessShieldBackend));
            if (initialized) return;
            mode = ParseMode(Environment.GetEnvironmentVariable("NEBULAE_SHIELD_BACKEND"));
            if (mode == ShieldBackendMode.Cpu) kind = ShieldBackendKind.Cpu;
            else
            {
                try { native = new ShieldNativeKernel(); kind = ShieldBackendKind.Native; }
                catch (Exception ex)
                {
                    if (mode == ShieldBackendMode.Native) throw;
                    kind = ShieldBackendKind.Cpu;
                    Log.Warn($"[shield-backend] native initialization failed: {ex.Message}; fallback={kind}");
                }
            }
            initialized = true;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }
    }

    private static void OnProcessExit(object sender, EventArgs args) => Shutdown();

    internal static void Shutdown()
    {
        lock (Sync)
        {
            stopped = true;
            ReleaseSession();
        }
    }

    internal static void ReleaseSession()
    {
        lock (Sync)
        {
            native?.Dispose();
            native = null;
            initialized = false;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }
    }

    // Keep the constructor replacement signature without allocating Unity GPU resources.
    internal static ComputeBuffer CreatePhysicsBuffer(int _, int _1, ComputeBufferType _2) => null;

    internal static void Compute(PlanetATField field)
    {
        lock (Sync)
        {
            Initialize();
            var radius = field.planet.realRadius;
            var blend = GetBlend(field);
            ShieldCpuKernel.Validate(field.physicsMeshVertsOriginal, field.generatorMatrix, field.generatorCount,
                radius, 60.8f, 0.05f, blend, field.physicsMeshVerts, field.physicsMeshNorms, field.physicsArgs);
            if (kind == ShieldBackendKind.Native)
            {
                try
                {
                    native.Compute(field.physicsMeshVertsOriginal, field.generatorMatrix, field.generatorCount,
                        radius, 60.8f, 0.05f, blend, field.physicsMeshVerts, field.physicsMeshNorms, field.physicsArgs);
                    return;
                }
                catch (Exception ex)
                {
                    if (mode != ShieldBackendMode.Auto) throw;
                    native.Dispose(); native = null;
                    kind = ShieldBackendKind.Cpu;
                    Log.Warn($"[shield-backend] native computation failed: {ex.Message}; fallback={kind}. No retry until session reset.");
                }
            }
            ShieldCpuKernel.Compute(field.physicsMeshVertsOriginal, field.generatorMatrix, field.generatorCount,
                radius, 60.8f, 0.05f, blend, field.physicsMeshVerts, field.physicsMeshNorms, field.physicsArgs);
        }
    }
}
