using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeWorld;
using NebulaeWorld.Factory;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(ConstructionModuleComponent))]
internal class ConstructionModuleComponent_Patch
{
    [System.ThreadStatic]
    private static PlanetFactory currentFactory;

    [HarmonyPrefix, HarmonyPatch(nameof(ConstructionModuleComponent.RecycleAllDrones))]
    public static bool RecycleAllDrones_Prefix(ConstructionModuleComponent __instance) =>
        !Multiplayer.IsActive || __instance.entityId != 0 ||
        ReferenceEquals(__instance, GameMain.mainPlayer?.mecha.constructionModule);

    [HarmonyPrefix, HarmonyPatch(nameof(ConstructionModuleComponent.GameTick))]
    public static bool GameTick_Prefix(ConstructionModuleComponent __instance, PlanetFactory factory)
    {
        if (!Multiplayer.IsActive) return true;
        ConstructionRepairTargets.Prune(__instance, factory);
        var entity = __instance.entityId;
        if (entity == 0) return true;
        if (!Multiplayer.Session.IsServer) return false;
        if (entity < 0 || entity >= factory.entityCursor || factory.entityPool[entity].id != entity) return false;
        var baseId = factory.entityPool[entity].battleBaseId;
        return baseId > 0 && baseId < factory.defenseSystem.battleBases.cursor &&
               factory.defenseSystem.battleBases.buffer[baseId]?.id == baseId;
    }

    [HarmonyPrefix, HarmonyPatch("PreLaunchDrone")]
    [HarmonyPatch(nameof(ConstructionModuleComponent.SortRepairTargets))]
    [HarmonyPatch(nameof(ConstructionModuleComponent.SearchRepairTargets))]
    public static void RepairTargets_Prefix(ConstructionModuleComponent __instance, PlanetFactory factory)
    {
        if (Multiplayer.IsActive) ConstructionRepairTargets.Prune(__instance, factory);
    }

    [HarmonyPostfix, HarmonyPatch("PreLaunchDrone")]
    public static void PreLaunchDrone_Postfix(ConstructionModuleComponent __instance, PlanetFactory factory)
    {
        if (!Multiplayer.IsActive) return;
        Multiplayer.Session.BuildDispatch.OfferLocalFlight(factory, __instance);
        Multiplayer.Session.Repairs?.Offer(__instance, factory);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(ConstructionModuleComponent.EjectMechaDrone))]
    public static void EjectMechaDrone_Postfix(PlanetFactory factory, Player player, int targetObjectId,
        int next1ObjectId, int next2ObjectId, int next3ObjectId)
    {
        if (!Multiplayer.IsActive) return;
        var playerId = Multiplayer.Session.LocalPlayer.Id;
        var planetId = factory.planetId;
        var priority = player.mecha.constructionModule.dronePriority;
        if (targetObjectId < 0)
        {
            if (Multiplayer.Session.BuildDispatch.TryCreateMechaLaunch(factory, targetObjectId,
                    next1ObjectId, next2ObjectId, next3ObjectId, priority, out var launch))
                Multiplayer.Session.BuildDispatch.LocalMechaLaunched(launch);
            else
                Log.Warn($"Construction drone launched without a current claim: planet {planetId}, target {targetObjectId}");
            return;
        }
        Multiplayer.Session.Repairs?.NativeLaunched(factory, targetObjectId);
    }

    [HarmonyPostfix, HarmonyPatch(nameof(ConstructionModuleComponent.RecycleDrone))]
    public static void Recycled(PlanetFactory factory, ref DroneComponent drone)
    {
        if (Multiplayer.IsActive && drone.owner == 0) Multiplayer.Session.Repairs?.Recycled(factory, drone.id);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(ConstructionModuleComponent.EjectBaseDrone))]
    public static void EjectBaseDrone_Postfix(ConstructionModuleComponent __instance, PlanetFactory factory,
        int targetObjectId, int next1ObjectId, int next2ObjectId, int next3ObjectId)
    {
        if (!Multiplayer.IsActive || targetObjectId >= 0) return;
        Multiplayer.Session.BuildDispatch.RecordBaseLaunch(factory, __instance.entityId, targetObjectId,
            next1ObjectId, next2ObjectId, next3ObjectId);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionModuleComponent.SearchBuildTargets))]
    public static void SearchBuildTargets_Prefix(PlanetFactory factory, out PlanetFactory __state)
    {
        __state = currentFactory;
        currentFactory = factory;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(nameof(ConstructionModuleComponent.SearchBuildTargets))]
    public static void SearchBuildTargets_Postfix(PlanetFactory __state) => currentFactory = __state;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionModuleComponent.InsertTmpBuildTarget))]
    public static bool InsertTmpBuildTarget_Prefix(ConstructionModuleComponent __instance, int objectId)
    {
        return !Multiplayer.IsActive ||
               Multiplayer.Session.BuildDispatch.CanQueue(currentFactory, __instance, objectId);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ConstructionModuleComponent.InsertBuildTarget))]
    public static bool InsertBuildTarget_Prefix(ConstructionModuleComponent __instance, PrebuildData[] prebuildPool,
        int objectId)
    {
        if (!Multiplayer.IsActive) return true;
        var factory = currentFactory ?? BuildDispatchManager.FindFactory(prebuildPool);
        return Multiplayer.Session.BuildDispatch.CanQueue(factory, __instance, objectId);
    }
}
