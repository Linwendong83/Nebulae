using System;
using System.IO;
using NebulaeModel.DataStructures;
using NebulaeModel.Logger;
using NebulaeModel.Utils;
using NebulaeWorld.GameStates;

namespace NebulaeWorld.Statistics;

internal static class StatisticsPreferences
{
    private static string FilePath() => Path.Combine(LegacyDataMigration.DataDirectory(GameConfig.propertyFolder),
        AtomicFile.IdentityFileName(MetadataManager.LocalIdentity), SaveManager.WorldId + ".statistics");

    public static void Save(UIStatisticsWindow window)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsGameLoaded || Multiplayer.IsDedicated ||
            window == null || GameMain.statistics?.production?.favoriteIds == null) return;
        string path = null;
        try
        {
            path = FilePath();
            AtomicFile.Write(path, new StatisticsFavorites
            {
                ProductionMask = window.favoriteMask,
                KillMask = window.killFavoriteMask,
                Production = GameMain.statistics.production.favoriteIds,
                Kills = GameMain.statistics.kill.favoriteIds
            }.Encode());
        }
        catch (Exception e) { Log.Warn($"Could not save statistics favorites path={path}: {e}"); }
    }

    public static void Restore()
    {
        if (Multiplayer.IsDedicated) return;
        var path = FilePath();
        if (!File.Exists(path)) return;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (!StatisticsFavorites.TryDecode(bytes, out var favorites))
            {
                var backup = path + ".bak";
                if (!File.Exists(backup) || !StatisticsFavorites.TryDecode(File.ReadAllBytes(backup), out favorites)) return;
            }
            var production = GameMain.statistics.production.favoriteIds;
            var kills = GameMain.statistics.kill.favoriteIds;
            Array.Clear(production, 0, production.Length);
            Array.Clear(kills, 0, kills.Length);
            Array.Copy(favorites.Production, production, Math.Min(production.Length, favorites.Production.Length));
            Array.Copy(favorites.Kills, kills, Math.Min(kills.Length, favorites.Kills.Length));
            var window = UIRoot.instance.uiGame.statWindow;
            window.favoriteMask = favorites.ProductionMask;
            window.killFavoriteMask = favorites.KillMask;
        }
        catch (Exception e) { Log.Warn($"Could not restore statistics favorites path={path}: {e}"); }
    }
}
