#region

using HarmonyLib;
using System.Collections.Generic;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Trash;
using NebulaeWorld;
using NebulaeWorld.GameStates;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UILootFilter))]
internal class UILootFilter_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UILootFilter.SetPickFilter))]
    public static void SetPickFilter_Postfix(Dictionary<int, int> ___pickFilters)
    {
        // The window normally applies on close, after the session may already have been disposed.
        // Persist edits immediately as well, so disconnecting with the window open retains them.
        LocalPlayerPreferences.SavePickFilters(___pickFilters);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UILootFilter.OnPickAllButtonClick))]
    public static void OnPickAllButtonClick_Postfix(Dictionary<int, int> ___pickFilters) =>
        LocalPlayerPreferences.SavePickFilters(___pickFilters);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UILootFilter.ApplyFilterSettings))]
    public static void ApplyFilterSettings_Postfix()
    {
        if (!Multiplayer.IsActive) return;

        LocalPlayerPreferences.SavePickFilters(GameMain.mainPlayer?.controller?.actionPick?.pickFilters);

        using var writer = new BinaryUtils.Writer();
        writer.BinaryWriter.Write(GameMain.data.trashSystem.enemyDropBans.Count);
        foreach (var itemId in GameMain.data.trashSystem.enemyDropBans)
        {
            writer.BinaryWriter.Write(itemId);
        }
        Multiplayer.Session.Network.SendPacket(new TrashSystemLootFilterPacket(writer.CloseAndGetBytes()));
    }
}
