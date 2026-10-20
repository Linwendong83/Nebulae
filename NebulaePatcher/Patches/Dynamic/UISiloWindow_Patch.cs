#region

using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeModel.Packets.Factory;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UISiloWindow))]
internal class UISiloWindow_Patch
{
    private static bool boost;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UISiloWindow.OnSiloIdChange))]
    public static void OnSiloIdChange_Postfix(UISiloWindow __instance)
    {
        if (Multiplayer.IsActive && __instance.active)
        {
            boost = __instance.boostSwitch.isOn;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UISiloWindow._OnUpdate))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnUpdate_Prefix(UISiloWindow __instance)
    {
        //Notify about boost change in sandbox mode
        if (!Multiplayer.IsActive || boost == __instance.boostSwitch.isOn)
        {
            return;
        }
        boost = __instance.boostSwitch.isOn;
        Multiplayer.Session.Network.SendPacketToLocalStar(new EntityBoostSwitchPacket
            (GameMain.localPlanet?.id ?? -1, EBoostEntityType.Silo, __instance.siloId, boost));
    }
}
