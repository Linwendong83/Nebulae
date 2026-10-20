using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(PlayerAction_Death))]
internal class PlayerAction_Death_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlayerAction_Death.Respawn))]
    public static bool Respawn_Prefix(PlayerAction_Death __instance, int _respawnMode)
    {
        if (!Multiplayer.IsActive) return true;
        if (__instance.player != GameMain.mainPlayer) return false;
        if (Multiplayer.Session.PropertyTransactions.ApplyingPersonal) return true;
        if (__instance.player.isAlive || __instance.respawning) return false;
        var option = __instance.selectedRespawnOption;
        if (option >= PlayerAction_Death.kRespawnCostsCount) option -= PlayerAction_Death.kRespawnCostsCount;
        if (GameMain.data.gameDesc.isSandboxMode && option < 0) option = 0;
        Multiplayer.Session.PropertyTransactions.Request(MetadataOperation.Respawn, _respawnMode, 0, option);
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlayerAction_Death.Respawn))]
    public static void Respawn_Postfix(PlayerAction_Death __instance)
    {
        if (Multiplayer.IsActive && __instance.player == GameMain.mainPlayer && __instance.respawning)
            Multiplayer.Session.Life.Publish();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlayerAction_Death.SettleRespawnCost))]
    public static bool SettleRespawnCost_Prefix()
    {
        // A multiplayer respawn starts only after its durable metadata payment commits.
        return !Multiplayer.IsActive;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlayerAction_Death.RespawnFinally))]
    public static void RespawnFinally_Postfix(PlayerAction_Death __instance)
    {
        if (Multiplayer.IsActive && __instance.player == GameMain.mainPlayer && __instance.player.isAlive)
            Multiplayer.Session.Life.Publish();
    }
}

[HarmonyPatch(typeof(UIDeathPanel))]
internal class UIDeathPanel_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIDeathPanel.OnLoadGameButtonClick))]
    [HarmonyPatch(nameof(UIDeathPanel.OnSaveGameButtonClick))]
    [HarmonyPatch(nameof(UIDeathPanel.OnSandboxButtonClick))]
    public static bool WorldManagement_Prefix() => !Multiplayer.IsActive || Multiplayer.Session.IsServer;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIDeathPanel._OnUpdate))]
    public static void Update_Postfix(UIDeathPanel __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return;
        foreach (var button in new[] { __instance.loadGameButton, __instance.saveGameButton, __instance.sandboxButton })
        {
            if (button?.button == null) continue;
            button.button.interactable = false;
            button.tips.tipText = "Only the host can manage the world save or sandbox mode".Translate();
        }
    }
}
