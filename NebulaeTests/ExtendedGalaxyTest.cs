using HarmonyLib;
using global::NebulaeModel;
using global::NebulaeModel.Utils;
using global::NebulaeModel.Packets.Session;
using NebulaeWorld;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class ExtendedGalaxyTest
{
    private readonly Harmony harmony = new("nebulae.tests.extended-galaxy");
    private static readonly System.Reflection.MethodInfo Generate = AccessTools.Method(typeof(UniverseGen), "GenerateTempPoses");
    private static readonly System.Reflection.FieldInfo Poses = AccessTools.Field(typeof(UniverseGen), "tmp_poses");

    [TestCleanup]
    public void Cleanup() => harmony.UnpatchSelf();

    private void PatchGeneration() => harmony.CreateClassProcessor(typeof(ExtendedGalaxy_Patch)).Patch();

    private static VectorLF3[] Positions(int seed, int stars)
    {
        var actual = (int)Generate.Invoke(null, [new DotNet35Random(seed).Next(), stars, 4, 2.0, 2.3, 3.5, .18]);
        var positions = ((List<VectorLF3>)Poses.GetValue(null)).ToArray();
        TestAssert.AreEqual(stars, actual, $"seed={seed}, stars={stars}");
        TestAssert.HasCount(stars, positions);
        return positions;
    }

    [TestMethod]
    public void VanillaPositionsAreBitForBitUnchanged()
    {
        var baseline = new Dictionary<(int, int), VectorLF3[]>();
        foreach (var count in new[] { 32, 48, 64 })
            foreach (var seed in new[] { 0, 1, 42, 802, 99999999 }) baseline[(seed, count)] = Positions(seed, count);
        PatchGeneration();
        foreach (var sample in baseline) CollectionAssert.AreEqual(sample.Value, Positions(sample.Key.Item1, sample.Key.Item2));
    }

    [TestMethod]
    public void ExpandedGalaxyPositionsAreRepeatableAcrossRepresentativeSeeds()
    {
        PatchGeneration();
        foreach (var seed in new[] { 0, 1, 42, 802, 99999999 })
        {
            CollectionAssert.AreEqual(Positions(seed, 1024), Positions(seed, 1024), $"seed={seed}");
        }
    }

    [TestMethod]
    [DataRow(65)]
    [DataRow(80)]
    [DataRow(81)]
    [DataRow(256)]
    [DataRow(257)]
    [DataRow(1024)]
    [DataRow(1025)]
    [DataRow(2047)]
    [DataRow(2048)]
    [DataRow(2049)]
    [DataRow(4095)]
    [DataRow(4096)]
    [DataRow(4097)]
    [DataRow(8191)]
    public void ExpandedGalaxiesCoverTheirLastStarAndPlanet(int count)
    {
        PatchGeneration();
        var positions = Positions(802, count);
        var galaxy = new GalaxyData { starCount = count, stars = new StarData[count] };
        ExtendedGalaxyAstros_Patch.Prefix(galaxy);
        var star = new StarData { galaxy = galaxy, id = count, index = count - 1, position = positions[count - 1], planetCount = 1 };
        var planet = new PlanetData { galaxy = galaxy, star = star, id = count * 100 + 1 };
        star.planets = [planet];
        galaxy.stars[count - 1] = star;
        galaxy.astrosData[planet.id].id = planet.id;
        TestAssert.AreSame(star, galaxy.StarById(count));
        TestAssert.AreSame(planet, galaxy.PlanetById(planet.id));
        TestAssert.AreEqual(planet.id, galaxy.astrosData[planet.id].id);
        TestAssert.IsGreaterThan(planet.id, galaxy.astrosFactory.Length);
        var model = Uninitialized<SectorModel>();
        model.sector = new SpaceSector { galaxy = galaxy };
        TestAssert.IsGreaterThan(planet.id, ExtendedGalaxyRender_Patch.Capacity(model));
    }

    [TestMethod]
    [DataRow(1024)]
    [DataRow(8191)]
    public void GraphAndReferenceStatisticsGrowForHighAstroIds(int count)
    {
        var previous = GameMain.data;
        try
        {
            GameMain.data = Uninitialized<GameData>();
            GameMain.data.galaxy = new GalaxyData { starCount = count };
            ExtendedGalaxy_Patch.GraphCapacity(GameMain.galaxy);
            TestAssert.IsGreaterThanOrEqualTo(count, ((int[])AccessTools.Field(typeof(UniverseGen), "tmp_state").GetValue(null)).Length);
            var tip = Uninitialized<UIReferenceSpeedTip>();
            GalaxyLimits.EnsureReferenceCapacity(tip);
            var planetId = count * 100 + 1;
            AccessTools.Method(typeof(UIReferenceSpeedTip), "AddSubTipEntryData").Invoke(tip, [planetId, 7f, false, false, false]);
            var data = (RefSpeedSubTipEntryData[])AccessTools.Field(typeof(UIReferenceSpeedTip), "loadedSubTipDatas").GetValue(tip);
            TestAssert.AreEqual(7f, data[planetId].normalSpeed);
            TestAssert.HasCount((count + 1) * 100, data);
        }
        finally { GameMain.data = previous; }
    }

    [TestMethod]
    public void GenerationAndRenderTranspilersBindRealGameBodies()
    {
        PatchGeneration();
        harmony.CreateClassProcessor(typeof(ExtendedGalaxyAstros_Patch)).Patch();
        harmony.CreateClassProcessor(typeof(ExtendedGalaxyRender_Patch)).Patch();
        harmony.CreateClassProcessor(typeof(ExtendedGalaxyHives_Patch)).Patch();
        // Rendering cannot execute outside Unity, but its allocation call sites must be patched.
        var render = AccessTools.Method(typeof(SectorModel), nameof(SectorModel.CreateGalaxyAstroBuffer));
        TestAssert.AreEqual(1, Harmony.GetPatchInfo(render).Transpilers.Count(p => p.owner == harmony.Id));
        var original = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(UniverseGen), "RandomPoses"));
        var patched = ExtendedGalaxy_Patch.RandomPoses(original).ToList();
        TestAssert.AreEqual(2, patched.Count(i => i.LoadsConstant(256)), "Collision retry budgets must remain vanilla.");
        var sector = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(SpaceSector), nameof(SpaceSector.SetForNewGame)));
        var expandedSector = ExtendedGalaxyHives_Patch.Transpiler(sector).ToArray();
        TestAssert.IsFalse(expandedSector.Any(i => i.LoadsConstant(100)), "Stars after the first 100 must retain their generated hives.");
        TestAssert.AreEqual(sector.Count(i => i.LoadsConstant(GalaxyLimits.MaxStars)) + 1,
            expandedSector.Count(i => i.LoadsConstant(GalaxyLimits.MaxStars)));
    }

    [TestMethod]
    [DataRow(1024)]
    [DataRow(2049)]
    [DataRow(4096)]
    [DataRow(8191)]
    public void SaveDescriptionAndEveryWorldPacketPreserveStarCount(int count)
    {
        var desc = new GameDesc { starCount = count, galaxySeed = 802, galaxyAlgo = UniverseGen.algoVersion, savedThemeIds = [] };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        desc.Export(writer); writer.Flush(); stream.Position = 0;
        var restored = new GameDesc(); restored.Import(new BinaryReader(stream));
        TestAssert.AreEqual(count, restored.starCount);
        TestAssert.AreEqual(count, RoundTrip(new LobbyResponse(desc, [], 1)).StarCount);
        TestAssert.AreEqual(count, RoundTrip(new LobbyUpdateValues(desc)).StarCount);
        TestAssert.AreEqual(count, RoundTrip(new HandshakeResponse(desc, [], false,
            new global::NebulaeModel.DataStructures.PlayerData(1, count * 100 + 1, "p"), 1),
            p => p.RegisterNestedType<global::NebulaeModel.DataStructures.PlayerData>(() => new global::NebulaeModel.DataStructures.PlayerData())).StarCount);
    }

    [TestMethod]
    public void LobbyStartRequestAndBusyResponseHaveCompleteWirePayloads()
    {
        foreach (var message in new[] { new StartGameMessage(), new StartGameMessage(false, null) })
        {
            var received = RoundTrip(message, p => p.RegisterNestedType<global::NebulaeModel.DataStructures.PlayerData>(
                () => new global::NebulaeModel.DataStructures.PlayerData()));
            TestAssert.IsFalse(received.IsAllowedToStart);
            TestAssert.IsFalse(received.IsNewPlayer);
            TestAssert.AreEqual(0, received.LocalPlayerData.PlayerId);
        }
    }

    [TestMethod]
    [DataRow(31, false)]
    [DataRow(32, true)]
    [DataRow(1024, true)]
    [DataRow(1025, true)]
    [DataRow(2048, true)]
    [DataRow(4096, true)]
    [DataRow(8191, true)]
    [DataRow(8192, false)]
    [DataRow(9999, false)]
    [DataRow(10000, false)]
    [DataRow(0, false)]
    public void DedicatedServerChecksSupportedStarCounts(int stars, bool valid)
    {
        var options = new CommandLineOptions();
        // SetForNewGame needs the loaded theme database, unrelated to argument validation.
        options.NewGameDesc = new GameDesc { starCount = stars };
        TestAssert.AreEqual(valid, options.VerifyStartupRequirements());
    }

    [TestMethod]
    [DataRow(-1, 64)]
    [DataRow(1024, 1024)]
    [DataRow(8191, 8191)]
    public void DedicatedConfigSupportsExpandedRangeAndPreservesNegativeDefault(int configured, int expected)
    {
        var config = new BepInEx.Configuration.ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
        { SaveOnConfigSet = false };
        config.Bind("Basic", "starCount", 64).Value = configured;
        var desc = new GameDesc { starCount = 64 };
        desc.ApplyModConfigFileSettings(config);
        TestAssert.AreEqual(expected, desc.starCount);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(42)]
    [DataRow(99999999)]
    public void MaximumGalaxyPositionsAreRepeatable(int seed)
    {
        PatchGeneration();
        CollectionAssert.AreEqual(Positions(seed, GalaxyLimits.MaxStars), Positions(seed, GalaxyLimits.MaxStars));
    }

    [TestMethod]
    public void MenuLimitsAndCallbacksReturnToVanillaAfterLeavingMultiplayer()
    {
        var previous = Multiplayer.IsInMultiplayerMenu;
        var session = Multiplayer.Session;
        try
        {
            Multiplayer.Session = null;
            Multiplayer.IsInMultiplayerMenu = true;
            TestAssert.IsTrue(UIGalaxySelect_Patch.UsesExtendedStarRange());
            Multiplayer.IsInMultiplayerMenu = false;
            TestAssert.IsFalse(UIGalaxySelect_Patch.UsesExtendedStarRange());
            Multiplayer.Session = Uninitialized<MultiplayerSession>();
            TestAssert.IsTrue(UIGalaxySelect_Patch.UsesExtendedStarRange(), "Direct client connections also use the More display.");
            harmony.CreateClassProcessor(typeof(ExtendedGalaxyMenu_Patch)).Patch();
            var callback = AccessTools.Method(typeof(UIGalaxySelect), nameof(UIGalaxySelect.OnStarCountSliderValueChange));
            harmony.Patch(callback, prefix: new HarmonyMethod(typeof(UIGalaxySelect_Patch), nameof(UIGalaxySelect_Patch.StarCount_Prefix)));
            TestAssert.IsTrue(Harmony.GetPatchInfo(callback).Prefixes.Any(p => p.owner == harmony.Id &&
                p.PatchMethod.Name == nameof(UIGalaxySelect_Patch.StarCount_Prefix)));
            var display = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(UIGalaxySelect), nameof(UIGalaxySelect.UpdateUIDisplay)));
            var setter = AccessTools.PropertySetter(typeof(UnityEngine.UI.Slider), nameof(UnityEngine.UI.Slider.value));
            TestAssert.IsTrue(display.Any(i => i.Calls(setter)));
            TestAssert.IsFalse(ExtendedGalaxyMenu_Patch.Display_Transpiler(display).Any(i => i.Calls(setter)));
        }
        catch (Exception failure) { TestAssert.Fail(failure.ToString()); }
        finally { Multiplayer.IsInMultiplayerMenu = previous; Multiplayer.Session = session; }
    }
}
