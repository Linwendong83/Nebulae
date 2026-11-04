using HarmonyLib;
using global::NebulaeModel.DataStructures;
using global::NebulaeModel.Packets.GameStates;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class GalaxySeedKeyTest
{
    private readonly Harmony harmony = new("nebulae.tests.galaxy-seed-key");

    [TestCleanup]
    public void Cleanup() => harmony.UnpatchSelf();

    private void Patch() => harmony.CreateClassProcessor(typeof(GameDescSeedKey_Patch)).Patch();

    private static GameDesc Description(int stars, int seed = 802, float resources = 1f, int mode = 0)
    {
        var desc = new GameDesc
        {
            galaxySeed = seed,
            starCount = stars,
            resourceMultiplier = resources,
            isPeaceMode = mode != 1,
            isSandboxMode = mode == 2,
            savedThemeIds = []
        };
        desc.combatSettings.SetDefault();
        return desc;
    }

    [TestMethod]
    public void EveryLegacyStarCountKeepsItsOriginalKeyAndSettingsEncoding()
    {
        var samples = new List<(GameDesc Desc, long Key)>();
        foreach (var stars in new[] { 0, 1, 20, 64, 80, 998, 999 })
            foreach (var mode in new[] { 0, 1, 2 })
                foreach (var resources in new[] { .1f, .3f, 1f, 100f })
                {
                    var desc = Description(stars, resources: resources, mode: mode);
                    samples.Add((desc, desc.seedKey64));
                }
        Patch();
        foreach (var sample in samples) TestAssert.AreEqual(sample.Key, sample.Desc.seedKey64);
    }

    [TestMethod]
    [DataRow(int.MinValue)]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(802)]
    [DataRow(99999999)]
    [DataRow(int.MaxValue)]
    public void ExtendedCountsAreUniquePositiveKeysWithoutSeedCarryOrOverflow(int seed)
    {
        var legacy = Description(999, seed).seedKey64;
        Patch();
        var keys = new HashSet<long> { legacy };
        foreach (var stars in Enumerable.Range(1000, 7192))
        {
            var desc = Description(stars, seed);
            var key = desc.seedKey64;
            TestAssert.IsTrue(keys.Add(key), $"stars={stars}, seed={seed}");
            TestAssert.IsGreaterThan(214748364799999999L, key, "Expanded keys must be outside the full vanilla namespace.");
            var game = Uninitialized<GameData>(); game.gameDesc = desc;
            TestAssert.AreEqual(key, game.GetClusterSeedKey());
        }
        TestAssert.AreNotEqual(Description(1024, 802).seedKey64, Description(24, 803).seedKey64);
        TestAssert.AreNotEqual(Description(1024, seed).seedKey64, Description(1024, unchecked(seed + 1)).seedKey64);
        TestAssert.AreNotEqual(Description(8191, seed).seedKey64, Description(1000, unchecked(seed + 1)).seedKey64);
    }

    [TestMethod]
    [DataRow(1024)]
    [DataRow(8191)]
    public void ExpandedKeysKeepNativeResourceModeAndDifficultyDistinctions(int count)
    {
        var samples = new List<(GameDesc Desc, long Legacy)>();
        foreach (var mode in new[] { 0, 1, 2 })
            foreach (var resources in new[] { .1f, .3f, 1f, 100f })
            {
                var desc = Description(count, resources: resources, mode: mode);
                samples.Add((desc, desc.seedKey64));
            }
        Patch();
        var offset = samples[0].Desc.seedKey64 - samples[0].Legacy;
        foreach (var sample in samples) TestAssert.AreEqual(offset, sample.Desc.seedKey64 - sample.Legacy);
        TestAssert.AreEqual(samples.Count, samples.Select(sample => sample.Desc.seedKey64).Distinct().Count());
    }

    [TestMethod]
    public void PropertyRecordsDoNotMix999And1024AndTheirKeysSurviveSerialization()
    {
        Patch();
        var small = Description(999); var large = Description(1024);
        var properties = new PropertySystem { propertyDatas = [] };
        properties.SetItemProduction(small.seedKey64, 6001, 100);
        properties.SetItemConsumption(small.seedKey64, 6001, 7);
        properties.SetItemProduction(large.seedKey64, 6001, 200);
        properties.SetItemConsumption(large.seedKey64, 6001, 11);
        TestAssert.HasCount(2, properties.propertyDatas);
        TestAssert.AreEqual(100, properties.GetItemProduction(small.seedKey64, 6001));
        TestAssert.AreEqual(200, properties.GetItemProduction(large.seedKey64, 6001));
        TestAssert.AreEqual(7, properties.GetItemConsumption(small.seedKey64, 6001));
        TestAssert.AreEqual(11, properties.GetItemConsumption(large.seedKey64, 6001));

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        properties.GetClusterData(large.seedKey64).Export_Main(writer); writer.Flush(); stream.Position = 0;
        var restored = new ClusterPropertyData(); restored.Import_Main(new BinaryReader(stream));
        TestAssert.AreEqual(large.seedKey64, restored.seedKey);
        TestAssert.AreEqual(200, restored.GetItemProduction(6001));
        TestAssert.AreEqual(11, restored.GetItemConsumption(6001));

        var ledger = new MetadataLedger { ClusterKey = large.seedKey64 };
        TestAssert.AreEqual(large.seedKey64, MetadataLedger.Import(ledger.Export()).ClusterKey);
        TestAssert.AreEqual(large.seedKey64, RoundTrip(new MetadataProductionPacket
        {
            WorldId = "test",
            ClusterKey = large.seedKey64,
            Earned = new int[6],
            WorldPeak = new int[6]
        }).ClusterKey);

        using var descStream = new MemoryStream();
        using var descWriter = new BinaryWriter(descStream, System.Text.Encoding.UTF8, true);
        large.Export(descWriter); descWriter.Flush(); descStream.Position = 0;
        var descRestored = new GameDesc(); descRestored.Import(new BinaryReader(descStream));
        TestAssert.AreEqual(large.seedKey64, descRestored.seedKey64);
    }
}
