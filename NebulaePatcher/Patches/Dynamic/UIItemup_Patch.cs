using HarmonyLib;
using NebulaeWorld.MonoBehaviours.Local.Chat;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIItemup))]
internal class UIItemup_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIItemup.Up))]
    public static void Up_Postfix()
    {
        // Up() moves the pickup tips in front of every window. The chat overlay shares that
        // parent, so put it back above the tips after every manufactured item notification.
        ChatManager.Overlay?.SetAsLastSibling();
    }
}
