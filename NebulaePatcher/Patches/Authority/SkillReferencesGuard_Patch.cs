#region

using HarmonyLib;

#endregion

namespace NebulaePatcher.Patches.Authority;

/// <summary>Keeps native skill teardown from dereferencing a planet factory this peer does not have.</summary>
/// <remarks>
/// <para>
/// <c>SkillSystem.ClearReferencesOnSkillRemove</c> maps the astro a skill names straight onto
/// <c>astroFactories[astroId].enemySystem</c>. That array is <c>galaxy.astrosFactory</c> itself, and a
/// Nebulae client empties it whenever it leaves a star system, so a ground skill that expires while
/// its planet is not loaded throws inside <c>SkillSystem.GameTick</c>.
/// </para>
/// <para>
/// The throw lands before the pool entry is removed, so the same entry expires again on every
/// following frame: one stranded projectile aborts the whole SpaceSector task for the rest of the
/// session, and the engine only logs the first hundred errored frames. Answering "nothing to clean"
/// is always correct here — the base references that cleanup walks belong to a factory that is gone
/// with the planet, so there is nothing left to unlink.
/// </para>
/// </remarks>
[HarmonyPatch(typeof(SkillSystem), nameof(SkillSystem.ClearReferencesOnSkillRemove))]
internal static class SkillReferencesGuard_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(SkillSystem __instance, int astroId)
    {
        if (!SpaceSector.IsPlanetAstro(astroId)) return true;
        var factories = __instance.astroFactories;
        if (factories == null || astroId >= factories.Length) return false;
        var factory = factories[astroId];
        return factory != null && factory.enemySystem != null;
    }
}
