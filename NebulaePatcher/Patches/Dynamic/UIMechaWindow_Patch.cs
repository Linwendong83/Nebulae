#region

using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;
using System.Runtime.CompilerServices;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIMechaWindow))]
internal class UIMechaWindow_Patch
{
    private sealed class WeaponProgress
    {
        public Mecha Mecha;
        public int DeathCount, AmmoItem, AmmoSlot, AmmoInc;
        public long LaserCapacity;
        public float Ammo, Laser;
    }

    private static readonly ConditionalWeakTable<UIMechaWindow, WeaponProgress> weaponProgress = new();

    [HarmonyPrefix]
    [HarmonyPatch("_OnOpen")]
    private static void ResetWeaponProgress(UIMechaWindow __instance) => weaponProgress.Remove(__instance);

    [HarmonyPostfix]
    [HarmonyPatch("RefreshWindowComplete")]
    private static void SmoothWeaponProgress(UIMechaWindow __instance)
    {
        var mecha = __instance.mecha;
        if (!WorldSimulation.IsClient || mecha == null)
        {
            weaponProgress.Remove(__instance);
            return;
        }

        var progress = weaponProgress.GetValue(__instance, _ => new WeaponProgress());
        var deathCount = mecha.player?.deathCount ?? 0;
        var reset = !ReferenceEquals(progress.Mecha, mecha) || progress.DeathCount != deathCount;
        var ammoItem = PlayerWeaponResources.DisplayAmmoItem(mecha);
        var ammoInc = PlayerWeaponResources.DisplayAmmoInc(mecha);
        // Native refresh has already read the same predicted magazine and laser display as the
        // combat HUD. Ease only the images; weapon budgets and settings keep their exact values.
        progress.Ammo = reset || progress.AmmoItem != ammoItem || progress.AmmoSlot != mecha.ammoSelectSlot || progress.AmmoInc != ammoInc
            ? __instance.ammoProgress.fillAmount
            : SmoothProgress(progress.Ammo, __instance.ammoProgress.fillAmount, Time.unscaledDeltaTime);
        progress.Laser = reset || progress.LaserCapacity != mecha.laserEnergyCapacity
            ? __instance.laserProgress.fillAmount
            : SmoothProgress(progress.Laser, __instance.laserProgress.fillAmount, Time.unscaledDeltaTime);
        progress.Mecha = mecha;
        progress.DeathCount = deathCount;
        progress.AmmoItem = ammoItem;
        progress.AmmoSlot = mecha.ammoSelectSlot;
        progress.AmmoInc = ammoInc;
        progress.LaserCapacity = mecha.laserEnergyCapacity;
        __instance.ammoProgress.fillAmount = progress.Ammo;
        __instance.laserProgress.fillAmount = progress.Laser;
    }

    private static float SmoothProgress(float current, float target, float deltaTime)
    {
        // About 95% of a correction settles in 0.125 s, independent of render frame rate.
        var value = Mathf.Lerp(current, target, 1f - Mathf.Exp(-24f * deltaTime));
        return Mathf.Abs(value - target) < 0.0001f ? target : value;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIMechaWindow.OnReplaceFuelButtonClick))]
    public static bool OnReplaceFuelButtonClick_Prefix()
    {
        return AuthorityRuleGuard.AllowHostRule();
    }
}
