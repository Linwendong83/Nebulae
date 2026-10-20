#region

using HarmonyLib;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIControlPanelStationRouteEntry))]
internal class UIControlPanelStationRouteEntry_Patch
{
    private const int PROTOID_PLS = 2103; // Planetary logistics station
    private const int PROTOID_ILS = 2104; // Interstellar logistics station
    private const int PROTOID_OC = 2105;  // Orbital collector
    private const int PROTOID_AMM = 2316; // Advanced mining machine


    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelStationRouteEntry._OnUpdate))]
    public static bool OnUpdate_Prefix(UIControlPanelStationRouteEntry __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer || __instance.factory != null)
        {
            return true;
        }

        // On client, change all parts that require to access the factory
        if (__instance.gameData == null || __instance.station == null || __instance.planet == null)
        {
            return false;
        }
        // Set station icon using the attributes of station.
        var protoId = __instance.station.isStellar ?
            (__instance.station.isCollector ? PROTOID_OC : PROTOID_ILS) :
            (__instance.station.isVeinCollector ? PROTOID_AMM : PROTOID_PLS);
        var itemProto = LDB.items.Select(protoId);
        var iconSprite = itemProto?.iconSprite;
        if (__instance.stationIconImage.sprite != iconSprite)
            __instance.stationIconImage.sprite = iconSprite;

        // Set planet display name. Remote route rows re-run this every frame while the
        // panel is open, so only write when the value actually changed: every uGUI text
        // or color write dirties the canvas and forces a rebuild.
        var displayName = __instance.planet.displayName;
        var planetName = __instance.isInterstellarLogistics && __instance.isLocal
            ? displayName + string.Format("<color=#{0}>(" + "当前星球".Translate() + ")</color>", ColorUtility.ToHtmlStringRGBA(__instance.stationInspector.currentPlanetColor))
            : displayName;
        if (__instance.planetNameText.text != planetName)
            __instance.planetNameText.text = planetName;

        // Set special station name
        var extraInfoName = ""; // Can't ReadExtraInfoOnEntity, so assume empty string
        var stationNameColor = (extraInfoName == "") ? __instance.stationInspector.unnamedColor : __instance.stationInspector.renamedColor;
        if (__instance.stationNameText.color != stationNameColor)
            __instance.stationNameText.color = stationNameColor;
        // Show more informative name instead of "unnamed"
        var stationName = "#" + __instance.planet.astroId + "-" + __instance.station.id;
        if (__instance.stationNameText.text != stationName)
            __instance.stationNameText.text = stationName;
        __instance.UpdateStorageItem();

        // Show estimated time
        var stationComponent = __instance.stationInspector.station;
        if (stationComponent.isCollector || stationComponent.isVeinCollector)
        {
            if (__instance.estimatedTimeText.color != Color.clear)
                __instance.estimatedTimeText.color = Color.clear;
        }
        else
        {
            if (__instance.estimatedTimeText.color != __instance.stationInspector.estimatedTimeColor)
                __instance.estimatedTimeText.color = __instance.stationInspector.estimatedTimeColor;
            if (__instance.routeResult.routeType == EUIControlPanelRouteType.NormalLocal)
            {
                var time = (2 * stationComponent.CalcLocalSingleTripTime(__instance.station, __instance.gameData.history.logisticDroneSpeedModified));
                var estimatedTime = string.Format("预估耗时".Translate(), (time / 60.0).ToString("f2") + " s");
                if (__instance.estimatedTimeText.text != estimatedTime)
                    __instance.estimatedTimeText.text = estimatedTime;
            }
            else
            {
                var warperCount = stationComponent.warperCount;
                var time = (double)stationComponent.CalcRemoteSingleTripTime(__instance.gameData.galaxy.astrosData, __instance.station, __instance.gameData.history.logisticShipSailSpeedModified, __instance.gameData.history.logisticShipWarpSpeedModified, warperCount > 0, 1, 0);
                time += stationComponent.CalcRemoteSingleTripTime(__instance.gameData.galaxy.astrosData, __instance.station, __instance.gameData.history.logisticShipSailSpeedModified, __instance.gameData.history.logisticShipWarpSpeedModified, warperCount > 1, -1, 0);
                var time_tick = (int)(time + 0.5);
                var time_sec = time_tick / 60;
                var time_min = time_sec / 60;
                var time_hour = time_min / 60;
                time_sec %= 60;
                time_min %= 60;
                string formattedTimeText;
                if (time_hour > 0)
                {
                    formattedTimeText = string.Format("{0:0}\u2009h\u2008{1:00}\u2009min\u2008{2:00}\u2009s", time_hour, time_min, time_sec);
                }
                else if (time_min > 0)
                {
                    formattedTimeText = string.Format("{0:0}\u2009min\u2008{1:00}\u2009s", time_min, time_sec);
                }
                else
                {
                    formattedTimeText = string.Format("{0:0.0}\u2009s", time / 60.0);
                }
                var estimatedTime = string.Format("预估耗时".Translate(), formattedTimeText);
                if (__instance.estimatedTimeText.text != estimatedTime)
                    __instance.estimatedTimeText.text = estimatedTime;
            }
        }

        // Set isDisplayPairing
        if (__instance.routeResult.routeType != EUIControlPanelRouteType.NormalLocal)
        {
            var hash = Maths.CombineIdsToHash(stationComponent.gid, __instance.routeResult.targetId);
            __instance.isDisplayPairing = __instance.gameData.preferences.uiControlPanelInterstellarPairing == hash;
        }
        else if (__instance.planet != null)
        {
            var hash = Maths.CombineIdsToHash(stationComponent.id, __instance.routeResult.targetId);
            if (__instance.planet.id == __instance.gameData.preferences.uiControlPanelIntraplanetaryPairingPlanetId)
            {
                __instance.isDisplayPairing = __instance.gameData.preferences.uiControlPanelIntraplanetaryPairing == hash;
            }
            else
            {
                __instance.isDisplayPairing = false;
            }
        }
        else
        {
            __instance.isDisplayPairing = false;
        }
        __instance.showRouteButton.highlighted = __instance.isDisplayPairing;

        return false;
    }
}
