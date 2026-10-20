using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NebulaeModel.Utils;

/// <summary>Copies legacy data once; existing Nebulae data always wins.</summary>
internal static class LegacyDataMigration
{
    private static readonly object gate = new();

    public static string ConfigFile(string directory, string name, string legacyName, bool migrateSettingsSection = false)
    {
        lock (gate)
        {
            var target = Path.Combine(directory, name);
            var source = Path.Combine(directory, legacyName);
            if (File.Exists(target) || !File.Exists(source)) return target;
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                if (migrateSettingsSection)
                {
                    var contents = File.ReadAllText(source);
                    contents = Regex.Replace(contents, @"(?m)^(\s*)\[Nebula - Settings\]([\t ]*\r?)$",
                        "$1[Nebulae - Settings]$2");
                    File.WriteAllText(temporary, contents, new UTF8Encoding(false));
                }
                else File.Copy(source, temporary);
                try { File.Move(temporary, target); }
                catch (IOException) when (File.Exists(target)) { /* Another instance completed the migration. */ }
                return target;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    public static string DataDirectory(string directory)
    {
        lock (gate)
        {
            var target = Path.Combine(directory, "Nebulae");
            var source = Path.Combine(directory, "Nebula");
            if (Directory.Exists(target) || !Directory.Exists(source)) return target;
            var temporary = Path.Combine(directory, ".nebulae-migration-" + Guid.NewGuid().ToString("N"));
            try
            {
                CopyDirectory(source, temporary);
                try { Directory.Move(temporary, target); }
                catch (IOException) when (Directory.Exists(target)) { /* Keep the already published directory. */ }
                return target;
            }
            finally
            {
                if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        RejectLink(source);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            RejectLink(file);
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (var child in Directory.GetDirectories(source))
            CopyDirectory(child, Path.Combine(target, Path.GetFileName(child)));
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing linked legacy data: " + path);
    }
}
