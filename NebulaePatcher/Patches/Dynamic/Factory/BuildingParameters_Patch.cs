#region

using HarmonyLib;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic.Factory;

[HarmonyPatch(typeof(BuildingParameters))]
internal class BuildingParameters_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(BuildingParameters.ApplyPrebuildParametersToEntity))]
    [HarmonyPatch(nameof(BuildingParameters.PasteToFactoryObject))]
    public static void ApplyPrebuildParametersToEntity_Prefix()
    {
        if (Multiplayer.IsActive)
        {
            // Let the upper patch handle the SetBans, SetFilter syncing
            Multiplayer.Session.Storage.IsHumanInput = false;
        }
    }

    /// <summary>
    /// A battle base can outlive its storage component (destroyed, or restored from a save that
    /// captured the pair half-way). Vanilla then dereferences storagePool[storageId] without a
    /// null check, which throws every frame the death is retried and keeps the entity alive
    /// forever. Copying no parameters is the safe fallback: the prebuild is restored with
    /// defaults, and the death itself still commits.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(nameof(BuildingParameters.CopyFromFactoryObject))]
    public static bool CopyFromFactoryObject_Prefix(int objectId, PlanetFactory factory)
    {
        if (factory?.entityPool == null || objectId <= 0 || objectId >= factory.entityPool.Length) return true;
        var entity = factory.entityPool[objectId];
        if (entity.battleBaseId == 0) return true;
        var storage = factory.factoryStorage?.storagePool;
        if (storage != null && entity.storageId > 0 && entity.storageId < storage.Length && storage[entity.storageId] != null) return true;
        NebulaeModel.Logger.Log.Warn($"[factory] battle base object={objectId} planet={factory.planetId} is missing its storage component; skipping the building parameter copy.");
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(BuildingParameters.ApplyPrebuildParametersToEntity))]
    [HarmonyPatch(nameof(BuildingParameters.PasteToFactoryObject))]
    public static void ApplyPrebuildParametersToEntity_Postfix()
    {
        if (Multiplayer.IsActive)
        {
            Multiplayer.Session.Storage.IsHumanInput = true;
        }
    }
}