using NebulaeModel.Networking;
#region

using HarmonyLib;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIStarDetail))]
internal class UIStarDetail_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIStarDetail.OnNameInputEndEdit))]
    public static void OnNameInputEndEdit_Postfix(UIStarDetail __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.Factories.IsIncomingRequest.Value)
        {
            return;
        }
        if (__instance.star != null)
        {
            // Send packet with new star name
            Multiplayer.Session.Network.SendPacket(new NameInputPacket(__instance.star.overrideName, __instance.star.id,
                SessionSentinels.PLANET_NONE));
        }
    }
}
