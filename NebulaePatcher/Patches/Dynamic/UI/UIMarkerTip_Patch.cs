#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic.UI;

[HarmonyPatch(typeof(UIMarkerTip))]
internal class UIMarkerTip_Patch
{
    /// <summary>
    ///     The marker tooltip renders its memo checkboxes itself; clicking one rewrites the todo
    ///     content on the local marker only.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerTip.OnTodoTextTriggerClick))]
    public static void OnTodoTextTriggerClick_Postfix(UIMarkerTip __instance)
    {
        if (!Multiplayer.IsActive) return;
        MarkerPacketSync.SendTodo(MarkerPacketSync.FindByGid(__instance.markerGId));
    }
}
