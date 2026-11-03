using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic.World;

internal static class StarPresentationBatch
{
    private static bool captured;
    private static PlanetData planet;
    private static VectorLF3 origin;
    private static AstroPreparation.RelativeTransform transform;
    private static Color warpColor;
    private static float warpAmount, warpBrightness;
    internal static void Begin() => captured = false;

    private static void Capture()
    {
        if (captured && VisualWrites.FrameActive) return;
        captured = true;
        planet = GameMain.localPlanet;
        if (planet != null)
        {
            origin = planet.uPosition;
            transform = new AstroPreparation.RelativeTransform(planet.runtimeRotation);
        }
        warpAmount = 0; warpBrightness = 1;
        var warp = GameMain.mainPlayer?.effect?.warpEffect;
        if (warp == null || !warp.enableWarpRedBlueShift) return;
        var shift = warp.currentWarpColorShift;
        warpAmount = Mathf.Abs(shift);
        if (warpAmount > 1f) warpAmount = 1f;
        if (warpAmount <= 0f) return;
        Color rgb;
        float multiplier;
        if (shift < 0f)
        {
            rgb = warp.maximumRedShiftColor;
            multiplier = warp.redShiftFlareBrightnessMultiplier;
            if (multiplier < 0f) multiplier = 0f;
            else if (multiplier > 1f) multiplier = 1f;
        }
        else
        {
            rgb = warp.maximumBlueShiftColor;
            multiplier = warp.blueShiftFlareBrightnessMultiplier;
            if (multiplier < 1f) multiplier = 1f;
        }
        Color.RGBToHSV(rgb, out var h, out var s, out _);
        if (shift > 0f && h > 7f / 12f) h = 7f / 12f;
        warpColor = Color.HSVToRGB(h, s, 1f);
        warpBrightness = 1f + (multiplier - 1f) * warpAmount;
    }

    internal static VectorLF3 Position(VectorLF3 position, VectorLF3 player)
    {
        Capture();
        return planet == null ? position - player : transform.PositionLF(position - origin);
    }

    internal static Color Flare(Color original, ref float brightness)
    {
        Capture();
        if (warpAmount <= 0f) return original;
        original.r += (warpColor.r - original.r) * warpAmount;
        original.g += (warpColor.g - original.g) * warpAmount;
        original.b += (warpColor.b - original.b) * warpAmount;
        brightness *= warpBrightness;
        return original;
    }
}
