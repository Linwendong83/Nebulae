using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(Player), nameof(Player.GameTick))]
internal static class PlayerExit_Patch
{
    // Once exit starts, production, fuel loading and actions must not change the personal
    // image while its final receipt is in flight. Network settlements continue at frame boundaries.
    [HarmonyPrefix]
    internal static bool Prefix(Player __instance) => !Multiplayer.IsActive || !Multiplayer.Session.IsClient ||
        Multiplayer.Session.Life?.IsExiting != true || __instance != GameMain.mainPlayer;
}
