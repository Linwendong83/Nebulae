#region

using HarmonyLib;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIAstroTodoPanel))]
internal class UIAstroTodoPanel_Patch
{
    /// <summary>
    /// Allow viewing planet memos for unvisited planets by searching global todos pool.
    /// Game returns early if factory is null, but multiplayer syncs todos globally.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIAstroTodoPanel.SetData))]
    public static void SetData_Postfix(UIAstroTodoPanel __instance, int _astroId)
    {
        if (!Multiplayer.IsActive) return;

        // If todo is set AND matches this planet, factory was loaded correctly
        // Game's SetData doesn't clear todo when factory is null, so check ownerId
        if (__instance.todo != null && __instance.todo.ownerId == _astroId) return;

        // Not a planet (stars have astroId % 100 == 0)
        if (_astroId % 100 <= 0) return;

        // Factory not loaded - search global todos pool
        var todos = GameMain.data?.galacticDigital?.todos;
        if (todos == null) return;

        for (int i = 1; i < todos.cursor; i++)
        {
            ref var todo = ref todos.buffer[i];
            if (todo.id == i && todo.ownerId == _astroId &&
                todo.ownerType == ETodoModuleOwnerType.Astro)
            {
                __instance.todo = todo;
                __instance.delayLabelUpdate = true;
                break;
            }
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIAstroTodoPanel._OnRegEvent))]
    public static void OnRegEvent_Postfix(UIAstroTodoPanel __instance)
    {
        __instance.todoInputField.onEndEdit.AddListener(OnTodoInputFieldEndEdit);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIAstroTodoPanel._OnUnregEvent))]
    public static void OnUnregEvent_Postfix(UIAstroTodoPanel __instance)
    {
        __instance.todoInputField.onEndEdit.RemoveListener(OnTodoInputFieldEndEdit);
    }

    /// <summary>
    /// Sync memo content when user finishes editing.
    /// </summary>
    public static void OnTodoInputFieldEndEdit(string _)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.Warning.IsIncomingMarkerPacket) return;
        var __instance = UIRoot.instance.uiGame.planetDetail.uiTodoPanel;
        if (__instance.astroId <= 0 || __instance.todo == null) return;

        Multiplayer.Session.Network.SendPacket(
            new PlanetMemoUpdatePacket(
                __instance.astroId,
                __instance.todo.content,
                __instance.todo.hasReminder,
                __instance.todo.contentColorIndex));
    }

    /// <summary>
    /// Sync reminder checkbox toggle.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIAstroTodoPanel.OnCheckBoxButtonClick))]
    public static void OnCheckBoxButtonClick_Postfix(UIAstroTodoPanel __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.Warning.IsIncomingMarkerPacket) return;
        if (__instance.astroId <= 0 || __instance.todo == null) return;

        Multiplayer.Session.Network.SendPacket(
            new PlanetMemoUpdatePacket(
                __instance.astroId,
                __instance.todo.content,
                __instance.todo.hasReminder,
                __instance.todo.contentColorIndex));
    }
}
