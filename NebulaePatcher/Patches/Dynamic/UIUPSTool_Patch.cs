#region

using HarmonyLib;
using NebulaeModel.Packets.GameStates;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIUPSTool))]
internal class UIUPSTool_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIUPSTool.ApplySimSpeedIncrease))]
    public static void ApplySimSpeedIncrease_Postfix(UIUPSTool __instance)
    {
        RequestSimSpeed(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIUPSTool.ApplySimSpeedDecrease))]
    public static void ApplySimSpeedDecrease_Postfix(UIUPSTool __instance)
    {
        RequestSimSpeed(__instance);
    }

    private static void RequestSimSpeed(UIUPSTool upsTool)
    {
        // The world speed belongs to the host. The vanilla step already applied locally for
        // instant feedback; ask the host to adopt it and let its GameStateUpdate confirm or
        // clamp the value a second later.
        if (Multiplayer.IsActive && Multiplayer.Session.LocalPlayer.IsClient)
        {
            Multiplayer.Session.Network.SendPacket(new SimSpeedPacket((float)upsTool.targetSimSpeed));
        }
    }
}
