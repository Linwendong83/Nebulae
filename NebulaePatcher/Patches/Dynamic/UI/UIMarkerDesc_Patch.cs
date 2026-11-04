#region

using HarmonyLib;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic.UI;

[HarmonyPatch(typeof(UIMarkerDesc))]
internal class UIMarkerDesc_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnDFBeaconSwitchToggle))]
    public static void OnDFBeaconSwitchToggle_Postfix(UIMarkerDesc __instance)
    {
        if (!CanSend(__instance)) return;
        Multiplayer.Session.Network.SendPacket(new MarkerSettingUpdatePacket(
            __instance.factory.planetId, __instance.marker.id,
            MarkerSettingEvent.SetDFAttractionFlags, intValue: __instance.marker.dfAttractionFlags));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnTitleInputFieldEndEdit))]
    public static void OnTitleInputFieldEndEdit_Postfix(UIMarkerDesc __instance)
    {
        if (!CanSend(__instance)) return;
        Multiplayer.Session.Network.SendPacket(
            new MarkerSettingUpdatePacket(__instance.factory.planetId, __instance.marker.id,
                MarkerSettingEvent.SetName, stringValue: __instance.marker.name));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnWordInputFieldEndEdit))]
    public static void OnWordInputFieldEndEdit_Postfix(UIMarkerDesc __instance)
    {
        if (!CanSend(__instance)) return;
        Multiplayer.Session.Network.SendPacket(
            new MarkerSettingUpdatePacket(__instance.factory.planetId, __instance.marker.id,
                MarkerSettingEvent.SetWord, stringValue: __instance.marker.word));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnTagsInputFieldEndEdit))]
    public static void OnTagsInputFieldEndEdit_Postfix(UIMarkerDesc __instance)
    {
        if (!CanSend(__instance)) return;
        Multiplayer.Session.Network.SendPacket(
            new MarkerSettingUpdatePacket(__instance.factory.planetId, __instance.marker.id,
                MarkerSettingEvent.SetTags, stringValue: __instance.marker.tags));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnTodoInputFieldEndEdit))]
    public static void OnTodoInputFieldEndEdit_Postfix(UIMarkerDesc __instance) => SendTodoUpdate(__instance);

    // The memo checkbox is written straight through PlanetFactory.WriteMarkerComment, so the field
    // end-edit that carries the text never runs. The same holds for the colour picker, the icon
    // insert and the automatic checkbox on a new line.
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnTodoCheckBoxButtonClick))]
    public static void OnTodoCheckBoxButtonClick_Postfix(UIMarkerDesc __instance) => SendTodoUpdate(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnTodoColorPickerReturn))]
    public static void OnTodoColorPickerReturn_Postfix(UIMarkerDesc __instance) => SendTodoUpdate(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnTodoAlphaChange))]
    public static void OnTodoAlphaChange_Postfix(UIMarkerDesc __instance) => SendTodoUpdate(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnTodoIconSelectSignalPickerReturn))]
    public static void OnTodoIconSelectSignalPickerReturn_Postfix(UIMarkerDesc __instance) =>
        SendTodoUpdate(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnTodoInputFieldNewLine))]
    public static void OnTodoInputFieldNewLine_Postfix(UIMarkerDesc __instance) => SendTodoUpdate(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnDigitalSignalIdEndEdit))]
    public static void OnDigitalSignalIdEndEdit_Postfix(UIMarkerDesc __instance)
    {
        if (!CanSend(__instance)) return;
        Multiplayer.Session.Network.SendPacket(
            new MarkerSettingUpdatePacket(__instance.factory.planetId, __instance.marker.id,
                MarkerSettingEvent.SetDigitalSignalId,
                intValue: __instance.marker.digitalSignalId));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnVisibilityBoxItemIndexChanged))]
    public static void OnVisibilityBoxItemIndexChanged_Postfix(UIMarkerDesc __instance)
    {
        if (!CanSend(__instance)) return;
        // Vanilla also derives marker.offline from the visibility; the processor does the same on
        // the receiving peers, so the power state stays in step with the choice.
        Multiplayer.Session.Network.SendPacket(
            new MarkerSettingUpdatePacket(__instance.factory.planetId, __instance.marker.id,
                MarkerSettingEvent.SetVisibility, intValue: (int)__instance.marker.visibility));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnDetailLevelBoxItemIndexChanged))]
    public static void OnDetailLevelBoxItemIndexChanged_Postfix(UIMarkerDesc __instance)
    {
        if (!CanSend(__instance)) return;
        Multiplayer.Session.Network.SendPacket(
            new MarkerSettingUpdatePacket(__instance.factory.planetId, __instance.marker.id,
                MarkerSettingEvent.SetDetailLevel, intValue: (int)__instance.marker.detailLevel));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnHeightSliderValueChanged))]
    public static void OnHeightSliderValueChanged_Postfix(UIMarkerDesc __instance)
    {
        if (!CanSend(__instance)) return;
        Multiplayer.Session.Network.SendPacket(
            new MarkerSettingUpdatePacket(__instance.factory.planetId, __instance.marker.id,
                MarkerSettingEvent.SetHeight, floatValue: __instance.marker.height));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnRadiusSliderValueChanged))]
    public static void OnRadiusSliderValueChanged_Postfix(UIMarkerDesc __instance)
    {
        if (!CanSend(__instance)) return;
        Multiplayer.Session.Network.SendPacket(
            new MarkerSettingUpdatePacket(__instance.factory.planetId, __instance.marker.id,
                MarkerSettingEvent.SetRadius, floatValue: __instance.marker.radius));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnIconSelectRightButtonClick))]
    public static void OnIconSelectRightButtonClick_Postfix(UIMarkerDesc __instance)
    {
        if (!CanSend(__instance)) return;
        Multiplayer.Session.Network.SendPacket(
            new MarkerSettingUpdatePacket(__instance.factory.planetId, __instance.marker.id,
                MarkerSettingEvent.SetIcon, intValue: __instance.marker.icon));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnIconSelectLeftButtonClick))]
    public static void OnIconSelectLeftButtonClick_Postfix(UIMarkerDesc __instance)
    {
        if (!CanSend(__instance)) return;
        Multiplayer.Session.Network.SendPacket(
            new MarkerSettingUpdatePacket(__instance.factory.planetId, __instance.marker.id,
                MarkerSettingEvent.SetIcon, intValue: __instance.marker.icon));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnIconSelectSignalPickerReturn))]
    public static void OnIconSelectSignalPickerReturn_Postfix(UIMarkerDesc __instance)
    {
        if (!CanSend(__instance)) return;
        Multiplayer.Session.Network.SendPacket(
            new MarkerSettingUpdatePacket(__instance.factory.planetId, __instance.marker.id,
                MarkerSettingEvent.SetIcon, intValue: __instance.marker.icon));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnBriefSwitchButtonClick))]
    public static void OnBriefSwitchButtonClick_Postfix(UIMarkerDesc __instance)
    {
        if (!CanSend(__instance)) return;
        // Brief mode owns both fields: entering it clears the icon, leaving it clears the word.
        // Sending only the icon leaves the other peer showing the old thumbnail.
        Multiplayer.Session.Network.SendPacket(
            new MarkerSettingUpdatePacket(__instance.factory.planetId, __instance.marker.id,
                MarkerSettingEvent.SetIcon, intValue: __instance.marker.icon));
        Multiplayer.Session.Network.SendPacket(
            new MarkerSettingUpdatePacket(__instance.factory.planetId, __instance.marker.id,
                MarkerSettingEvent.SetWord, stringValue: __instance.marker.word));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDesc.OnColorPanelPickerReturn))]
    public static void OnColorPanelPickerReturn_Postfix(UIMarkerDesc __instance)
    {
        if (!CanSend(__instance)) return;
        Multiplayer.Session.Network.SendPacket(
            new MarkerSettingUpdatePacket(__instance.factory.planetId, __instance.marker.id,
                MarkerSettingEvent.SetColor, intValue: __instance.marker.color));
    }

    private static bool CanSend(UIMarkerDesc __instance) =>
        Multiplayer.IsActive && !Multiplayer.Session.Warning.IsIncomingMarkerPacket &&
        __instance.marker != null && __instance.factory != null;

    private static void SendTodoUpdate(UIMarkerDesc __instance)
    {
        if (!CanSend(__instance)) return;
        var todo = __instance.marker.todo;
        if (todo == null) return;
        Multiplayer.Session.Network.SendPacket(
            new MarkerSettingUpdatePacket(__instance.factory.planetId, __instance.marker.id,
                MarkerSettingEvent.SetTodoContent, stringValue: todo.content,
                colorData: todo.contentColorIndex));
    }
}
