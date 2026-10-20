using System.Collections.Generic;
using HarmonyLib;
using NebulaePatcher.MonoBehaviours;
using NebulaeWorld;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(ArmorWreckage))]
internal static class ArmorWreckage_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(ArmorWreckage.PrepareRespawn))]
    public static void PrepareRespawn_Prefix(ArmorWreckage __instance)
    {
        if (!Multiplayer.IsActive || __instance.mats == null || __instance.mats.Length == 0 ||
            __instance.GetComponent<RespawnWreckageMaterialOwner>() != null) return;

        // Vanilla writes _RimLight directly to mats. A wreckage must not change a
        // material still used by another player or by its source armor renderer.
        var copies = new Dictionary<Material, Material>();
        var isolated = new Material[__instance.mats.Length];
        for (var i = 0; i < isolated.Length; i++)
        {
            var original = __instance.mats[i];
            if (original == null) continue;
            if (!copies.TryGetValue(original, out var copy))
            {
                copy = Object.Instantiate(original);
                copies.Add(original, copy);
            }
            isolated[i] = copy;
        }

        var owner = __instance.gameObject.AddComponent<RespawnWreckageMaterialOwner>();
        owner.Initialize(new List<Material>(copies.Values).ToArray());
        var renderer = __instance.GetComponent<MeshRenderer>();
        if (renderer != null) renderer.sharedMaterials = isolated;
        __instance.mats = isolated;
    }
}
