#region

using ABN;
using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(GameAbnormalityData_0925))]
internal class GameAbnormalityData_0925_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GameAbnormalityData_0925.TriggerAbnormality))]
    public static bool TriggerAbnormality_Prefix()
    {
        return !Multiplayer.IsActive || Multiplayer.Session.IsServer;
    }
}
