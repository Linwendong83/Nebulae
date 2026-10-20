using System.IO;
using NebulaeModel.Utils;

namespace NebulaeWorld.GameStates;

/// <summary>Uses the exact vanilla file format, but replaces the file atomically and propagates IO errors.</summary>
internal static class PropertyAccountStore
{
    public static void Save()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(0);
            DSPGame.propertySystem.Export(writer);
        }
        AtomicFile.Write(Path.Combine(GameConfig.propertyFolder, AccountData.me.userId.ToString()), stream.ToArray());
    }
}
