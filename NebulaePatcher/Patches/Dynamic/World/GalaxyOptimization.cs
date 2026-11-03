using System;

namespace NebulaePatcher.Patches.Dynamic.World;

// A single switch allows same-process A/B checks without changing simulation speed.
internal static class GalaxyOptimization
{
    internal static bool Enabled = Environment.GetEnvironmentVariable("NEBULAE_GALAXY_OPTIMIZATIONS") != "0";
    // This implementation is opt-in: the 8191-star client benchmark found that even
    // exact-argument cache traffic outweighed the saved native pure-function calls.
    internal static bool PoseReuseEnabled = Environment.GetEnvironmentVariable("NEBULAE_POSE_REUSE") == "1";
    internal static void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        // Switching is a main-thread boundary operation, after queued old-mode copies.
        AstroUploads.Flush();
        StarCallbackBatch.Restore();
        StarmapDisplay_Patch.Reset();
        StarPresentation_Patch.Reset();
        StarPresentationBatch.Begin();
        Enabled = enabled;
    }
}
