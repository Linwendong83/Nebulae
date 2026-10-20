#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(AbnormalityLogic))]
internal class AbnormalityLogic_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(AbnormalityLogic.GameTick))]
    public static bool GameTick_Prefix()
    {
        return !Multiplayer.IsActive || Multiplayer.Session.IsServer;
    }
}
