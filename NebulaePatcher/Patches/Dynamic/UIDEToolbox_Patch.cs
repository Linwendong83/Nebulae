#region

using HarmonyLib;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIDEToolbox))]
internal class UIDEToolbox_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIDEToolbox.OnColorChange))]
    public static void OnColorChange_Postfix(UIDEToolbox __instance, Color32 color)
    {
        if (!Multiplayer.IsActive || __instance.editor.selection.singleSelectedLayer == null)
        {
            return;
        }
        var starIndex = __instance.editor.selection.viewStar.index;
        var layerId = __instance.editor.selection.singleSelectedLayer.id;
        color.a = byte.MaxValue;
        var rgba = PackRgba(color);
        foreach (var node in __instance.editor.selection.selectedNodes)
        {
            Multiplayer.Session.DysonSpheres.RegisterColorChange(starIndex, layerId,
                (byte)DysonSphereComponentType.Node, node.id, rgba);
        }
        foreach (var frame in __instance.editor.selection.selectedFrames)
        {
            Multiplayer.Session.DysonSpheres.RegisterColorChange(starIndex, layerId,
                (byte)DysonSphereComponentType.Frame, frame.id, rgba);
        }
        foreach (var shell in __instance.editor.selection.selectedShells)
        {
            Multiplayer.Session.DysonSpheres.RegisterColorChange(starIndex, layerId,
                (byte)DysonSphereComponentType.Shell, shell.id, rgba);
        }
    }

    private static uint PackRgba(Color32 color)
    {
        return (uint)(color.r | (color.g << 8) | (color.b << 16) | (color.a << 24));
    }
}
