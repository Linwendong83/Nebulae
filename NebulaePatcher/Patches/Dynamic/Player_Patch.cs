using NebulaeModel.Networking;
#region

using HarmonyLib;
using NebulaeModel.Packets.Players;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(Player))]
internal class Player_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(Player.ExchangeSand))]
    public static bool ExchangeSand_Prefix(Player __instance)
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }

        var gainedSand = 0;
        for (var i = 0; i < __instance.package.size; i++)
        {
            if (__instance.package.grids[i].itemId == 1099) // 1099: enemy drop sand item
            {
                gainedSand += __instance.package.grids[i].count;
                __instance.package.grids[i].itemId = 0;
                __instance.package.grids[i].filter = 0;
                __instance.package.grids[i].count = 0;
                __instance.package.grids[i].inc = 0;
                __instance.package.grids[i].stackSize = 0;
            }
        }

        // Only call SetSandCount when there is sand change in client
        if (gainedSand > 0)
        {
            __instance.SetSandCount(__instance.sandCount + gainedSand, (ESandSource)0);
            // The host divides what a battle base yields between the players, so it needs this figure now
            // rather than at the next checkpoint.
            if (Multiplayer.Session.LocalPlayer.IsClient)
            {
                Multiplayer.Session.Network.SendPacket(new PlayerSandGained(gainedSand));
            }
        }
        return false;
    }


    [HarmonyPrefix]
    [HarmonyPatch(nameof(Player.SetSandCount))]
    public static bool SetSandCount_Prefix()
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }

        return Multiplayer.Session.Factories.PacketAuthor == Multiplayer.Session.LocalPlayer.Id ||
               Multiplayer.Session.LocalPlayer.IsHost &&
               Multiplayer.Session.Factories.PacketAuthor == SessionSentinels.AUTHOR_NONE ||
               !Multiplayer.Session.Factories.IsIncomingRequest.Value;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(Player.TryAddItemToPackage))]
    public static bool TryAddItemToPackage_Prefix(ref int __result)
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }

        // We should only add items to player if player requested
        if (!Multiplayer.Session.Factories.IsIncomingRequest.Value ||
            Multiplayer.Session.Factories.PacketAuthor == Multiplayer.Session.LocalPlayer.Id)
        {
            return true;
        }

        __result = 0;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(Player.UseHandItems))]
    public static bool UseHandItems_Prefix(ref int __result)
    {
        // Run normally if we are not in an MP session or StorageComponent is not player package
        if (!Multiplayer.IsActive)
        {
            return true;
        }

        // We should only take items to player if player requested
        if (!Multiplayer.Session.Factories.IsIncomingRequest.Value ||
            Multiplayer.Session.Factories.PacketAuthor == Multiplayer.Session.LocalPlayer.Id)
        {
            return true;
        }

        __result = 1;
        return false;
    }

    #region Combat

    [HarmonyPrefix]
    [HarmonyPatch(nameof(Player.Kill))]
    public static bool Kill_Prefix(Player __instance)
    {
        if (!Multiplayer.IsActive) return true;
        if (__instance != GameMain.mainPlayer)
        {
            if (Multiplayer.Session.IsServer && Multiplayer.Session.CombatAuthority.Owns(__instance) && __instance.isAlive)
            {
                __instance.isAlive = false;
                __instance.deathCount++;
                __instance.timeSinceKilled = 0;
                __instance.mecha.Kill();
            }
            return false;
        }
        if (Multiplayer.Session.IsClient && !Multiplayer.Session.AuthorityRuntime.ApplyContext.IsActiveOnCurrentThread) return false;

        return true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Player.Kill))]
    public static void Kill_Postfix(Player __instance)
    {
        if (!Multiplayer.IsActive || __instance != GameMain.mainPlayer || __instance.isAlive) return;
        NebulaeModel.DataStructures.PlayerLifeData.CurrentTransactionId = "";
        NebulaeModel.DataStructures.PlayerLifeData.CurrentRedeployItemsDropped = false;
        Multiplayer.Session.Life.Publish();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(Player.PrepareRedeploy))]
    public static bool PrepareRedeploy_Prefix(Player __instance)
    {
        if (!Multiplayer.IsActive) return true;

        if (__instance != GameMain.mainPlayer) return false;
        if (NebulaeModel.DataStructures.PlayerLifeData.CurrentRedeployItemsDropped) return false;
        Multiplayer.Session.PropertyTransactions.BeginDropOperation(NebulaeModel.DataStructures.PlayerLifeData.CurrentTransactionId);
        Multiplayer.Session.Life.Publish(); // Durable pre-drop image for interrupted redeploy recovery.
        return true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Player.PrepareRedeploy))]
    public static void PrepareRedeploy_Postfix(Player __instance)
    {
        if (!Multiplayer.IsActive || __instance != GameMain.mainPlayer) return;
        NebulaeModel.DataStructures.PlayerLifeData.CurrentRedeployItemsDropped = true;
        Multiplayer.Session.PropertyTransactions.EndDropOperation();
        Multiplayer.Session.Life.Publish();
    }

    [HarmonyFinalizer]
    [HarmonyPatch(nameof(Player.PrepareRedeploy))]
    public static System.Exception PrepareRedeploy_Finalizer(System.Exception __exception)
    {
        if (Multiplayer.IsActive) Multiplayer.Session.PropertyTransactions.EndDropOperation();
        return __exception;
    }

    #endregion
}
