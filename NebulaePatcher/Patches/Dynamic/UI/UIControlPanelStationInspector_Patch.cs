#region

using System;
using HarmonyLib;
using NebulaeModel.Packets.Logistics;
using NebulaePatcher.Patches.Shared;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Dynamic.UI;

[HarmonyPatch(typeof(UIControlPanelStationInspector))]
internal class UIControlPanelStationInspector_Patch
{
    private static long lastUpdateGametick;
    private static StationUI SliderBarPacket = new();

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnMaxChargePowerSliderValueChange))]
    public static bool OnMaxChargePowerSliderValueChange_Prefix(UIControlPanelStationInspector __instance, float value) =>
        StationControlSync.MaxChargePower(SliderBarPacket, __instance.event_lock, value, v =>
        {
            StringBuilderUtility.WriteKMG(__instance.powerServedSB, 8, (long)(3000000.0 * v + 0.5));
            __instance.maxChargePowerValue.text = __instance.powerServedSB.ToString();
        });

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnMaxTripDroneSliderValueChange))]
    public static bool OnMaxTripDroneSliderValueChange_Prefix(UIControlPanelStationInspector __instance, float value) =>
        StationControlSync.MaxTripDrone(SliderBarPacket, __instance.event_lock, value, t => __instance.maxTripDroneValue.text = t);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnMaxTripVesselSliderValueChange))]
    public static bool OnMaxTripVesselSliderValueChange_Prefix(UIControlPanelStationInspector __instance, float value) =>
        StationControlSync.MaxTripVessel(SliderBarPacket, __instance.event_lock, value, t => __instance.maxTripVesselValue.text = t);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnMinDeliverDroneValueChange))]
    public static bool OnMinDeliverDroneValueChange_Prefix(UIControlPanelStationInspector __instance, float value) =>
        StationControlSync.DeliverPercent(SliderBarPacket, __instance.event_lock, StationUI.EUISettings.MinDeliverDrone, value,
            t => __instance.minDeliverDroneValue.text = t);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnMinDeliverVesselValueChange))]
    public static bool OnMinDeliverVesselValueChange_Prefix(UIControlPanelStationInspector __instance, float value) =>
        StationControlSync.DeliverPercent(SliderBarPacket, __instance.event_lock, StationUI.EUISettings.MinDeliverVessel, value,
            t => __instance.minDeliverVesselValue.text = t);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnMaxMiningSpeedChange))]
    public static bool OnMaxMiningSpeedChanged_Prefix(UIControlPanelStationInspector __instance, float value) =>
        StationControlSync.MaxMiningSpeed(SliderBarPacket, __instance.event_lock, value, t => __instance.maxMiningSpeedValue.text = t);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnWarperDistanceValueChange))]
    public static bool OnWarperDistanceValueChange_Prefix(UIControlPanelStationInspector __instance, float value) =>
        StationControlSync.WarperDistance(SliderBarPacket, __instance.event_lock, value,
            v =>
            {
                __instance.event_lock = true;
                __instance.warperDistanceSlider.value = v;
                __instance.event_lock = false;
            },
            t => __instance.warperDistanceValue.text = t);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnMinPilerValueChange))]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnTechPilerClick))]
    public static void OnPilerCountChange(UIControlPanelStationInspector __instance)
    {
        if (StationControlSync.Locked(__instance.event_lock)) return;
        var station = StationControlSync.Station(__instance.transport, __instance.stationId);
        StationControlSync.Send(__instance.factory, station, StationUI.EUISettings.PilerCount, station.pilerCount);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnWarperNecessaryClick))]
    public static bool OnWarperNecessaryClick_Prefix(UIControlPanelStationInspector __instance)
    {
        if (StationControlSync.Locked(__instance.event_lock)) return true;
        StationControlSync.Send(__instance.factory, __instance.transport, __instance.stationId,
            StationUI.EUISettings.WarperNeeded, 0f);
        return StationControlSync.IsHost;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnIncludeOrbitCollectorClick))]
    public static bool OnIncludeOrbitCollectorClick_Prefix(UIControlPanelStationInspector __instance)
    {
        if (StationControlSync.Locked(__instance.event_lock)) return true;
        StationControlSync.Send(__instance.factory, __instance.transport, __instance.stationId,
            StationUI.EUISettings.IncludeCollectors, 0f);
        return StationControlSync.IsHost;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnDroneIconClick))]
    [HarmonyPriority(Priority.First)]
    public static void OnDroneIconClick_Prefix(UIControlPanelStationInspector __instance, ref int __state) =>
        __state = StationControlSync.Station(__instance.transport, __instance.stationId).idleDroneCount;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnDroneIconClick))]
    [HarmonyPriority(Priority.Last)]
    public static void OnDroneIconClick_Postfix(UIControlPanelStationInspector __instance, int __state)
    {
        if (StationControlSync.Locked(__instance.event_lock)) return;
        var station = StationControlSync.Station(__instance.transport, __instance.stationId);
        StationControlSync.IconCountChanged(__instance.factory, __instance.transport, __instance.stationId,
            StationUI.EUISettings.SetDroneCount, __state, station.idleDroneCount, station.workDroneCount, () =>
            {
                station.idleDroneCount = __state;
                __instance.droneIconButton.button.interactable = false;
            });
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnDroneAutoReplenishButtonClick))]
    public static void OnDroneAutoReplenishButtonClick_Postfix(UIControlPanelStationInspector __instance)
    {
        if (StationControlSync.Locked(__instance.event_lock)) return;
        var station = StationControlSync.Station(__instance.transport, __instance.stationId);
        StationControlSync.Send(__instance.factory, station, StationUI.EUISettings.DroneAutoReplenish,
            station.droneAutoReplenish ? 1f : 0f);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnShipIconClick))]
    [HarmonyPriority(Priority.First)]
    public static void OnShipIconClick_Prefix(UIControlPanelStationInspector __instance, ref int __state) =>
        __state = StationControlSync.Station(__instance.transport, __instance.stationId).idleShipCount;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnShipIconClick))]
    [HarmonyPriority(Priority.Last)]
    public static void OnShipIconClick_Postfix(UIControlPanelStationInspector __instance, int __state)
    {
        if (StationControlSync.Locked(__instance.event_lock)) return;
        var station = StationControlSync.Station(__instance.transport, __instance.stationId);
        StationControlSync.IconCountChanged(__instance.factory, __instance.transport, __instance.stationId,
            StationUI.EUISettings.SetShipCount, __state, station.idleShipCount, station.workShipCount, () =>
            {
                station.idleShipCount = __state;
                __instance.shipIconButton.button.interactable = false;
            });
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnShipAutoReplenishButtonClick))]
    public static void OnShipAutoReplenishButtonClick_Postfix(UIControlPanelStationInspector __instance)
    {
        if (StationControlSync.Locked(__instance.event_lock)) return;
        var station = StationControlSync.Station(__instance.transport, __instance.stationId);
        StationControlSync.Send(__instance.factory, station, StationUI.EUISettings.ShipAutoReplenish,
            station.shipAutoReplenish ? 1f : 0f);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnWarperIconClick))]
    [HarmonyPriority(Priority.First)]
    public static void OnWarperIconClick_Prefix(UIControlPanelStationInspector __instance, ref int __state) =>
        __state = StationControlSync.Station(__instance.transport, __instance.stationId).warperCount;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnWarperIconClick))]
    [HarmonyPriority(Priority.Last)]
    public static void OnWarperIconClick_Postfix(UIControlPanelStationInspector __instance, int __state)
    {
        if (StationControlSync.Locked(__instance.event_lock)) return;
        var station = StationControlSync.Station(__instance.transport, __instance.stationId);
        StationControlSync.IconCountChanged(__instance.factory, __instance.transport, __instance.stationId,
            StationUI.EUISettings.SetWarperCount, __state, station.warperCount, 0, () =>
            {
                station.warperCount = __state;
                __instance.warperIconButton.button.interactable = false;
            });
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnGroupButtonClick))]
    public static void OnGroupButtonClick_Postfix(UIControlPanelStationInspector __instance)
    {
        if (StationControlSync.Locked(__instance.event_lock) || __instance.stationId == 0 || __instance.factory == null) return;
        var station = StationControlSync.Station(__instance.transport, __instance.stationId);
        StationControlSync.Send(__instance.factory, station, StationUI.EUISettings.RemoteGroupMask,
            BitConverter.Int64BitsToDouble(station.remoteGroupMask));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnBehaviorBomboBoxItemIndexChange))]
    public static void OnBehaviorBomboBoxItemIndexChange_Postfix(UIControlPanelStationInspector __instance)
    {
        if (StationControlSync.Locked(__instance.event_lock) || __instance.stationId == 0 || __instance.factory == null) return;
        var station = StationControlSync.Station(__instance.transport, __instance.stationId);
        StationControlSync.Send(__instance.factory, station, StationUI.EUISettings.RoutePriority, (int)station.routePriority);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector._OnOpen))]
    public static void _OnOpen_Postfix(UIControlPanelStationInspector __instance)
    {
        if (!Multiplayer.IsActive || __instance.transport == null)
        {
            return;
        }

        lastUpdateGametick = 0;
        var stationComponent = __instance.transport.stationPool[__instance.stationId];
        SliderBarPacket = new StationUI(__instance.factory.planet.id, stationComponent.id,
            stationComponent.gid, StationUI.EUISettings.None, 0);
        Multiplayer.Session.StationsUI.StorageMaxChangeId = -1;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector._OnUpdate))]
    public static void _OnUpdate_Prefix(UIControlPanelStationInspector __instance)
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }

        // When releasing left mouse button
        if (Input.GetMouseButtonUp(0))
        {
            if (SliderBarPacket.SettingIndex != StationUI.EUISettings.None)
            {
                // Send SliderBarPacket when left mouse button is released
                Multiplayer.Session.Network.SendPacket(SliderBarPacket);
                SliderBarPacket.SettingIndex = StationUI.EUISettings.None;
            }
            if (Multiplayer.Session.StationsUI.StorageMaxChangeId >= 0)
            {
                // Do the job in UIStationStorage.OnMaxSliderValueChange()
                var index = Multiplayer.Session.StationsUI.StorageMaxChangeId;
                var val = __instance.storageUIs[index].maxSlider.value;
                var stationStore = __instance.transport.stationPool[__instance.stationId].storage[index];
                __instance.transport.SetStationStorage(__instance.stationId, index, stationStore.itemId,
                    (int)(val * 100f + 0.5f), stationStore.localLogic, stationStore.remoteLogic, GameMain.mainPlayer);

                // In client side, preserve displaying slider value until host response
                Multiplayer.Session.StationsUI.StorageMaxChangeId = Multiplayer.Session.LocalPlayer.IsHost ? -1 : -2;
            }
        }

        // Request for remoteOrder update every 60tick
        if (!Multiplayer.Session.LocalPlayer.IsClient || GameMain.gameTick - lastUpdateGametick <= 60)
        {
            return;
        }
        var gid = __instance.transport?.stationPool?[__instance.stationId].gid ?? 0;
        if (gid > 0)
        {
            Multiplayer.Session.Network.SendPacket(new RemoteOrderUpdate(gid, Array.Empty<int>()));
        }
        lastUpdateGametick = GameMain.gameTick;

        return;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIControlPanelStationInspector.OnStationIdChange))]
    public static void OnStationIdChange_Postfix(UIControlPanelStationInspector __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return;
        }

        __instance.warperIconButton.button.interactable = true;
        __instance.shipIconButton.button.interactable = true;
        __instance.droneIconButton.button.interactable = true;
    }
}
