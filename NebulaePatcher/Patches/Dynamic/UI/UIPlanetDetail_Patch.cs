using NebulaeModel.Networking;

#region
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Dynamic.UI;

[HarmonyPatch(typeof(UIPlanetDetail))]
internal class UIPlanetDetail_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIPlanetDetail.OnNameInputEndEdit))]
    public static void OnNameInputEndEdit_Postfix(UIPlanetDetail __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.Factories.IsIncomingRequest.Value)
        {
            return;
        }
        if (__instance.planet != null)
        {
            // Send packet with new planet name
            Multiplayer.Session.Network.SendPacket(new NameInputPacket(__instance.planet.overrideName,
                SessionSentinels.STAR_NONE, __instance.planet.id));
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIPlanetDetail._OnUpdate))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static bool _OnUpdate_Prefix(UIPlanetDetail __instance)
    {
        if (UIRoot.instance.galaxySelect == null || UIRoot.instance.galaxySelect.starmap == null ||
            Multiplayer.Session != null && !Multiplayer.Session.IsInLobby || Multiplayer.Session == null)
        {
            return true;
        }

        if (Time.frameCount % 30 == 0 && __instance.tabIndex == 0)
        {
            __instance.RefreshDynamicProperties();
            __instance.OnTabButtonClick(0); // baseInfoGroupGo.SetActive(true)
        }
        // SetActive and Graphic.color are native/uGUI writes that dirty the canvas; both
        // values are constant while the panel stays open, so skip the redundant writes.
        if (!__instance.trslBg.activeSelf) __instance.trslBg.SetActive(true);
        if (!__instance.imgBg.activeSelf) __instance.imgBg.SetActive(true);
        var comboColor = (__instance.uiGame.veinAmountDisplayFilter > 0)
            ? __instance.displayComboFilterColor
            : __instance.displayComboNormalColor;
        if (__instance.displayComboColorCard.color != comboColor)
            __instance.displayComboColorCard.color = comboColor;
        __instance.RefreshTabPanel();
        return false;
    }

    // Lobby previews may reveal resources, but must never change shared research state.
    // Replacing only these reads also handles nested refreshes and exceptions without cleanup.
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(UIPlanetDetail.OnPlanetDataSet))]
    [HarmonyPatch(nameof(UIPlanetDetail.RefreshDynamicProperties))]
    public static IEnumerable<CodeInstruction> ObserveLevel_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var field = AccessTools.Field(typeof(GameHistoryData), nameof(GameHistoryData.universeObserveLevel));
        var reader = AccessTools.Method(typeof(UIPlanetDetail_Patch), nameof(ObserveLevelForDetail));
        var code = new List<CodeInstruction>(instructions);
        var changed = 0;
        foreach (var instruction in code)
        {
            if (!instruction.LoadsField(field)) continue;
            instruction.opcode = OpCodes.Call;
            instruction.operand = reader;
            changed++;
        }
        if (changed != 2) throw new InvalidOperationException("Native planet detail observation checks changed.");
        return code;
    }

    internal static int ObserveLevelForDetail(GameHistoryData history) =>
        Multiplayer.IsActive && Multiplayer.Session.IsInLobby ? 4 : history.universeObserveLevel;

    /// <summary>
    /// Show the memo button for planets that have memos in the global pool,
    /// even if the factory isn't loaded (unvisited planets).
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIPlanetDetail.RefreshTabPanel))]
    public static void RefreshTabPanel_Postfix(UIPlanetDetail __instance)
    {
        if (!Multiplayer.IsActive) return;
        if (__instance.planet == null) return;

        // If memoBtn is already active, nothing to do
        if (__instance.memoBtn.gameObject.activeSelf) return;

        // Check if there's a memo in the global todos pool for this planet
        var todos = GameMain.data?.galacticDigital?.todos;
        if (todos == null)
        {
            return;
        }

        var planetId = __instance.planet.id;
        for (int i = 1; i < todos.cursor; i++)
        {
            ref var todo = ref todos.buffer[i];
            if (todo.id == i && todo.ownerId == planetId &&
                todo.ownerType == ETodoModuleOwnerType.Astro)
            {
                // Found a memo for this planet - show the memo button
                __instance.memoBtn.gameObject.SetActive(true);
                break;
            }
        }
    }
}
