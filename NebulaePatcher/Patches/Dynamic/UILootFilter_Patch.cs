#region

using HarmonyLib;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Trash;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UILootFilter))]
internal class UILootFilter_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UILootFilter.ApplyFilterSettings))]
    public static void ApplyFilterSettings_Postfix()
    {
        if (!Multiplayer.IsActive) return;

        using var writer = new BinaryUtils.Writer();
        writer.BinaryWriter.Write(GameMain.data.trashSystem.enemyDropBans.Count);
        foreach (var itemId in GameMain.data.trashSystem.enemyDropBans)
        {
            writer.BinaryWriter.Write(itemId);
        }
        Multiplayer.Session.Network.SendPacket(new TrashSystemLootFilterPacket(writer.CloseAndGetBytes()));
    }
}
