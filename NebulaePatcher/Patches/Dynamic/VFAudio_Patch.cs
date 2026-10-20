#region

using System;
using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(VFAudio))]
internal class VFAudio_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(VFAudio.Play), new Type[] { })]
    public static bool Play_Prefix()
    {
        if (!Multiplayer.IsActive) return true;

        // Only play sounds from events on the same planet.
        if (Multiplayer.Session.Factories.IsIncomingRequest.Value)
        {
            var onLocalPlanet = Multiplayer.Session.Factories.TargetPlanet == GameMain.localPlanet?.id;
            return onLocalPlanet;
        }
        if (Multiplayer.Session.Planets.IsIncomingRequest.Value)
        {
            var onLocalPlanet = Multiplayer.Session.Planets.TargetPlanet == GameMain.localPlanet?.id;
            return onLocalPlanet;
        }
        return true;
    }
}
