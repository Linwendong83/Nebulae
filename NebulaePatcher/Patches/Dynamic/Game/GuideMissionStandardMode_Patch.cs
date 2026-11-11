#region

using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Factory;

#endregion

namespace NebulaePatcher.Patches.Dynamic.Game;

[HarmonyPatch(typeof(GuideMissionStandardMode))]
internal class GuideMissionStandardMode_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GuideMissionStandardMode.CreateSpaceCapsuleVegetable))]
    public static bool CreateSpaceCapsuleVegetable_Prefix(GuideMissionStandardMode __instance)
    {
        // GameLoader runs Skip again for a save made at tick zero. That save can
        // already contain the capsule; creating it again stacks two harvestable objects.
        if (!Multiplayer.IsActive) return true;
        return Multiplayer.Session.IsServer && !LandingCapsule.Exists(__instance.localPlanet?.factory);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(GuideMissionStandardMode.Skip))]
    public static bool Skip_Prefix()
    {
        //This prevents spawning landing capsule and preparing spawn area for the clients in multiplayer.
        return !Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost;
    }
}

[HarmonyPatch(typeof(GameScenarioLogic), nameof(GameScenarioLogic.NotifyOnVegetableRemoved))]
internal static class LandingCapsuleRemoval_Patch
{
    // The surviving capsule still exists; removing a duplicate must not mark the
    // landing mission complete or publish the world's "capsule removed" feature.
    [HarmonyPrefix]
    internal static bool Prefix() => !LandingCapsule.IsRepairing;
}
