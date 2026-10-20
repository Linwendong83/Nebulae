using System;
using System.IO;
using System.Collections.Generic;
using NebulaeModel.Logger;
using NebulaeModel.Utils;

namespace NebulaeWorld.GameStates;

internal static class LocalPlayerPreferences
{
    private static string dashboardPath;
    private static byte[] dashboardBytes;
    private static string restoredDashboardPath;
    private static string PathFor(string kind) => Path.Combine(LegacyDataMigration.DataDirectory(GameConfig.propertyFolder),
        AtomicFile.IdentityFileName(MetadataManager.LocalIdentity), SaveManager.WorldId + "." + kind);

    private static IEnumerable<byte[]> ReadCandidates(string kind)
    {
        var path = PathFor(kind);
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            byte[] bytes = null;
            if (File.Exists(candidate))
                try { bytes = File.ReadAllBytes(candidate); }
                catch (IOException e) { Log.Warn("Could not read local preferences: " + e.Message); }
            if (bytes != null) yield return bytes;
        }
    }

    public static void SaveTutorials(int[] keys, int[] tutorials)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsGameLoaded || Multiplayer.IsDedicated) return;
        try
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
            {
                writer.Write(1);
                writer.Write(keys.Length); foreach (var key in keys) writer.Write(key);
                writer.Write(tutorials.Length); foreach (var tutorial in tutorials) writer.Write(tutorial);
            }
            AtomicFile.Write(PathFor("tutorials"), ms.ToArray());
        }
        catch (Exception e) { Log.Warn("Could not save personal tutorial state: " + e.Message); }
    }

    public static void LoadTutorials(out int[] keys, out int[] tutorials)
    {
        keys = tutorials = Array.Empty<int>();
        foreach (var bytes in ReadCandidates("tutorials"))
        {
            try
            {
                using var reader = new BinaryReader(new MemoryStream(bytes));
                if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported personal tutorial version");
                var k = ReadIds(reader); var t = ReadIds(reader);
                keys = k; tutorials = t;
                return;
            }
            catch (Exception e) { Log.Warn("Could not restore personal tutorial state: " + e.Message); }
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

    public static bool HasDashboard => restoredDashboardPath == PathFor("dashboard");

    public static void SaveDashboard()
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsGameLoaded || Multiplayer.IsDedicated ||
            GameMain.data?.statistics?.charts == null) return;
        try
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                GameMain.data.statistics.charts.Export(writer);
            var bytes = ms.ToArray(); var path = PathFor("dashboard");
            if (path == dashboardPath && Equal(bytes, dashboardBytes)) return;
            AtomicFile.Write(path, bytes);
            dashboardPath = path; dashboardBytes = bytes;
            restoredDashboardPath = path;
        }
        catch (Exception e) { Log.Warn("Could not save dashboard preferences: " + e.Message); }
    }

    public static void RestoreDashboard()
    {
        if (GameMain.data?.statistics?.charts == null) return;
        foreach (var bytes in ReadCandidates("dashboard"))
        {
            try
            {
                using var reader = new BinaryReader(new MemoryStream(bytes));
                GameMain.data.statistics.charts.Import(reader);
                dashboardPath = PathFor("dashboard"); dashboardBytes = bytes;
                restoredDashboardPath = dashboardPath;
                return;
            }
            catch (Exception e) { Log.Warn("Could not restore dashboard preferences: " + e.Message); }
        }
    }

    internal static bool Equal(byte[] left, byte[] right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null || left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }
}
