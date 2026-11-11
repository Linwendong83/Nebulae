using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using global::NebulaeModel;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Session;
using NebulaeModel.Packets.Universe;
using NebulaeNetwork.PacketProcessors.Session;
using NebulaeNetwork.PacketProcessors.Universe;
using NebulaeWorld;
using NebulaeWorld.Factory;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class GalaxyNamingTest
{
    private MultiplayerSession previousSession = null!;
    private GameData previousData = null!;
    private object previousThemes = null!;
    private RecordingBroadcastNetwork network = null!;

    private sealed class NameReceiver : NameInputProcessor
    {
        public void Apply(NameInputPacket packet) => ProcessPacket(packet, null!);
    }

    private sealed class LobbyReceiver : LobbyResponseProcessor
    {
        public void Apply(LobbyResponse packet) => ProcessPacket(packet, null!);
    }

    private sealed class LobbyUpdateReceiver : LobbyUpdateValuesProcessor
    {
        public void Apply(LobbyUpdateValues packet) => ProcessPacket(packet, null!);
    }

    private sealed class HandshakeReceiver : HandshakeResponseProcessor
    {
        public void Apply(HandshakeResponse packet) => ProcessPacket(packet, null!);
    }

    [TestInitialize]
    public void Initialize()
    {
        previousSession = Multiplayer.Session;
        previousData = GameMain.data;
        previousThemes = AccessTools.Field(typeof(LDB), "_themes").GetValue(null);
        var themes = Uninitialized<ThemeProtoSet>();
        AccessTools.Field(typeof(ThemeProtoSet), "m_CachedPtr").SetValue(themes, new IntPtr(1));
        themes.dataArray = Array.Empty<ThemeProto>();
        AccessTools.Field(typeof(LDB), "_themes").SetValue(null, themes);

        network = new RecordingBroadcastNetwork();
        Multiplayer.Session = Uninitialized<MultiplayerSession>();
        Multiplayer.Session.Network = network;
        Multiplayer.Session.Client = Uninitialized<global::NebulaeNetwork.Client>();
        Multiplayer.Session.Factories = new FactoryManager();

        var galaxy = new GalaxyData { starCount = 1 };
        var star = new StarData { id = 1, name = "南门二", galaxy = galaxy, planetCount = 1 };
        var planet = new PlanetData { id = 101, index = 0, star = star, galaxy = galaxy };
        star.planets = [planet];
        galaxy.stars = [star];
        GameMain.data = Uninitialized<GameData>();
        GameMain.data.galaxy = galaxy;
        GameMain.data.gameDesc = new GameDesc { starNameLCID = 2052 };
        AccessTools.Field(typeof(GameData), "<localStar>k__BackingField").SetValue(GameMain.data, star);
        AccessTools.Field(typeof(GameData), "<localPlanet>k__BackingField").SetValue(GameMain.data, planet);
        planet.RegenerateName(false, GameMain.data.gameDesc);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Multiplayer.Session = previousSession;
        GameMain.data = previousData;
        AccessTools.Field(typeof(LDB), "_themes").SetValue(null, previousThemes);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2052)]
    public void EveryWorldDescriptionPacketPreservesTheHostsNamingRule(int lcid)
    {
        var desc = new GameDesc
        {
            galaxyAlgo = 20221001,
            galaxySeed = 42,
            starCount = 64,
            starNameLCID = lcid,
            savedThemeIds = [1, 2],
            resourceMultiplier = 1f
        };
        TestAssert.AreEqual(lcid, RoundTrip(new HandshakeResponse(desc, [], false, new PlayerData(1, 101, "p1"), 1)).StarNameLCID);
        TestAssert.AreEqual(lcid, RoundTrip(new LobbyResponse(desc, [], 1)).StarNameLCID);
        TestAssert.AreEqual(lcid, RoundTrip(new LobbyUpdateValues(desc)).StarNameLCID);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2052)]
    public void LobbyReceiversInstallTheRuleBeforeGeneratingTheGalaxy(int lcid)
    {
        var previousRoot = UIRoot.instance;
        var root = Uninitialized<UIRoot>();
        root.galaxySelect = Uninitialized<UIGalaxySelect>();
        root.uiMainMenu = Uninitialized<UIMainMenu>();
        var select = root.galaxySelect;
        select.uiCombat = Uninitialized<UICombatSettingsDF>();
        select.GoalSetting = Uninitialized<UIGoalSetting>();
        select.sandboxToggle = Uninitialized<UIToggle>();
        select.sandboxToggle.toggle = Uninitialized<UnityEngine.UI.Toggle>();
        Multiplayer.Session.LocalPlayer = new LocalPlayer();
        using var harmony = new NamingUiScope(root);
        try
        {
            var desc = new GameDesc { starNameLCID = lcid, starCount = 64, savedThemeIds = [] };
            using var writer = new BinaryUtils.Writer();
            desc.combatSettings.Export(writer.BinaryWriter);
            new LobbyReceiver().Apply(RoundTrip(new LobbyResponse(desc, writer.CloseAndGetBytes(), 1)));
            TestAssert.AreEqual(lcid, generatedLCID);
            TestAssert.AreEqual(lcid, Description(select).starNameLCID);

            desc.starNameLCID = lcid == 0 ? 2052 : 0;
            new LobbyUpdateReceiver().Apply(RoundTrip(new LobbyUpdateValues(desc)));
            TestAssert.AreEqual(desc.starNameLCID, generatedLCID);
            TestAssert.AreEqual(desc.starNameLCID, Description(select).starNameLCID);
        }
        finally { AccessTools.Field(typeof(UIRoot), "_instance").SetValue(null, previousRoot); }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2052)]
    public void RunningServerHandshakeInstallsTheRuleUsedToStartTheClientWorld(int lcid)
    {
        var previousRoot = UIRoot.instance;
        var previousGame = DSPGame.Game;
        var previousDesc = DSPGame.GameDesc;
        var previousReturnToJoin = Multiplayer.ShouldReturnToJoinMenu;
        var root = Uninitialized<UIRoot>();
        using var harmony = new NamingUiScope(root);
        try
        {
            DSPGame.Game = null;
            Multiplayer.Session.LocalPlayer = new LocalPlayer();
            Multiplayer.Session.Planets = new NebulaeWorld.Planet.PlanetManager();
            Multiplayer.Session.Goals = new NebulaeWorld.GameStates.GoalManager();
            Multiplayer.Session.Authority = new NebulaeWorld.Authority.AuthoritySessionState();
            AccessTools.Field(typeof(MultiplayerSession), "<JoinSync>k__BackingField")
                .SetValue(Multiplayer.Session, new NebulaeWorld.GameStates.JoinSyncManager());
            var desc = new GameDesc { starNameLCID = lcid, starCount = 64, savedThemeIds = [] };
            using var writer = new BinaryUtils.Writer();
            desc.combatSettings.Export(writer.BinaryWriter);
            new HandshakeReceiver().Apply(RoundTrip(new HandshakeResponse(desc, writer.CloseAndGetBytes(),
                false, new PlayerData(1, 101, "p1"), 1)));
            TestAssert.AreEqual(lcid, DSPGame.GameDesc.starNameLCID);
            TestAssert.IsInstanceOfType<GlobalGameDataRequest>(network.Packets.Single());
        }
        finally
        {
            AccessTools.Field(typeof(UIRoot), "_instance").SetValue(null, previousRoot);
            DSPGame.Game = previousGame;
            DSPGame.GameDesc = previousDesc;
            Multiplayer.ShouldReturnToJoinMenu = previousReturnToJoin;
        }
    }

    [TestMethod]
    public void ChineseNamingSurvivesVanillaSaveAndLoad()
    {
        var desc = new GameDesc { starNameLCID = 2052, savedThemeIds = [] };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        desc.Export(writer);
        writer.Flush();
        stream.Position = 0;
        var restored = new GameDesc();
        using var reader = new BinaryReader(stream);
        restored.Import(reader);
        TestAssert.AreEqual(2052, restored.starNameLCID);
        TestAssert.AreEqual(NameGen.RandomStarNameFromRawNames(42, desc.starNameLCID),
            NameGen.RandomStarNameFromRawNames(42, restored.starNameLCID));
        TestAssert.AreNotEqual(NameGen.RandomStarNameFromRawNames(42, 0),
            NameGen.RandomStarNameFromRawNames(42, restored.starNameLCID));
    }

    [TestMethod]
    [DataRow(false, 2052)]
    [DataRow(true, 2052)]
    [DataRow(false, 0)]
    [DataRow(true, 0)]
    public void HeadlessNamingArgumentWorksOnEitherSideOfNewGame(bool before, int lcid)
    {
        var options = new CommandLineOptions();
        var names = new[] { "-star-name-lcid", lcid.ToString() };
        var game = new[] { "-nebulae-server", "-batchmode", "-newgame", "42", "32", "1" };
        options.ParseArgs(before ? names.Concat(game).ToArray() : game.Concat(names).ToArray());
        TestAssert.IsTrue(options.VerifyStartupRequirements());
        TestAssert.AreEqual(lcid, options.NewGameDesc.starNameLCID);
        TestAssert.AreEqual(42, options.NewGameDesc.galaxySeed);
    }

    [TestMethod]
    public void HeadlessDefaultNewGameAlsoAcceptsChineseNames()
    {
        var options = new CommandLineOptions();
        options.ParseArgs(["-newgame-default", "-star-name-lcid", "2052"]);
        TestAssert.AreEqual(2052, options.NewGameDesc.starNameLCID);
    }

    [TestMethod]
    [DataRow("bad")]
    [DataRow("-1")]
    [DataRow("123")]
    [DataRow(null)]
    public void InvalidNamingArgumentsRefuseStartup(string? value)
    {
        var options = new CommandLineOptions();
        options.ParseArgs(value == null ? ["-star-name-lcid"] : ["-star-name-lcid", value]);
        TestAssert.IsFalse(options.VerifyStartupRequirements());
    }

    [TestMethod]
    public void NamingOverrideDoesNotReplaceLoadedWorldSettings()
    {
        var options = new CommandLineOptions { SaveName = "existing", LoadArgumentExists = true };
        options.ParseArgs(["-star-name-lcid", "0"]);
        TestAssert.IsFalse(options.ShouldCreateNewGame);
        TestAssert.AreEqual(2052, GameMain.data.gameDesc.starNameLCID);
    }

    [TestMethod]
    public void HeadlessConfigCanSelectChineseNames()
    {
        var path = Path.Combine(Path.GetTempPath(), "nebulae-names-" + Guid.NewGuid().ToString("N") + ".cfg");
        var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
        config.Bind("Basic", "starNameLCID", 2052).Value = 2052;
        var desc = new GameDesc();
        desc.ApplyModConfigFileSettings(config);
        TestAssert.AreEqual(2052, desc.starNameLCID);
        TestAssert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    [DataRow("star")]
    [DataRow("planet")]
    [DataRow("globe-planet")]
    [DataRow("globe-star")]
    public void EveryRenameEntryPointSendsAnEmptyOverrideToRestoreDefault(string entry)
    {
        if (entry == "star")
        {
            var ui = Uninitialized<UIStarDetail>();
            AccessTools.Field(typeof(UIStarDetail), "_star").SetValue(ui, GameMain.localStar);
            UIStarDetail_Patch.OnNameInputEndEdit_Postfix(ui);
        }
        else if (entry == "planet")
        {
            var ui = Uninitialized<UIPlanetDetail>();
            AccessTools.Field(typeof(UIPlanetDetail), "_planet").SetValue(ui, GameMain.localPlanet);
            UIPlanetDetail_Patch.OnNameInputEndEdit_Postfix(ui);
        }
        else
        {
            if (entry == "globe-star")
                AccessTools.Field(typeof(GameData), "<localPlanet>k__BackingField").SetValue(GameMain.data, null);
            UIPlanetGlobe_Patch.OnNameInputEndEdit_Postfix();
        }
        var packet = (NameInputPacket)network.Packets.Single();
        TestAssert.AreEqual("", RoundTrip(packet).Names.Single());
        TestAssert.AreEqual(entry is "star" or "globe-star" ? 1 : SessionSentinels.STAR_NONE, packet.StarIds.Single());
        TestAssert.AreEqual(entry is "star" or "globe-star" ? SessionSentinels.PLANET_NONE : 101, packet.PlanetIds.Single());
    }

    [TestMethod]
    public void ClearingAStarNameRestoresChineseDefaultsAndRegeneratesItsPlanets()
    {
        var receiver = new NameReceiver();
        var defaultPlanetName = GameMain.localPlanet.name;
        receiver.Apply(RoundTrip(new NameInputPacket("中文改名", 1, SessionSentinels.PLANET_NONE)));
        TestAssert.AreEqual("中文改名", GameMain.localStar.displayName);
        StringAssert.StartsWith(GameMain.localPlanet.displayName, "中文改名");
        receiver.Apply(RoundTrip(new NameInputPacket("", 1, SessionSentinels.PLANET_NONE)));
        TestAssert.AreEqual("南门二", GameMain.localStar.displayName);
        TestAssert.AreEqual(defaultPlanetName, GameMain.localPlanet.displayName);
        TestAssert.Contains("1", GameMain.localPlanet.name);
        TestAssert.DoesNotContain("I", GameMain.localPlanet.name);
    }

    [TestMethod]
    public void ClearingAPlanetNameRestoresItsDefaultWithoutTouchingTheStar()
    {
        var receiver = new NameReceiver();
        var original = GameMain.localPlanet.displayName;
        receiver.Apply(RoundTrip(new NameInputPacket("中文行星", SessionSentinels.STAR_NONE, 101)));
        TestAssert.AreEqual("中文行星", GameMain.localPlanet.displayName);
        receiver.Apply(RoundTrip(new NameInputPacket("", SessionSentinels.STAR_NONE, 101)));
        TestAssert.AreEqual(original, GameMain.localPlanet.displayName);
        TestAssert.AreEqual("南门二", GameMain.localStar.displayName);
    }

    [TestMethod]
    public void InitialNamesSyncPreservesChineseOverridesAndLeavesGeneratedNamesAlone()
    {
        TestAssert.IsEmpty(new NameInputPacket(GameMain.galaxy).Names);
        GameMain.localStar.overrideName = "中文恒星";
        GameMain.localPlanet.overrideName = "中文行星";
        var packet = RoundTrip(new NameInputPacket(GameMain.galaxy));
        CollectionAssert.AreEqual(new[] { "中文恒星", "中文行星" }, packet.Names);
        CollectionAssert.AreEqual(new[] { 1, SessionSentinels.STAR_NONE }, packet.StarIds);
        CollectionAssert.AreEqual(new[] { SessionSentinels.PLANET_NONE, 101 }, packet.PlanetIds);
    }

    [TestMethod]
    public void ClientsCannotChangeTheHostsNamingRule()
    {
        TestAssert.IsFalse(UIGalaxySelect_Patch.ClientSettings_Prefix());
        Multiplayer.Session = null;
        TestAssert.IsTrue(UIGalaxySelect_Patch.ClientSettings_Prefix());
    }

    private static int generatedLCID;
    private static GameDesc Description(UIGalaxySelect select) =>
        (GameDesc)AccessTools.Field(typeof(UIGalaxySelect), "gameDesc").GetValue(select);
    private static void CaptureGalaxy(UIGalaxySelect select)
    {
        generatedLCID = Description(select).starNameLCID;
    }

    private static IEnumerable<CodeInstruction> WithoutSceneUi(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(AccessTools.Method(typeof(UIGalaxySelect), nameof(UIGalaxySelect.SetStarmapGalaxy))))
            {
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GalaxyNamingTest), nameof(CaptureGalaxy)))
                    .WithLabels(instruction.labels).WithBlocks(instruction.blocks);
            }
            else if (instruction.Calls(AccessTools.PropertySetter(typeof(UIToggle), nameof(UIToggle.isOn))))
            {
                yield return new CodeInstruction(OpCodes.Pop).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                yield return new CodeInstruction(OpCodes.Pop);
            }
            else if (instruction.Calls(AccessTools.Method(typeof(ManualBehaviour), "_Open")) ||
                     instruction.Calls(AccessTools.Method(typeof(ManualBehaviour), "_Close")) ||
                     instruction.Calls(AccessTools.Method(typeof(UIRoot), nameof(UIRoot.OpenLoadingUI))))
                yield return new CodeInstruction(OpCodes.Pop).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
            else yield return instruction;
        }
    }

    // Only the scene-dependent UI is suppressed. Receivers, SetForNewGame and the
    // real serializer all run, including the transition from English to Chinese.
    private sealed class NamingUiScope : IDisposable
    {
        private readonly Harmony harmony = new("nebulae.tests.naming-ui");
        public NamingUiScope(UIRoot root)
        {
            AccessTools.Field(typeof(UIRoot), "_instance").SetValue(null, root);
            foreach (var type in new[] { typeof(LobbyResponseProcessor), typeof(LobbyUpdateValuesProcessor), typeof(HandshakeResponseProcessor) })
                harmony.Patch(AccessTools.Method(type, "ProcessPacket"),
                    transpiler: new HarmonyMethod(typeof(GalaxyNamingTest), nameof(WithoutSceneUi)));
            generatedLCID = -1;
        }
        public void Dispose() => harmony.UnpatchSelf();
    }

    private static T RoundTrip<T>(T packet) where T : class, new() =>
        TestSupport.RoundTrip(packet, processor => processor.RegisterNestedType<PlayerData>(() => new PlayerData()));
}
