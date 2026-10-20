#region

using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeModel.Packets.Factory;
using NebulaeModel.Packets.Factory.Ejector;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIEjectorWindow))]
internal class UIEjectorWindow_Patch
{
    private static bool boost;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIEjectorWindow.OnSetOrbit))]
    public static void OnSetOrbit_Postfix(UIEjectorWindow __instance, int orbitId)
    {
        //Notify about target orbit change
        if (Multiplayer.IsActive)
        {
            Multiplayer.Session.Network.SendPacketToLocalStar(new EjectorOrbitUpdatePacket(__instance.ejectorId, orbitId,
                GameMain.localPlanet?.id ?? -1));
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIEjectorWindow.OnClickAutoOrbitSwitchButton))]
    public static void OnClickAutoOrbitSwitchButton_Postfix(UIEjectorWindow __instance)
    {
        if (!Multiplayer.IsActive || __instance.ejectorId == 0 || __instance.factory == null) return;

        var autoOrbit = __instance.factory.factorySystem.ejectorPool[__instance.ejectorId].autoOrbit;
        Multiplayer.Session.Network.SendPacketToLocalStar(new EjectorAutoOrbitUpdatePacket(__instance.ejectorId, autoOrbit,
            GameMain.localPlanet?.id ?? -1));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIEjectorWindow.OnApplyPlanetButtonClick))]
    public static void OnApplyPlanetButtonClick_Postfix(UIEjectorWindow __instance)
    {
        if (!Multiplayer.IsActive || __instance.ejectorId == 0 || __instance.factory == null) return;

        var autoOrbit = __instance.factory.factorySystem.ejectorPool[__instance.ejectorId].autoOrbit;
        Multiplayer.Session.Network.SendPacketToLocalStar(new EjectorAutoOrbitUpdatePacket(-1, autoOrbit,
            GameMain.localPlanet?.id ?? -1)); // ejectorId = -1 as setting whole planet
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIEjectorWindow.OnEjectorIdChange))]
    public static void OnEjectorIdChange_Postfix(UIEjectorWindow __instance)
    {
        if (Multiplayer.IsActive && __instance.active)
        {
            boost = __instance.boostSwitch.isOn;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIEjectorWindow._OnUpdate))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnUpdate_Prefix(UIEjectorWindow __instance)
    {
        //Notify about boost change in sandbox mode
        if (!Multiplayer.IsActive || boost == __instance.boostSwitch.isOn)
        {
            return;
        }
        boost = __instance.boostSwitch.isOn;
        Multiplayer.Session.Network.SendPacketToLocalStar(new EntityBoostSwitchPacket
            (GameMain.localPlanet?.id ?? -1, EBoostEntityType.Ejector, __instance.ejectorId, boost));
    }
}
