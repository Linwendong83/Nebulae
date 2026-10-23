using System;
using System.IO;

namespace NebulaeModel.Utils;

internal static class AtomicFile
{
    public static string IdentityFileName(string identity)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity))).Replace("-", "");
    }
    public static void Write(string path, byte[] bytes) => Write(path, bytes, bytes.Length);

    public static void Write(string path, byte[] bytes, int count)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        Directory.CreateDirectory(directory);
        // Do not append two GUIDs to a long profile path: Unity's Windows Mono still has MAX_PATH limits.
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, count);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
