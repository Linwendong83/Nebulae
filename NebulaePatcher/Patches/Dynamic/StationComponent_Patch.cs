#region

using HarmonyLib;
using NebulaeWorld;
#pragma warning disable IDE1006

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(StationComponent))]
internal class StationComponent_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(StationComponent.InternalTickRemote))]
    public static bool InternalTickRemote_Prefix(StationComponent __instance, out bool __state)
    {
        __state = false;
        // Clients run the vanilla flight-only reverse patch once from GameLogic.
        if (!Multiplayer.IsActive) return true;
        if (!Multiplayer.Session.IsServer) return false;
        var ships = Multiplayer.Session.Ships;
        var pool = GameMain.data.galacticTransport.stationPool;
        ships.Lifecycle.ObserveBefore(__instance, pool);
        ships.RegisterStorage(__instance);
        for (var i = 0; i < __instance.workShipCount; i++)
        {
            var gid = __instance.workShipDatas[i].otherGId;
            if (gid > 0 && gid < pool.Length && pool[gid] != null) ships.RegisterStorage(pool[gid]);
        }
        NativeLogisticsContext.Enter();
        __state = true;
        return true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(StationComponent.InternalTickRemote))]
    public static void InternalTickRemote_Postfix(StationComponent __instance, bool __state)
    {
        if (__state) Multiplayer.Session.Ships.Lifecycle.ObserveAfter(__instance, GameMain.data.galacticTransport.stationPool);
    }

    [HarmonyFinalizer]
    [HarmonyPatch(nameof(StationComponent.InternalTickRemote))]
    public static void InternalTickRemote_Finalizer(bool __state)
    {
        if (__state) NativeLogisticsContext.Exit();
    }
    [HarmonyPrefix]
    [HarmonyPatch(nameof(StationComponent.RematchRemotePairs))]
    public static bool RematchRemotePairs_Prefix(StationComponent __instance)
    {
        if (!Multiplayer.IsActive) return true;
        if (!Multiplayer.Session.IsServer) return false;
        Multiplayer.Session.Ships.Lifecycle.ObserveBefore(__instance, GameMain.data.galacticTransport.stationPool);
        return true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(StationComponent.RematchRemotePairs))]
    public static void RematchRemotePairs_Postfix(StationComponent __instance)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsServer)
            Multiplayer.Session.Ships.Lifecycle.ObserveAfter(__instance, GameMain.data.galacticTransport.stationPool);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(StationComponent.IdleShipGetToWork))]
    public static void IdleShipGetToWork_Postfix(StationComponent __instance)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.LocalPlayer.IsHost)
        {
            return;
        }
        Multiplayer.Session.Ships.Lifecycle.Depart(__instance, in __instance.workShipDatas[__instance.workShipCount - 1]);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(StationComponent.WorkShipBackToIdle))]
    public static void WorkShipBackToIdle_Postfix(StationComponent __instance, int index)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.LocalPlayer.IsHost)
            Multiplayer.Session.Ships.Lifecycle.Return(__instance, index);
    }
    // as we unload PlanetFactory objects when leaving the star system we need to prevent the call on ILS entries in the gStationPool array
    [HarmonyPrefix]
    [HarmonyPatch(nameof(StationComponent.Free))]
    public static bool Free_Prefix(StationComponent __instance)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.Ships.PatchLockILS)
        {
            return true;
        }
        return !__instance.isStellar;
    }
}
