using System.Collections.Generic;
using System.Runtime.Serialization.Json;

namespace NebulaeModel.Utils;

/// <summary>Built-in translations for Nebulae's English string keys.</summary>
internal static class NebulaeLocalization
{
    private static readonly Dictionary<string, string> Chinese = LoadChinese();

    // Keep game/Unity state out of the catalogue so it can be validated independently.
    public static bool TryTranslate(string key, bool simplifiedChinese, out string translation)
    {
        translation = null;
        if (!simplifiedChinese || key == null || !Chinese.TryGetValue(key, out var value))
        {
            return false;
        }
        translation = value;
        return true;
    }

    private static Dictionary<string, string> LoadChinese()
    {
        using var stream = typeof(NebulaeLocalization).Assembly.GetManifestResourceStream("NebulaeModel.Localization.zh-CN.json");
        var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        return (Dictionary<string, string>)serializer.ReadObject(stream);
    }
}
