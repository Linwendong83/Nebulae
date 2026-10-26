using System;
using System.IO;

namespace NebulaeModel.Utils;

/// <summary>Rotates each companion file to the same autosave slot as its server save.</summary>
internal static class ServerSaveRotation
{
    public static void Rotate(string folder, string temporaryName, string[] slots, string[] suffixes)
    {
        if (slots.Length == 0 || suffixes.Length == 0 ||
            !File.Exists(Path.Combine(folder, temporaryName + suffixes[0]))) return;
        foreach (var suffix in suffixes)
        {
            string from = null, to = null;
            try
            {
                var oldest = Path.Combine(folder, slots[slots.Length - 1] + suffix);
                from = oldest;
                if (File.Exists(oldest)) File.Delete(oldest);
                for (var i = slots.Length - 2; i >= 0; i--)
                {
                    var source = Path.Combine(folder, slots[i] + suffix);
                    from = source; to = Path.Combine(folder, slots[i + 1] + suffix);
                    if (File.Exists(source)) File.Move(source, Path.Combine(folder, slots[i + 1] + suffix));
                }
                var temporary = Path.Combine(folder, temporaryName + suffix);
                from = temporary; to = Path.Combine(folder, slots[0] + suffix);
                if (File.Exists(temporary)) File.Move(temporary, Path.Combine(folder, slots[0] + suffix));
            }
            catch (Exception error)
            {
                error.Data["Nebulae.FileStage"] = "autosave-rotation";
                error.Data["Nebulae.FileSource"] = from;
                error.Data["Nebulae.FileTarget"] = to;
                throw;
            }
        }
    }
}
