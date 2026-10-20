#region

using HarmonyLib;
using NebulaePatcher.Patches.Transpilers;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(PowerSystem))]
internal class PowerSystem_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(PowerSystem.GameTick))]
    public static void PowerSystem_GameTick_Prefix(PowerSystem __instance, long time, bool multithreaded, ref bool isActive)
    {
        if (!Multiplayer.IsActive) return;

        if (PowerSystem_Transpiler.ChargerSyncEnabled)
        {
            Multiplayer.Session.PowerTowers.UpdateLocalState(__instance, multithreaded);
        }

        // Enable signType update on remote planet every 64 tick
        if ((time & 63) == 0 && Multiplayer.Session.IsServer)
        {
            isActive = true;
        }

        // Imported client factories can start ticking before their production statistics arrive.
        var factoryIndex = __instance.factory.index;
        if (GameMain.statistics.production.factoryStatPool[factoryIndex] == null)
        {
            GameMain.statistics.production.factoryStatPool[factoryIndex] = new FactoryProductionStat();
            GameMain.statistics.production.factoryStatPool[factoryIndex].Init();
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PowerSystem.RemoveNodeComponent))]
    public static bool RemoveNodeComponent(PowerSystem __instance, int id)
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }
        // as the destruct is synced across players this event is too
        // and as such we can safely remove power demand for every player        
        Multiplayer.Session.PowerTowers.RemoveNode(__instance.factory.planetId, id);

        return true;
    }
}
