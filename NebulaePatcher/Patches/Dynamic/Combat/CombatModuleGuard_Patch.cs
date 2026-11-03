#region

using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeWorld;
using NebulaeWorld.Factory;

#endregion

namespace NebulaePatcher.Patches.Dynamic.Combat;

/// <summary>Keeps a half-removed battle base out of the native combat and death paths.</summary>
/// <remarks>
/// The native module tick dereferences its battle base and that base's storage without a null
/// check, and the native death path dereferences the components the interrupted death already
/// freed. Both then throw every tick and keep the whole planet's combat task erroring, which is
/// what a peer sees as a resync loop. The repair finishes the death (or rebuilds the storage) at
/// load; these prefixes cover any corruption that appears afterwards.
/// </remarks>
[HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.GameTick))]
internal static class CombatModuleGuard_Patch
{
    [HarmonyPrefix]
    private static bool Prefix(CombatModuleComponent __instance, PlanetFactory factory)
    {
        if (!Multiplayer.IsActive || __instance == null || __instance.entityId <= 0) return true;
        if (FactoryRepair.CanTick(__instance, factory)) return true;
        Diagnostics.Warn($"combat:module:{factory?.planetId}:{__instance.entityId}", () => $"[combat] skipping a module whose battle base or storage is gone planet={factory?.planetId} entity={__instance.entityId} base={__instance.battleBaseId}");
        return false;
    }
}

[HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.KillEntityFinally))]
internal static class CorruptBattleBaseDeath_Patch
{
    [HarmonyPrefix]
    private static bool Prefix(PlanetFactory __instance, int objId)
    {
        if (!Multiplayer.IsActive || __instance == null || objId <= 0) return true;
        if (!FactoryRepair.IsCorruptBattleBase(__instance, objId)) return true;
        return !FactoryRepair.RemoveCorruptEntity(__instance, objId, "death");
    }
}
