#region

using System;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeModel.Packets.GameStates;
using NebulaeWorld;
using NebulaeWorld.GameStates;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(GameSave))]
internal class GameSave_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GameSave.SaveCurrentGame))]
    public static bool SaveCurrentGame_Prefix()
    {
        if (Multiplayer.SuppressClientWorldSaves) return false;
        if (!Multiplayer.IsActive || !Multiplayer.Session.LocalPlayer.IsHost)
        {
            return !Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost;
        }
        if (!SaveManager.CanSave) return false;
        // Only save if in single player or if you are the host
        return !Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(GameSave.SaveCurrentGame))]
    public static void SaveCurrentGame_Postfix(string saveName, bool __result)
    {
        if (Multiplayer.SuppressClientWorldSaves || !Multiplayer.IsActive || !Multiplayer.Session.LocalPlayer.IsHost)
        {
            return;
        }
        if (!__result) return;
        SaveManager.SaveServerData(saveName);
        // Update last save time in clients
        GameStatesManager.LastSaveTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Multiplayer.Session.Server.SendPacket(new GameStateSaveInfoPacket(GameStatesManager.LastSaveTime));
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(GameSave.AutoSave))]
    public static bool AutoSave_Prefix()
    {
        // Only save if in single player or if you are the host
        return !Multiplayer.SuppressClientWorldSaves && (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(GameSave.AutoSaveAfterErrored))]
    public static bool AutoSaveAfterErrored_Prefix() =>
        !Multiplayer.SuppressClientWorldSaves && (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(GameSave.SaveAsLastExit))]
    public static bool SaveAsLastExit_Prefix()
    {
        // Only save if in single player, since multiplayer requires to load from the Load Save Window
        return !Multiplayer.IsActive && !Multiplayer.IsLeavingGame;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(GameSave.LoadCurrentGame))]
    [SuppressMessage("Style", "IDE1006:Naming Styles")]
    public static void LoadCurrentGame_Postfix(bool __result)
    {
        // If loading success, check and correct offset for all inserters
        if (!__result)
        {
            return;
        }
        if (Multiplayer.IsActive) GoalManager.RestoreLevel(GameMain.data);
        for (var index = 0; index < GameMain.data.factoryCount; index++)
        {
            var factory = GameMain.data.factories[index];
            var entityPool = factory.entityPool;
            var traffic = factory.factorySystem.traffic;
            var beltPool = factory.factorySystem.traffic.beltPool;
            for (var i = 1; i < factory.factorySystem.inserterCursor; i++)
            {
                ref var inserter = ref factory.factorySystem.inserterPool[i];
                if (inserter.id == i)
                {
                    inserter.InternalOffsetCorrection(entityPool, traffic, beltPool);
                }
            }
        }
    }
}
