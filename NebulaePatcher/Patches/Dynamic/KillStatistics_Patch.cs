using System;
using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(KillStatistics))]
internal static class KillStatistics_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(KillStatistics.RegisterStarKillStat))]
    public static bool Star_Prefix(int starId, int modelIndex)
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient) return false;
        Multiplayer.Session.Kills.Record(starId * 100, modelIndex);
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(KillStatistics.RegisterFactoryKillStat))]
    public static bool Factory_Prefix(int factoryIndex, int modelIndex)
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient) return false;
        if (factoryIndex < 0 || factoryIndex >= GameMain.data.factoryCount) return false;
        Multiplayer.Session.Kills.Record(GameMain.data.factories[factoryIndex].planetId, modelIndex);
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(KillStatistics.RegisterMechaKillStat))]
    public static bool Mecha_Prefix(int modelIndex) => !Multiplayer.IsActive || Multiplayer.Session.Kills.RecordPersonal(modelIndex);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(KillStatistics.GameTick))]
    [HarmonyPatch(nameof(KillStatistics.GameTick_Parallel))]
    [HarmonyPatch(nameof(KillStatistics.PrepareTick))]
    [HarmonyPatch(nameof(KillStatistics.PrepareTick_Parallel))]
    public static bool Tick_Prefix() => !Multiplayer.IsActive || Multiplayer.Session.IsServer;
}

[HarmonyPatch(typeof(CombatStat), nameof(CombatStat.HandleZeroHp))]
internal static class KillAttribution_Patch
{
    [HarmonyPrefix]
    public static void Prefix(ref CombatStat __instance, out IDisposable __state)
    {
        __state = Multiplayer.IsActive && Multiplayer.Session.IsServer ? Multiplayer.Session.Kills.EnterDeath(__instance) : null;
    }

    [HarmonyFinalizer]
    public static Exception Finalizer(Exception __exception, IDisposable __state)
    {
        __state?.Dispose();
        return __exception;
    }
}

[HarmonyPatch(typeof(UIStatisticsWindow), nameof(UIStatisticsWindow.AddAstroStatGroup))]
internal static class RemoteKillStatisticsUI_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(UIStatisticsWindow __instance, int _astroId)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        if (_astroId == 0)
        {
            // Vanilla's all-galaxy loop only enumerates loaded factories. Add remote planets here.
            foreach (var planetId in Multiplayer.Session.Kills.RemotePlanets) Add(__instance, planetId);
        }
        else Add(__instance, _astroId < 0 ? -1 : _astroId);
        return false;
    }

    private static void Add(UIStatisticsWindow window, int astro)
    {
        var stat = Multiplayer.Session.Kills.GetRemote(astro);
        if (stat == null) return;
        for (var i = 0; i < window.astroStatGroupCursor; i++)
            if (window.astroStatGroup[i].astroId == astro) return;
        if (window.astroStatGroupCursor >= window.astroStatGroup.Length) return;
        window.astroStatGroup[window.astroStatGroupCursor].astroId = astro;
        window.astroStatGroup[window.astroStatGroupCursor++].killPool = stat.killStatPool;
    }
}
