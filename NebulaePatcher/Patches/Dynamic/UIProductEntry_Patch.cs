#region

using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Statistics;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIProductEntry))]
internal class UIProductEntry_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIProductEntry.OnRefreshStorageCountClick))]
    public static void OnRefreshClick_Prefix()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return;
        }

        // Stock totals still require the host's authoritative values.
        var astroFilter = StatisticsAstroFilter.Resolve(
            UIRoot.instance.uiGame.statWindow.astroFilter,
            GameMain.localPlanet?.astroId ?? 0,
            GameMain.localStar?.astroId ?? 0);
        Multiplayer.Session.Network.SendPacket(new StatisticsRequestEvent(StatisticEvent.AstroFilterChanged, astroFilter));
    }

    [HarmonyPrefix, HarmonyPatch(nameof(UIProductEntry.OnRefreshRefSpeedClick))]
    public static void RefreshRate_Prefix()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return;
        var filter = StatisticsAstroFilter.Resolve(UIRoot.instance.uiGame.statWindow.astroFilter,
            GameMain.localPlanet?.astroId ?? 0, GameMain.localStar?.astroId ?? 0);
        if (!Multiplayer.Session.Statistics.LocalReferences.CanCalculate(filter)) OnRefreshClick_Prefix();
    }
}
