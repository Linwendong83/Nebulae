using System;
using HarmonyLib;
using NebulaeModel.Packets.Authority;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(UITurretWindow))]
internal static class TurretAuthorityUI_Patch
{
    private static bool Client => Multiplayer.IsActive && Multiplayer.Session.IsClient && Multiplayer.Session.Authority.IsActive;
    private static void Request(UITurretWindow ui, TurretIntent intent)
    {
        if (ui.factory != null && ui.turretId > 0)
            Multiplayer.Session.Turrets.Request(ui.factory.planetId, ui.turretId, intent);
    }
    [HarmonyPrefix, HarmonyPatch(nameof(UITurretWindow.OnTurretShiftValueChange))]
    public static bool Phase_Prefix(UITurretWindow __instance, float phaseValue)
    {
        if (!Client) return true;
        if (!float.IsNaN(phaseValue) && !float.IsInfinity(phaseValue)) Request(__instance, new TurretIntent(TurretIntentKind.Phase, (int)(phaseValue * 6f + 0.5f)));
        return false;
    }
    [HarmonyPrefix, HarmonyPatch(nameof(UITurretWindow.GroupSelectionBtn_onClick))]
    public static bool Group_Prefix(UITurretWindow __instance, int obj)
    {
        if (!Client) return true;
        if (__instance.factory != null && __instance.turretId > 0)
        {
            var turret = __instance.defenseSystem.turrets.buffer[__instance.turretId];
            Request(__instance, new TurretIntent(TurretIntentKind.Group, turret.group == obj ? 0 : obj));
        }
        return false;
    }
    [HarmonyPrefix, HarmonyPatch(nameof(UITurretWindow.OnPrioritySelectButtonClicked))]
    public static bool Priority_Prefix(UITurretWindow __instance, int value)
    {
        if (!Client) return true;
        if (__instance.factory != null && __instance.turretId > 0 && value >= 0 && value <= 3)
        {
            var turret = __instance.defenseSystem.turrets.buffer[__instance.turretId];
            if (turret.type == ETurretType.Disturb && (value == 1 || value == 2)) return false;
            var shift = Math.Max(0, Math.Min(3, __instance.vsModeSelectIndex)) * 2;
            var settings = ((int)turret.vsSettings & ~(3 << shift)) | (value << shift);
            Request(__instance, new TurretIntent(TurretIntentKind.Priority, settings));
            __instance.selectBoxRt.gameObject.SetActive(false);
        }
        return false;
    }
    [HarmonyPrefix, HarmonyPatch(nameof(UITurretWindow.SuperNovaBtn_onClick))]
    public static bool Supernova_Prefix(UITurretWindow __instance)
    {
        if (!Client) return true;
        if (__instance.factory != null && __instance.turretId > 0)
        {
            var turret = __instance.defenseSystem.turrets.buffer[__instance.turretId];
            Request(__instance, new TurretIntent(TurretIntentKind.Supernova, UITurretWindow.burstModeIndex | (turret.inSupernova ? 0 : 4)));
        }
        return false;
    }
    [HarmonyPrefix, HarmonyPatch(nameof(UITurretWindow.OnHandFillAmmoButtonClick))]
    public static bool Fill_Prefix(UITurretWindow __instance, int obj)
    {
        if (!Client) return true;
        if (__instance.factory != null && __instance.turretId > 0 && __instance.player.inhandItemId == 0)
        {
            var turret = __instance.defenseSystem.turrets.buffer[__instance.turretId];
            var needs = ItemProto.turretNeeds[(uint)turret.ammoType];
            if (needs != null && obj >= 0 && obj < needs.Length)
                Multiplayer.Session.Turrets.Deposit(__instance.factory.planetId, __instance.turretId, needs[obj], Math.Max(0, 100 - turret.itemCount));
        }
        return false;
    }
    [HarmonyPrefix, HarmonyPatch(nameof(UITurretWindow.AmmoBtn_onClick))]
    public static bool Ammo_Prefix(UITurretWindow __instance)
    {
        if (!Client) return true;
        var player = __instance.player;
        if (__instance.factory == null || __instance.turretId <= 0 || player == null || Multiplayer.Session.Turrets.InventoryPending) return false;
        if (player.inhandItemId > 0 && player.inhandItemCount > 0)
        {
            // Move the requested hand stack into the checkpointed package before reserving it.
            var item = player.inhandItemId;
            var turret = __instance.defenseSystem.turrets.buffer[__instance.turretId];
            var needs = ItemProto.turretNeeds[(uint)turret.ammoType];
            if (needs == null || Array.IndexOf(needs, item) < 0 || turret.itemId != 0 && turret.itemId != item) return false;
            var amount = player.package.AddItemStacked(item, player.inhandItemCount, player.inhandItemInc, out var remainingInc);
            player.SetHandItems(item, player.inhandItemCount - amount, remainingInc);
            Multiplayer.Session.Turrets.Deposit(__instance.factory.planetId, __instance.turretId, item, Math.Min(amount, Math.Max(0, 100 - turret.itemCount)));
        }
        else Request(__instance, new TurretIntent(TurretIntentKind.Withdraw));
        return false;
    }
    [HarmonyPrefix, HarmonyPatch(nameof(UITurretWindow.ClearMagBtn_onClick))]
    public static bool Clear_Prefix(UITurretWindow __instance)
    {
        if (!Client) return true;
        Request(__instance, new TurretIntent(TurretIntentKind.Clear)); return false;
    }
}
