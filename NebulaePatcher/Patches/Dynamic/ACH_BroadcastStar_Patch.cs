#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(ACH_BroadcastStar))]
internal class ACH_BroadcastStar_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(ACH_BroadcastStar.OnGameTick))]
    [HarmonyPatch(nameof(ACH_BroadcastStar.TryAlterEntity))]
    public static bool Block_On_Client_Prefix()
    {
        // ACH_BroadcastStar is for the achievement "Let there be light!"
        // which needs to light up Artificial Star (2210) on certain planets
        // Clients only have partial factories loaded so they won't get the correct stats.
        // Therefore it's disabled to prevent errors.
        return !Multiplayer.IsActive || Multiplayer.Session.IsServer;
    }
}
