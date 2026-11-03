using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using NebulaeModel.Utils;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class LocalizationTest
{
    [TestMethod]
    public void ChineseCatalogueIsEmbeddedAndReadable()
    {
        TestAssert.IsTrue(NebulaeLocalization.TryTranslate("Multiplayer", true, out var text));
        TestAssert.AreEqual("多人联机", text);
    }

    [TestMethod]
    public void OtherLanguagesAndUnknownKeysFallBackToTheGame()
    {
        TestAssert.IsFalse(NebulaeLocalization.TryTranslate("Multiplayer", false, out var text));
        TestAssert.IsNull(text);
        TestAssert.IsFalse(NebulaeLocalization.TryTranslate("Nebulae.Unknown.Key", true, out text));
        TestAssert.IsNull(text);
        TestAssert.IsFalse(NebulaeLocalization.TryTranslate(null!, true, out text));
        TestAssert.IsNull(text);
        TestAssert.IsFalse(NebulaeLocalization.TryTranslate("开始游戏", true, out text));
    }

    [TestMethod]
    public void CatalogueKeysStayPrintableAsciiSourceStrings()
    {
        using var stream = typeof(NebulaeLocalization).Assembly.GetManifestResourceStream("NebulaeModel.Localization.zh-CN.json");
        var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        var catalogue = (Dictionary<string, string>)serializer.ReadObject(stream!)!;

        foreach (var pair in catalogue)
        {
            // Keys are the English source strings the code passes to .Translate(). A localized key would
            // never be looked up by the game, so it silently degrades to the untranslated source text.
            foreach (var character in pair.Key)
            {
                var isAllowedWhitespace = character is '\r' or '\n' or '\t';
                TestAssert.IsTrue(isAllowedWhitespace || character is >= ' ' and <= '~',
                    $"Catalogue key is not a printable ASCII source string: {pair.Key}");
            }

            TestAssert.IsFalse(string.IsNullOrWhiteSpace(pair.Value), $"Empty translation for: {pair.Key}");
        }
    }

    [TestMethod]
    public void TranslationsPreserveFormatArgumentsAndNumericFormats()
    {
        using var stream = typeof(NebulaeLocalization).Assembly.GetManifestResourceStream("NebulaeModel.Localization.zh-CN.json");
        var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        var catalogue = (Dictionary<string, string>)serializer.ReadObject(stream!)!;
        var placeholder = new Regex(@"\{\d+(?:,[^}:]+)?(?::[^}]+)?\}");
        foreach (var pair in catalogue)
        {
            var source = placeholder.Matches(pair.Key).Cast<Match>().Select(match => match.Value).OrderBy(value => value).ToArray();
            var translated = placeholder.Matches(pair.Value).Cast<Match>().Select(match => match.Value).OrderBy(value => value).ToArray();
            CollectionAssert.AreEqual(source, translated, pair.Key);
            if (source.Length == 0) continue;
            // Also exercise the actual .NET formatter, including reordered placeholders.
            var count = source.Select(value => int.Parse(Regex.Match(value, @"\d+").Value)).Max() + 1;
            var arguments = Enumerable.Repeat<object>(12, count).ToArray();
            TestAssert.IsFalse(string.IsNullOrEmpty(string.Format(pair.Value, arguments)), pair.Key);
        }
    }
}
