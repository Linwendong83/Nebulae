#region

using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIStarmap))]
internal class UIStarmap_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStarmap.SetHivePreviewInStar))]
    public static bool SetHivePreviewInStar_Prefix(UIStarmap __instance, StarData star, bool open)
    {
        if (!Multiplayer.IsActive) return true;
        if (star == null) return false;
        // A realized sibling must not stop later virtual hives from opening or closing.
        for (var hive = __instance.spaceSector.dfHives[star.index]; hive != null; hive = hive.nextSibling)
            if (!hive.realized)
            {
                if (open) hive.OpenPreview(); else hive.ClosePreview();
            }
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIStarmap._OnLateUpdate))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnLateUpdate_Postfix(UIStarmap __instance)
    {
        if (Multiplayer.IsActive)
        {
            Multiplayer.Session.World.RenderPlayerNameTagsOnStarmap(__instance);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIStarmap._OnClose))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnClose_Postfix()
    {
        if (Multiplayer.IsActive)
        {
            Multiplayer.Session.World.ClearPlayerNameTagsOnStarmap();
        }
    }

    static int s_queryingIndex = -1;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIStarmap), nameof(UIStarmap.OnCursorFunction2Click))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void QueryDysonSphere(UIStarmap __instance)
    {
        // Client: Query existing dyson sphere when clicking on 'View' star on starmap
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost || Multiplayer.Session.IsInLobby || __instance.focusStar == null)
        {
            return;
        }

        var starIndex = __instance.focusStar.star.index;
        if (GameMain.data.dysonSpheres[starIndex] == null)
        {
            if (s_queryingIndex != starIndex)
            {
                Multiplayer.Session.Network.SendPacket(new DysonSphereLoadRequest(starIndex, DysonSphereRequestEvent.Query));
                s_queryingIndex = starIndex;
            }
        }
    }
}
