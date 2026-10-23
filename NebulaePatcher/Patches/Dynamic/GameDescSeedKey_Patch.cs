using HarmonyLib;
using NebulaeModel.Utils;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(GameDesc), nameof(GameDesc.seedKey64), MethodType.Getter)]
internal static class GameDescSeedKey_Patch
{
    // All vanilla keys (including the full signed Int32 seed range) are below this
    // namespace. Four star-count digits need a billion-wide stride between seeds.
    private const long ExtendedNamespace = 1L << 62;

    [HarmonyPostfix]
    public static void Postfix(GameDesc __instance, ref long __result)
    {
        if (__instance.starCount <= 999) return;
        GalaxyLimits.ValidateStarCount(__instance.starCount);

        // Keep vanilla resource rounding and mode/difficulty encoding exactly intact.
        var settings = __result - (long)__instance.galaxySeed * 100000000L - 99900000L;
        var seed = (long)unchecked((uint)__instance.galaxySeed);
        __result = checked(ExtendedNamespace + seed * 1000000000L +
                           (long)__instance.starCount * 100000L + settings);
    }
}
