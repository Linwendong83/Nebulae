#region

using System;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeModel.Packets.Logistics;
using NebulaePatcher.Patches.Shared;
using NebulaeWorld;
using UnityEngine;
// ReSharper disable RedundantAssignment

#endregion

namespace NebulaePatcher.Patches.Dynamic.UI;

[HarmonyPatch(typeof(UIStationWindow))]
internal class UIStationWindow_Patch
{
    private static long lastUpdateGametick;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStationWindow.OnMaxChargePowerSliderValueChange))]
    public static bool OnMaxChargePowerSliderValueChange_Prefix(UIStationWindow __instance, float value) =>
        StationControlSync.MaxChargePower(Multiplayer.Session.StationsUI.SliderBarPacket, __instance.event_lock, value, v =>
        {
            StringBuilderUtility.WriteKMG(__instance.powerServedSB, 8, (long)(3000000.0 * v + 0.5));
            __instance.maxChargePowerValue.text = __instance.powerServedSB.ToString();
        });

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStationWindow.OnMaxTripDroneSliderValueChange))]
    public static bool OnMaxTripDroneSliderValueChange_Prefix(UIStationWindow __instance, float value) =>
        StationControlSync.MaxTripDrone(Multiplayer.Session.StationsUI.SliderBarPacket, __instance.event_lock, value, t => __instance.maxTripDroneValue.text = t);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStationWindow.OnMaxTripVesselSliderValueChange))]
    public static bool OnMaxTripVesselSliderValueChange_Prefix(UIStationWindow __instance, float value) =>
        StationControlSync.MaxTripVessel(Multiplayer.Session.StationsUI.SliderBarPacket, __instance.event_lock, value, t => __instance.maxTripVesselValue.text = t);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStationWindow.OnMinDeliverDroneValueChange))]
    public static bool OnMinDeliverDroneValueChange_Prefix(UIStationWindow __instance, float value) =>
        StationControlSync.DeliverPercent(Multiplayer.Session.StationsUI.SliderBarPacket, __instance.event_lock, StationUI.EUISettings.MinDeliverDrone, value,
            t => __instance.minDeliverDroneValue.text = t);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStationWindow.OnMinDeliverVesselValueChange))]
    public static bool OnMinDeliverVesselValueChange_Prefix(UIStationWindow __instance, float value) =>
        StationControlSync.DeliverPercent(Multiplayer.Session.StationsUI.SliderBarPacket, __instance.event_lock, StationUI.EUISettings.MinDeliverVessel, value,
            t => __instance.minDeliverVesselValue.text = t);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStationWindow.OnMaxMiningSpeedChange))]
    public static bool OnMaxMiningSpeedChanged_Prefix(UIStationWindow __instance, float value) =>
        StationControlSync.MaxMiningSpeed(Multiplayer.Session.StationsUI.SliderBarPacket, __instance.event_lock, value, t => __instance.maxMiningSpeedValue.text = t);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStationWindow.OnWarperDistanceValueChange))]
    public static bool OnWarperDistanceValueChange_Prefix(UIStationWindow __instance, float value) =>
        StationControlSync.WarperDistance(Multiplayer.Session.StationsUI.SliderBarPacket, __instance.event_lock, value,
            v =>
            {
                __instance.event_lock = true;
                __instance.warperDistanceSlider.value = v;
                __instance.event_lock = false;
            },
            t => __instance.warperDistanceValue.text = t);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIStationWindow.OnMinPilerValueChange))]
    [HarmonyPatch(nameof(UIStationWindow.OnTechPilerClick))]
    public static void OnPilerCountChange(UIStationWindow __instance)
    {
        if (StationControlSync.Locked(__instance.event_lock)) return;
        var station = StationControlSync.Station(__instance.transport, __instance.stationId);
        StationControlSync.Send(__instance.factory, station, StationUI.EUISettings.PilerCount, station.pilerCount);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStationWindow.OnWarperNecessaryClick))]
    public static bool OnWarperNecessaryClick_Prefix(UIStationWindow __instance)
    {
        if (StationControlSync.Locked(__instance.event_lock)) return true;
        StationControlSync.Send(__instance.factory, __instance.transport, __instance.stationId,
            StationUI.EUISettings.WarperNeeded, 0f);
        return StationControlSync.IsHost;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStationWindow.OnIncludeOrbitCollectorClick))]
    public static bool OnIncludeOrbitCollectorClick_Prefix(UIStationWindow __instance)
    {
        if (StationControlSync.Locked(__instance.event_lock)) return true;
        StationControlSync.Send(__instance.factory, __instance.transport, __instance.stationId,
            StationUI.EUISettings.IncludeCollectors, 0f);
        return StationControlSync.IsHost;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStationWindow.OnDroneIconClick))]
    [HarmonyPriority(Priority.First)]
    public static void OnDroneIconClick_Prefix(UIStationWindow __instance, ref int __state) =>
        __state = StationControlSync.Station(__instance.transport, __instance.stationId).idleDroneCount;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIStationWindow.OnDroneIconClick))]
    [HarmonyPriority(Priority.Last)]
    public static void OnDroneIconClick_Postfix(UIStationWindow __instance, int __state)
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
    [HarmonyPatch(nameof(UIStationWindow.OnDroneAutoReplenishButtonClick))]
    public static void OnDroneAutoReplenishButtonClick_Postfix(UIStationWindow __instance)
    {
        if (StationControlSync.Locked(__instance.event_lock)) return;
        var station = StationControlSync.Station(__instance.transport, __instance.stationId);
        StationControlSync.Send(__instance.factory, station, StationUI.EUISettings.DroneAutoReplenish,
            station.droneAutoReplenish ? 1f : 0f);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStationWindow.OnShipIconClick))]
    [HarmonyPriority(Priority.First)]
    public static void OnShipIconClick_Prefix(UIStationWindow __instance, ref int __state) =>
        __state = StationControlSync.Station(__instance.transport, __instance.stationId).idleShipCount;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIStationWindow.OnShipIconClick))]
    [HarmonyPriority(Priority.Last)]
    public static void OnShipIconClick_Postfix(UIStationWindow __instance, int __state)
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
    [HarmonyPatch(nameof(UIStationWindow.OnShipAutoReplenishButtonClick))]
    public static void OnShipAutoReplenishButtonClick_Postfix(UIStationWindow __instance)
    {
        if (StationControlSync.Locked(__instance.event_lock)) return;
        var station = StationControlSync.Station(__instance.transport, __instance.stationId);
        StationControlSync.Send(__instance.factory, station, StationUI.EUISettings.ShipAutoReplenish,
            station.shipAutoReplenish ? 1f : 0f);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStationWindow.OnWarperIconClick))]
    [HarmonyPriority(Priority.First)]
    public static void OnWarperIconClick_Prefix(UIStationWindow __instance, ref int __state) =>
        __state = StationControlSync.Station(__instance.transport, __instance.stationId).warperCount;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIStationWindow.OnWarperIconClick))]
    [HarmonyPriority(Priority.Last)]
    public static void OnWarperIconClick_Postfix(UIStationWindow __instance, int __state)
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
    [HarmonyPatch(nameof(UIStationWindow.OnGroupButtonClick))]
    public static void OnGroupButtonClick_Postfix(UIStationWindow __instance)
    {
        if (StationControlSync.Locked(__instance.event_lock) || __instance.stationId == 0 || __instance.factory == null) return;
        var station = StationControlSync.Station(__instance.transport, __instance.stationId);
        StationControlSync.Send(__instance.factory, station, StationUI.EUISettings.RemoteGroupMask,
            BitConverter.Int64BitsToDouble(station.remoteGroupMask));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIStationWindow.OnBehaviorBomboBoxItemIndexChange))]
    public static void OnBehaviorBomboBoxItemIndexChange_Postfix(UIStationWindow __instance)
    {
        if (StationControlSync.Locked(__instance.event_lock) || __instance.stationId == 0 || __instance.factory == null) return;
        var station = StationControlSync.Station(__instance.transport, __instance.stationId);
        StationControlSync.Send(__instance.factory, station, StationUI.EUISettings.RoutePriority, (int)station.routePriority);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIStationWindow._OnOpen))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnOpen_Postfix(UIStationWindow __instance)
    {
        if (!Multiplayer.IsActive || __instance.transport == null)
        {
            return;
        }

        var stationComponent = __instance.transport.stationPool[__instance.stationId];
        Multiplayer.Session.StationsUI.SliderBarPacket = new StationUI(__instance.factory.planet.id, stationComponent.id,
            stationComponent.gid, StationUI.EUISettings.None, 0);
        Multiplayer.Session.StationsUI.StorageMaxChangeId = -1;
        // We want OnNameInputSubmit only call on nameInput.onEndEdit, so remove listener on onValueChanged
        __instance.nameInput.onValueChanged.RemoveAllListeners();
        if (Multiplayer.Session.LocalPlayer.IsHost)
        {
            return;
        }
        lastUpdateGametick = GameMain.gameTick;

        // Keep the loaded replica visible while refreshing authoritative inventory.
        // for some reason advance miner has planetId set to 0, so we use UI's factory planetId
        Multiplayer.Session.Network.SendPacket(new StationUIInitialSyncRequest(__instance.factory.planetId, stationComponent.id,
            stationComponent.gid));
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStationWindow._OnUpdate))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static bool _OnUpdate_Prefix(UIStationWindow __instance)
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }

        // When releasing left mouse button
        if (Input.GetMouseButtonUp(0))
        {
            if (Multiplayer.Session.StationsUI.SliderBarPacket.SettingIndex != StationUI.EUISettings.None)
            {
                // Send SliderBarPacket when left mouse button is released
                Multiplayer.Session.Network.SendPacket(Multiplayer.Session.StationsUI.SliderBarPacket);
                Multiplayer.Session.StationsUI.SliderBarPacket.SettingIndex = StationUI.EUISettings.None;
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
            return true;
        }
        var gid = __instance.transport?.stationPool?[__instance.stationId].gid ?? 0;
        if (gid > 0)
        {
            Multiplayer.Session.Network.SendPacket(new RemoteOrderUpdate(gid, Array.Empty<int>()));
        }
        lastUpdateGametick = GameMain.gameTick;

        return true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIStationWindow.OnStationIdChange))]
    public static void OnStationIdChange_Postfix(UIStationWindow __instance)
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
