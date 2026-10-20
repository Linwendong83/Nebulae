using NebulaeModel.Networking;
using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(Player), nameof(Player.vegetableCollection), MethodType.Getter)]
internal static class PlayerVegetation_Patch
{
    [HarmonyPrefix]
    public static bool GetCollection(Player __instance, ref VegetableCollection __result)
    {
        if (!Multiplayer.IsActive || __instance != GameMain.mainPlayer ||
            Multiplayer.Session?.Vegetation == null) return true;

        var manager = Multiplayer.Session.Vegetation;
        if (Multiplayer.Session.IsServer)
        {
            var author = Multiplayer.Session.Factories.PacketAuthor;
            if (author != SessionSentinels.AUTHOR_NONE && author > 0 && author <= ushort.MaxValue &&
                author != Multiplayer.Session.LocalPlayer.Id)
            {
                var remote = manager.GetRemote((ushort)author);
                if (remote != null)
                {
                    __result = remote;
                    return false;
                }
            }
        }
        else if (manager.ReplayCollection != null)
        {
            __result = manager.ReplayCollection;
            return false;
        }

        return true;
    }
}
