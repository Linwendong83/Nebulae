using System.Linq;
using HarmonyLib;
using NebulaeModel.Packets.Logistics.ControlPanel;
using NebulaeNetwork.PacketProcessors.Logistics.ControlPanel;
using NebulaeWorld;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIStationRoutePanel))]
internal class UIStationRoutePanel_Patch
{
    [HarmonyPostfix, HarmonyPatch(nameof(UIStationRoutePanel._OnOpen))]
    public static void Open(UIStationRoutePanel __instance)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient) Multiplayer.Session.FacilityDirectory.Subscribe(__instance);
    }

    [HarmonyPostfix, HarmonyPatch(nameof(UIStationRoutePanel._OnClose))]
    public static void Close(UIStationRoutePanel __instance)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsClient) Multiplayer.Session.FacilityDirectory.Unsubscribe(__instance);
    }

    [HarmonyPrefix, HarmonyPatch(nameof(UIStationRoutePanel.OnSearchNameInputChanged))]
    public static bool OnSearchNameInputChanged_Prefix(UIStationRoutePanel __instance, string str)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        var directory = Multiplayer.Session.FacilityDirectory;
        if (__instance.active) directory.Subscribe(__instance);
        var names = string.IsNullOrEmpty(str) ? new string[0] : directory.Search(str, __instance.station.planetId, false).Select(e => e.Name).Distinct().ToArray();
        __instance.searchDropDownRt.gameObject.SetActive(names.Length > 0);
        __instance.activeSearchEntryCount = 0;
        foreach (var name in names)
        {
            var index = __instance.activeSearchEntryCount++;
            if (__instance.searchEntries.Count <= index) __instance.searchEntries.Add(null);
            var entry = __instance.searchEntries[index];
            if (entry == null)
            {
                entry = Object.Instantiate(__instance.searchEntryPrefab, __instance.searchEntryPrefab.transform.parent);
                entry._Create(); entry.rt.anchoredPosition = new Vector2(0, -index * __instance.searchEntryPrefab.rt.rect.height);
                __instance.searchEntries[index] = entry;
            }
            entry._Init(null); entry._Open(); entry.realName = name; entry.SetName(str);
        }
        for (var i = names.Length; i < __instance.searchEntries.Count; i++) __instance.searchEntries[i]._Close();
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(UIStationRoutePanel.RefreshSearchEntries))]
    public static bool RefreshSearchEntries_Prefix(UIStationRoutePanel __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        var directory = Multiplayer.Session.FacilityDirectory;
        if (__instance.active) directory.Subscribe(__instance);
        var str = __instance.searchNameInputField.text;
        if (string.IsNullOrEmpty(str)) return true;
        __instance.addGids.Clear();
        __instance.addGids.AddRange(directory.Search(str, __instance.station.planetId, true).Select(e => e.GId));
        __instance.searchNameInputField.textComponent.material = null;
        __instance.searchNameInputField.textComponent.material = __instance.textMat;
        __instance.RefreshAddEntry();
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(UIStationRoutePanel.OnSearchNameInputEndEdit))]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter",
        Justification = "Retain the public patch signature and the native argument name used by Harmony.")]
    public static bool OnSearchNameInputEndEdit_Prefix(UIStationRoutePanel __instance, string str)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        __instance.SetSearchStations();
        __instance.activeSearchEntryCount = 0;
        __instance.searchDropDownRt.gameObject.SetActive(false);
        return false;
    }
}
