using System;
using System.IO;
using System.Collections.Generic;
using NebulaeModel.Logger;
using NebulaeModel.Networking;
using NebulaeModel.Utils;

namespace NebulaeWorld.GameStates;

internal static class LocalPlayerPreferences
{
    private static string dashboardPath;
    private static byte[] dashboardBytes;
    private static string restoredDashboardPath;
    private static string PathFor(string kind) => Path.Combine(LegacyDataMigration.DataDirectory(GameConfig.propertyFolder),
        AtomicFile.IdentityFileName(MetadataManager.LocalIdentity), SaveManager.WorldId + "." + kind);

    private static IEnumerable<(string Path, byte[] Bytes)> ReadCandidates(string kind)
    {
        var path = PathFor(kind);
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            byte[] bytes = null;
            if (File.Exists(candidate))
                try { bytes = File.ReadAllBytes(candidate); }
                catch (IOException e) { Log.Warn($"Could not read local preferences path={candidate}: {e}"); }
            if (bytes != null) yield return (candidate, bytes);
        }
    }

    public static void SaveTutorials(int[] keys, int[] tutorials)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsGameLoaded || Multiplayer.IsDedicated) return;
        string path = null;
        try
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
            {
                writer.Write(1);
                writer.Write(keys.Length); foreach (var key in keys) writer.Write(key);
                writer.Write(tutorials.Length); foreach (var tutorial in tutorials) writer.Write(tutorial);
            }
            path = PathFor("tutorials");
            AtomicFile.Write(path, ms.ToArray());
        }
        catch (Exception e) { Log.Warn($"Could not save personal tutorial state path={path}: {e}"); }
    }

    public static void LoadTutorials(out int[] keys, out int[] tutorials)
    {
        keys = tutorials = Array.Empty<int>();
        foreach (var candidate in ReadCandidates("tutorials"))
        {
            try
            {
                using var reader = new BinaryReader(new MemoryStream(candidate.Bytes));
                if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported personal tutorial version");
                var k = ReadIds(reader); var t = ReadIds(reader);
                keys = k; tutorials = t;
                return;
            }
            catch (Exception e) { Log.Warn($"Could not restore personal tutorial state path={candidate.Path}: {e}"); }
        }
    }

    private static int[] ReadIds(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 10000) throw new InvalidDataException("Invalid preference count");
        var ids = new int[count];
        for (var i = 0; i < count; i++) ids[i] = reader.ReadInt32();
        return ids;
    }

    public static void SavePickFilters(IDictionary<int, int> filters)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsGameLoaded || Multiplayer.IsDedicated || filters == null) return;
        string path = null;
        try
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
            {
                writer.Write(1);
                writer.Write(filters.Count);
                foreach (var filter in filters)
                {
                    writer.Write(filter.Key);
                    writer.Write(filter.Value);
                }
            }
            // An empty dictionary is an explicit return to the default, manual pickup mode.
            path = PathFor("pick-filters");
            AtomicFile.Write(path, ms.ToArray());
        }
        catch (Exception e) { Log.Warn($"Could not save personal pick filters path={path}: {e}"); }
    }

    public static void RestorePickFilters()
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsGameLoaded || Multiplayer.IsDedicated) return;
        var action = GameMain.mainPlayer?.controller?.actionPick;
        var preferences = GameMain.data?.preferences;
        if (action?.pickFilters == null || preferences == null) return;

        foreach (var candidate in ReadCandidates("pick-filters"))
        {
            try
            {
                using var reader = new BinaryReader(new MemoryStream(candidate.Bytes));
                if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported personal pick filter version");
                var count = reader.ReadInt32();
                if (count < 0 || count > 10000 || reader.BaseStream.Length - reader.BaseStream.Position != (long)count * 8)
                    throw new InvalidDataException("Invalid personal pick filter count");
                var filters = new Dictionary<int, int>();
                for (var i = 0; i < count; i++)
                {
                    var item = reader.ReadInt32();
                    var mode = reader.ReadInt32();
                    if (item <= 0 || mode < 0 || mode > 1) throw new InvalidDataException("Invalid personal pick filter");
                    filters.Add(item, mode);
                }
                // Keep the native preferences in step so a later native Restore cannot undo this.
                action.pickFilters.Clear();
                preferences.pickFilters.Clear();
                foreach (var filter in filters)
                {
                    action.pickFilters.Add(filter.Key, filter.Value);
                    preferences.pickFilters.Add(filter.Key, filter.Value);
                }
                return;
            }
            catch (Exception e) { Log.Warn($"Could not restore personal pick filters path={candidate.Path}: {e}"); }
        }
    }

    public static bool HasDashboard => restoredDashboardPath == PathFor("dashboard");

    public static void SaveDashboard()
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsGameLoaded || Multiplayer.IsDedicated ||
            GameMain.data?.statistics?.charts == null) return;
        string path = null;
        try
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                GameMain.data.statistics.charts.Export(writer);
            var bytes = ms.ToArray(); path = PathFor("dashboard");
            if (path == dashboardPath && FieldDeltaCodec.Equal(bytes, dashboardBytes)) return;
            AtomicFile.Write(path, bytes);
            dashboardPath = path; dashboardBytes = bytes;
            restoredDashboardPath = path;
        }
        catch (Exception e) { Log.Warn($"Could not save dashboard preferences path={path}: {e}"); }
    }

    public static void RestoreDashboard()
    {
        if (GameMain.data?.statistics?.charts == null) return;
        foreach (var candidate in ReadCandidates("dashboard"))
        {
            try
            {
                using var reader = new BinaryReader(new MemoryStream(candidate.Bytes));
                GameMain.data.statistics.charts.Import(reader);
                dashboardPath = PathFor("dashboard"); dashboardBytes = candidate.Bytes;
                restoredDashboardPath = dashboardPath;
                return;
            }
            catch (Exception e) { Log.Warn($"Could not restore dashboard preferences path={candidate.Path}: {e}"); }
        }
    }
}
