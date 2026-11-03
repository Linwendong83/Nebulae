using HarmonyLib;
using NebulaeModel.Contracts;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Trash;
using NebulaeModel.Utils;
using NebulaeWorld;
using NebulaeWorld.GameStates;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class PickFilterPersistenceTest
{
    private static string identity = "pick-player-a";
    private MultiplayerSession? previousSession;
    private GameData? previousData;
    private bool previousDedicated;
    private string previousWorld = null!;
    private object previousPropertyPath = null!;
    private string directory = null!;
    private Harmony harmony = null!;
    private readonly RecordingNetwork network = new();
    private string FilePath => Path.Combine(directory, "Nebulae", AtomicFile.IdentityFileName(identity),
        SaveManager.WorldId + ".pick-filters");
    private Dictionary<int, int> Filters => GameMain.mainPlayer.controller.actionPick.pickFilters;

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session;
        previousData = GameMain.data;
        previousDedicated = Multiplayer.IsDedicated;
        previousWorld = SaveManager.WorldId;
        previousPropertyPath = AccessTools.Field(typeof(GameConfig), "propertyPath").GetValue(null);
        directory = Path.Combine(Path.GetTempPath(), "nebulae-pick-" + Guid.NewGuid().ToString("N"));
        AccessTools.Field(typeof(GameConfig), "propertyPath").SetValue(null, directory);
        identity = "pick-player-a";
        SaveManager.SetWorldId(Guid.NewGuid().ToString("N"));
        Multiplayer.IsDedicated = false;
        Reconnect();
        harmony = new Harmony("nebulae.tests.pick-filters");
        harmony.Patch(AccessTools.PropertyGetter(typeof(MetadataManager), nameof(MetadataManager.LocalIdentity)),
            prefix: new HarmonyMethod(typeof(PickFilterPersistenceTest), nameof(LocalIdentity)));
        // Stub rendering before compiling the button patch, so the JIT cannot inline Unity calls.
        foreach (var method in new[] { "RefreshIcons", "SetBufferData", "SetMaterialProps" })
            harmony.Patch(AccessTools.Method(typeof(UILootFilter), method),
                prefix: new HarmonyMethod(typeof(PickFilterPersistenceTest), nameof(SkipRendering)));
        harmony.CreateClassProcessor(typeof(UILootFilter_Patch)).Patch();
        foreach (var method in new[] { "SetPickFilter", "OnPickAllButtonClick", "ApplyFilterSettings" })
            Check.IsTrue(Harmony.GetPatchInfo(AccessTools.Method(typeof(UILootFilter), method)).Postfixes
                .Any(patch => patch.owner == harmony.Id), method);
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchSelf();
        Multiplayer.Session = previousSession;
        GameMain.data = previousData;
        Multiplayer.IsDedicated = previousDedicated;
        SaveManager.SetWorldId(previousWorld);
        AccessTools.Field(typeof(GameConfig), "propertyPath").SetValue(null, previousPropertyPath);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private void Reconnect()
    {
        Multiplayer.Session = Uninitialized<MultiplayerSession>();
        Multiplayer.Session.IsGameLoaded = true;
        Multiplayer.Session.Network = network;
        var game = Uninitialized<GameData>();
        var player = Uninitialized<Player>();
        var controller = Uninitialized<PlayerController>();
        controller.actionPick = new PlayerAction_Pick { pickFilters = new Dictionary<int, int>() };
        SetBackingField(player, "controller", controller);
        SetBackingField(game, "mainPlayer", player);
        game.preferences = new GamePrefsData();
        game.preferences.Init(game);
        game.trashSystem = Uninitialized<TrashSystem>();
        game.trashSystem.enemyDropBans = new HashSet<int> { 1201 };
        GameMain.data = game;
    }

    private static UILootFilter OpenWindow()
    {
        var window = Uninitialized<UILootFilter>();
        NativeGameAccess.SetHiddenProperty(window, nameof(ManualBehaviour.data), GameMain.data);
        SetField(window, "pickFilters", new Dictionary<int, int>());
        SetField(window, "dropBans", new HashSet<int>());
        Call(window, "GetFilterSettings");
        return window;
    }

    private static void Call(UILootFilter window, string method, params object[] args) =>
        AccessTools.Method(typeof(UILootFilter), method).Invoke(window, args);

    private void AssertFilters(params (int Item, int Mode)[] expected)
    {
        Check.HasCount(expected.Length, Filters);
        foreach (var (item, mode) in expected) Check.AreEqual(mode, Filters[item]);
        CollectionAssert.AreEquivalent(Filters.ToArray(), GameMain.data.preferences.pickFilters.ToArray());
        CollectionAssert.AreEquivalent(new[] { 1201 }, GameMain.data.trashSystem.enemyDropBans.ToArray());
    }

    [TestMethod]
    public void NativeEditsSurviveDisconnectWithTheWindowStillOpenAndCanReturnToDefaults()
    {
        var window = OpenWindow();
        Call(window, "SetPickFilter", 1001); // Default -> automatic.
        Call(window, "SetPickFilter", 1002);
        Call(window, "SetPickFilter", 1002); // Automatic -> excluded.
        Check.IsEmpty(Filters, "The native window has not applied its draft yet.");
        Check.IsEmpty(network.Packets, "Personal pickup edits must not broadcast drop settings.");

        Reconnect();
        LocalPlayerPreferences.RestorePickFilters();
        AssertFilters((1001, 1), (1002, 0));

        window = OpenWindow();
        Call(window, "SetPickFilter", 1001);
        Call(window, "SetPickFilter", 1001); // Automatic -> excluded -> default.
        Call(window, "SetPickFilter", 1002); // Excluded -> default.
        Reconnect();
        Filters[9999] = 1;
        GameMain.data.preferences.pickFilters[9999] = 1; // An older native save must not win.
        LocalPlayerPreferences.RestorePickFilters();
        AssertFilters();
    }

    [TestMethod]
    public void ClosingTheNativeWindowStillAppliesAndBroadcastsOnlyTheExistingDropPacket()
    {
        var window = OpenWindow();
        Call(window, "SetPickFilter", 1001);
        Call(window, "ApplyFilterSettings");
        Check.AreEqual(1, Filters[1001]);
        Check.HasCount(1, network.Packets);
        Check.IsInstanceOfType<TrashSystemLootFilterPacket>(network.Packets[0]);
        using (var reader = new BinaryUtils.Reader(((TrashSystemLootFilterPacket)network.Packets[0]).EnemyDropBansData))
        {
            Check.AreEqual(1, reader.BinaryReader.ReadInt32());
            Check.AreEqual(1201, reader.BinaryReader.ReadInt32());
        }
        Reconnect();
        LocalPlayerPreferences.RestorePickFilters();
        AssertFilters((1001, 1));
    }

    [TestMethod]
    public void PickAllRemovesExclusionsWithoutLosingAutomaticPickupOnReconnect()
    {
        Filters[1001] = 1;
        Filters[1002] = 0;
        var window = OpenWindow();
        Call(window, "OnPickAllButtonClick", 0);
        Reconnect();
        LocalPlayerPreferences.RestorePickFilters();
        AssertFilters((1001, 1));
    }

    [TestMethod]
    public void PreferencesStaySpecificToThePlayerAndWorldAndDoNotReplaceExistingHostDefaults()
    {
        var world = SaveManager.WorldId;
        LocalPlayerPreferences.SavePickFilters(new Dictionary<int, int> { [1001] = 1 });
        var saved = File.ReadAllBytes(FilePath);
        SaveManager.SetWorldId(Guid.NewGuid().ToString("N"));
        Reconnect();
        LocalPlayerPreferences.RestorePickFilters();
        AssertFilters();
        LocalPlayerPreferences.SavePickFilters(new Dictionary<int, int> { [1002] = 0 });
        SaveManager.SetWorldId(world);
        identity = "pick-player-b";
        Reconnect();
        Filters[1003] = 0;
        GameMain.data.preferences.pickFilters[1003] = 0;
        LocalPlayerPreferences.RestorePickFilters();
        AssertFilters((1003, 0)); // No personal file: keep the native host save's settings.
        identity = "pick-player-a";
        Reconnect();
        LocalPlayerPreferences.RestorePickFilters();
        AssertFilters((1001, 1));
        CollectionAssert.AreEqual(saved, File.ReadAllBytes(FilePath));
    }

    [TestMethod]
    [DataRow("truncated")]
    [DataRow("mode")]
    [DataRow("duplicate")]
    [DataRow("trailing")]
    public void DamagedPrimaryRestoresBackupWithoutApplyingAPartialFilter(string damage)
    {
        LocalPlayerPreferences.SavePickFilters(new Dictionary<int, int> { [1001] = 1, [1002] = 0 });
        LocalPlayerPreferences.SavePickFilters(new Dictionary<int, int> { [1003] = 1 });
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(1);
                writer.Write(damage == "duplicate" ? 2 : 1);
                writer.Write(1004);
                if (damage != "truncated") writer.Write(damage == "mode" ? 2 : 1);
                if (damage == "duplicate") { writer.Write(1004); writer.Write(0); }
                if (damage == "trailing") writer.Write(123);
            }
            File.WriteAllBytes(FilePath, stream.ToArray());
        }
        Reconnect();
        LocalPlayerPreferences.RestorePickFilters();
        AssertFilters((1001, 1), (1002, 0));
    }

    [TestMethod]
    [DataRow("singleplayer")]
    [DataRow("loading")]
    [DataRow("dedicated")]
    public void NonInteractiveSessionsNeitherOverwriteNorRestorePersonalFilters(string mode)
    {
        LocalPlayerPreferences.SavePickFilters(new Dictionary<int, int> { [1001] = 1 });
        var saved = File.ReadAllBytes(FilePath);
        Filters[1002] = 0;
        GameMain.data.preferences.pickFilters[1002] = 0;
        if (mode == "singleplayer") Multiplayer.Session = null;
        if (mode == "loading") Multiplayer.Session!.IsGameLoaded = false;
        if (mode == "dedicated") Multiplayer.IsDedicated = true;
        LocalPlayerPreferences.SavePickFilters(new Dictionary<int, int>());
        LocalPlayerPreferences.RestorePickFilters();
        AssertFilters((1002, 0));
        CollectionAssert.AreEqual(saved, File.ReadAllBytes(FilePath));
    }

    private static bool LocalIdentity(ref string __result) { __result = identity; return false; }
    private static bool SkipRendering() => false;


}
