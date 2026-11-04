#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic.UI;

[HarmonyPatch(typeof(UIMarkerDetailNode))]
internal class UIMarkerDetailNode_Patch
{
    /// <summary>
    ///     The marker detail list has the same checkbox handling as the tooltip; both write the
    ///     todo content without going through the marker window.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMarkerDetailNode.OnTodoTextTriggerClick))]
    public static void OnTodoTextTriggerClick_Postfix(UIMarkerDetailNode __instance)
    {
        if (!Multiplayer.IsActive) return;
        MarkerPacketSync.SendTodo(MarkerPacketSync.FindByGid(__instance.markerGId));
    }
}
