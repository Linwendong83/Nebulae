#region

using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeModel.Packets.Statistics;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIStatisticsWindow))]
internal class UIStatisticsWindow_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIStatisticsWindow.OnFavoriteButtonClick))]
    [HarmonyPatch(nameof(UIStatisticsWindow.OnKillFavoriteButtonClick))]
    public static void SaveFavorites_Postfix(UIStatisticsWindow __instance) =>
        NebulaeWorld.Statistics.StatisticsPreferences.Save(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStatisticsWindow._OnClose))]
    public static void SaveOnClose_Prefix(UIStatisticsWindow __instance) =>
        NebulaeWorld.Statistics.StatisticsPreferences.Save(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIStatisticsWindow._OnOpen))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnOpen_Postfix(UIStatisticsWindow __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return;
        }
        Multiplayer.Session.Statistics.IsStatisticsNeeded = true;
        Multiplayer.Session.Statistics.BeginStatisticsRequest();
        var astroFilter = NebulaeModel.DataStructures.StatisticsAstroFilter.Resolve(
            __instance.astroFilter, GameMain.localPlanet?.astroId ?? 0, GameMain.localStar?.astroId ?? 0);
        Multiplayer.Session.Network.SendPacket(new StatisticsRequestEvent(StatisticEvent.WindowOpened, astroFilter));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIStatisticsWindow._OnClose))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnClose_Postfix()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost ||
            !Multiplayer.Session.Statistics.IsStatisticsNeeded)
        {
            return;
        }
        Multiplayer.Session.Statistics.IsStatisticsNeeded = false;
        Multiplayer.Session.Network.SendPacket(new StatisticsRequestEvent(StatisticEvent.WindowClosed, 0));
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStatisticsWindow.AddProductStatGroup))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static bool AddProductStatGroup_Prefix(int _factoryIndex, ProductionStatistics ___productionStat)
    {
        //Skip when StatisticsDataPacket hasn't arrived yet
        return _factoryIndex >= 0 && _factoryIndex < ___productionStat.factoryStatPool.Length &&
               ___productionStat.factoryStatPool[_factoryIndex] != null;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIStatisticsWindow.AstroBoxToValue))]
    public static void AstroBoxToValue_Postfix(UIStatisticsWindow __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost) return;

        if (__instance.isStatisticsTab && __instance.lastAstroFilter != __instance.astroFilter)
        {
            var filter = NebulaeModel.DataStructures.StatisticsAstroFilter.Resolve(
                __instance.astroFilter, GameMain.localPlanet?.astroId ?? 0, GameMain.localStar?.astroId ?? 0);
            Multiplayer.Session.Network.SendPacket(new StatisticsRequestEvent(StatisticEvent.AstroFilterChanged, filter));
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIStatisticsWindow.RefreshProductionExtraInfo))]
    public static bool RefreshProductionExtraInfo_Prefix(UIStatisticsWindow __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost) return true;

        var filter = NebulaeModel.DataStructures.StatisticsAstroFilter.Resolve(
            __instance.astroFilter, GameMain.localPlanet?.astroId ?? 0, GameMain.localStar?.astroId ?? 0);
        if (__instance.isProductionTab) Multiplayer.Session.Statistics.LocalReferences.RefreshRates(filter);
        return false;
    }
}

[HarmonyPatch(typeof(UIProductEntry), nameof(UIProductEntry.OnFavoriteButtonClick))]
internal class UIProductFavorite_Patch
{
    [HarmonyPostfix]
    public static void Postfix() =>
        NebulaeWorld.Statistics.StatisticsPreferences.Save(UIRoot.instance.uiGame.statWindow);
}

[HarmonyPatch(typeof(UIKillEntry), nameof(UIKillEntry.OnFavoriteButtonClick))]
internal class UIKillFavorite_Patch
{
    [HarmonyPostfix]
    public static void Postfix() =>
        NebulaeWorld.Statistics.StatisticsPreferences.Save(UIRoot.instance.uiGame.statWindow);
}
