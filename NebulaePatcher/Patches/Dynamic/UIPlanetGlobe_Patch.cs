using NebulaeModel.Networking;
#region

using HarmonyLib;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIPlanetGlobe))]
internal class UIPlanetGlobe_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIPlanetGlobe.OnNameInputEndEdit))]
    public static void OnNameInputEndEdit_Postfix()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.Factories.IsIncomingRequest.Value)
        {
            return;
        }
        if (GameMain.localPlanet != null)
        {
            // Send packet with new planet name
            Multiplayer.Session.Network.SendPacket(new NameInputPacket(GameMain.localPlanet.overrideName,
                SessionSentinels.STAR_NONE, GameMain.localPlanet.id));
        }
        else if (GameMain.localStar != null)
        {
            // Send packet with new star name
            Multiplayer.Session.Network.SendPacket(new NameInputPacket(GameMain.localStar.overrideName,
                GameMain.localStar.id, SessionSentinels.PLANET_NONE));
        }
    }
}
