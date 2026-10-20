using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Players;
using NebulaeWorld;
using NebulaeWorld.Factory;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(ConstructionModuleComponent))]
internal class ConstructionModuleComponent_Patch
{
    [System.ThreadStatic]
    private static PlanetFactory currentFactory;

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
        Multiplayer.Session.Network.SendPacketToLocalStar(new PlayerEjectMechaDronePacket(playerId, planetId,
            targetObjectId, next1ObjectId, next2ObjectId, next3ObjectId, priority));
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

    [HarmonyPostfix]
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
