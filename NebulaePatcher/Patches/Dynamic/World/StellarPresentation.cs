using System;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic.World;

internal static class StellarPresentation
{
    internal static bool Enabled = Environment.GetEnvironmentVariable("NEBULAE_STELLAR_PRESENTATION") != "0";
    internal static bool Active => Enabled && GalaxyOptimization.Enabled;

    internal static void SetEnabled(bool enabled)
    {
        StarCallbackBatch.Restore();
        StarPresentation_Patch.Reset();
        StarmapDisplay_Patch.Reset();
        Enabled = enabled;
    }

    // Compare to the last computed value, never the previous frame: errors cannot accumulate.
    internal static bool Close(double previous, double current) => previous > 0 && current > 0 &&
        Math.Abs(previous - current) <= previous * 0.0001;
}

// Distant LateUpdate only rewrites disabled lights, render queues and a disabled black renderer.
// Own only components that were enabled; native local lighting and GPU profiling keep their callbacks.
internal static class StarCallbackBatch
{
    private static StarSimulator[] stars;
    private static bool[] owned;
    private static int local = -1;

    internal static void Refresh(UniverseSimulator universe)
    {
        if (!StellarPresentation.Active || !Multiplayer.IsActive || Multiplayer.IsDedicated ||
            GameMain.inOtherScene || GameMain.data?.guideMission != null || PerformanceMonitor.GpuProfilerOn ||
            universe?.starSimulators == null || universe.starSimulators.Length < GalaxyWork.ParallelThreshold)
        { Restore(); return; }
        var next = GameMain.localStar?.index ?? -1;
        if (!ReferenceEquals(stars, universe.starSimulators))
        {
            Restore();
            stars = universe.starSimulators; owned = new bool[stars.Length]; local = next;
            for (var i = 0; i < stars.Length; i++) if (i != local) Suspend(i);
        }
        else if (local != next)
        {
            Resume(next);
            Suspend(local);
            local = next;
        }
    }

    private static void Suspend(int index)
    {
        if ((uint)index >= (uint)stars.Length) return;
        var star = stars[index];
        if (star == null || !star.enabled) return;
        star.sunLight.enabled = false;
        star.blackRenderer.enabled = false;
        VisualWrites.Queue(star.bodyMaterial, 2979);
        VisualWrites.Queue(star.haloMaterial, 2979);
        star.enabled = false;
        owned[index] = true;
    }

    private static void Resume(int index)
    {
        if (stars == null || (uint)index >= (uint)stars.Length || !owned[index]) return;
        if (stars[index] != null) stars[index].enabled = true;
        owned[index] = false;
    }

    internal static void Restore()
    {
        if (stars != null) for (var i = 0; i < stars.Length; i++) Resume(i);
        stars = null; owned = null; local = -1;
    }
}

internal sealed class DistantStarAppearance
{
    internal struct Values
    {
        internal float Halo, Flare, Intensity, BodySize, HaloSize;
    }
    private double distance;
    private float scale, radius, luminosity;
    private Values values;
    internal bool TryReuse(double nextDistance, float nextScale, float nextRadius, float nextLuminosity, out Values result)
    {
        result = values;
        // Large/near bodies use exact arithmetic. Far sizes/intensities only reuse within 0.01% input motion.
        return nextDistance >= 2400000 && nextRadius * 6000 / nextDistance < 0.01 &&
            radius == nextRadius && luminosity == nextLuminosity &&
            StellarPresentation.Close(distance, nextDistance) && StellarPresentation.Close(scale, nextScale);
    }
    internal void Capture(double nextDistance, float nextScale, float nextRadius, float nextLuminosity, Values result)
    { distance = nextDistance; scale = nextScale; radius = nextRadius; luminosity = nextLuminosity; values = result; }
}
