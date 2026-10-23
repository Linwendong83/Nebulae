using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NebulaePatcher.Patches.Dynamic;

/// <summary>
/// Star labels use an invisible Image as their container. Thousands of
/// overlapping quads make Canvas batching expensive even when every label is hidden.
/// The starmap screen already provides the same pointer-event surface.
/// </summary>
[HarmonyPatch(typeof(UIStarmapStar), "_OnCreate")]
internal static class StarmapContainerGraphic_Patch
{
    [HarmonyPostfix]
    internal static void Postfix(UIStarmapStar __instance)
    {
        var map = __instance.starmap;
        var image = __instance.GetComponent<Image>();
        if (map == null || map.screenImage == null || !map.screenImage.enabled ||
            !map.screenImage.raycastTarget || image == null || !image.enabled ||
            image.color.a != 0f || image.material != Graphic.defaultGraphicMaterial ||
            __instance.transform.parent != map.screenImage.transform ||
            image.GetComponent<Mask>() != null || image.GetComponent<RectMask2D>() != null)
            return;

        // A customized prefab may give this graphic an actual interaction. Keep it in that case.
        foreach (var component in image.GetComponents<Component>())
            if (component is IEventSystemHandler) return;

        // Keep the GameObject and ManualBehaviour active. Star projection, 3D picking,
        // labels, reminder buttons and marker children retain their normal lifecycle.
        image.enabled = false;
    }
}
