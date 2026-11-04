using System.Net;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Contracts;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Warning;
using NebulaeNetwork.PacketProcessors.Warning;
using NebulaeWorld;
using NebulaeWorld.Warning;
using WebSocketSharp;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class GreenPrebuildWarningTest
{
    private MultiplayerSession? previousSession;
    private GameData? previousData;
    private int previousMask;
    private Harmony harmony = null!;
    private GameData host = null!;
    private readonly RecordingNetwork network = new();
    private readonly WarningManager warnings = new();
    private static ModelProtoSet models = null!;
    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session; previousData = GameMain.data;
        previousMask = EntitySignRenderer.buildingWarningMask;
        EntitySignRenderer.buildingWarningMask = int.MaxValue;
        models = Uninitialized<ModelProtoSet>();
        SetField(models, "dataIndices", new Dictionary<int, int>());
        harmony = new Harmony("nebulae.tests.green-prebuild-warning");
        harmony.Patch(AccessTools.PropertyGetter(typeof(LDB), nameof(LDB.models)),
            prefix: new HarmonyMethod(typeof(GreenPrebuildWarningTest), nameof(GetModels)));
        // The native warning and preference methods run normally; only Unity UI is stubbed.
        harmony.Patch(AccessTools.Method(typeof(WarningManager), "RefreshGreenPrebuildWarningPanel"),
            transpiler: new HarmonyMethod(typeof(GreenPrebuildWarningTest), nameof(SkipUI)));
        host = NewWorld();
        Use(host, true);
    }

    [TestCleanup]
    public void TearDown()
    {
        try { harmony.UnpatchSelf(); }
        finally
        {
            warnings.Dispose();
            Multiplayer.Session = previousSession; GameMain.data = previousData;
            EntitySignRenderer.buildingWarningMask = previousMask;
        }
    }

    private void Use(GameData data, bool hostRole)
    {
        var session = Uninitialized<MultiplayerSession>();
        session.LocalPlayer = new LocalPlayer { IsHost = hostRole };
        session.Warning = warnings; session.Network = network; session.IsGameLoaded = true;
        Multiplayer.Session = session; GameMain.data = data;
    }

    private static GameData NewWorld()
    {
        var data = Uninitialized<GameData>();
        data.preferences = new GamePrefsData { greenPrebuildWarningPlanetIds = new HashSet<int>() };
        var star = new StarData { id = 1, planets = new PlanetData[3] };
        data.galaxy = new GalaxyData { stars = new[] { star } };
        data.factories = new PlanetFactory[2]; data.factoryCount = 2;
        for (var i = 0; i < 3; i++)
        {
            var planet = star.planets[i] = new PlanetData { id = 101 + i, star = star };
            if (i == 2) continue; // A valid planet that has no factory yet.
            var factory = data.factories[i] = new PlanetFactory();
            planet.factory = factory;
            SetBackingField(factory, "planet", planet);
            SetBackingField(factory, "gameData", data);
            SetBackingField(factory, "index", i);
            factory.prebuildCursor = 3; factory.prebuildPool = new PrebuildData[4];
            factory.prebuildPool[1] = new PrebuildData { id = 1, protoId = 2001, itemRequired = 0 };
            factory.prebuildPool[2] = new PrebuildData { id = 2, protoId = 2001, itemRequired = 1 };
        }
        var ws = data.warningSystem = new WarningSystem
        {
            gameData = data,
            warningPool = new WarningData[32],
            warningCounts = new int[1000],
            warningSignals = new int[1000],
            focusDetailCounts = new int[10000],
            focusDetailSignals = new int[10000]
        };
        SetField(ws, "warningCapacity", 32); SetField(ws, "warningRecycle", new int[32]);
        SetField(ws, "warningLineOrderLinked", new bool[32]);
        SetField(ws, "tmpEntityPools", new EntityData[2][]);
        SetField(ws, "tmpPrebuildPools", new PrebuildData[2][]);
        SetField(ws, "tmpSignPools", new SignData[2][]);
        return data;
    }

    private void Toggle(bool all, bool enable) => warnings.ChangeGreenPrebuildWarning(all ? 0 : 101, enable);

    [TestMethod]
    public void BothNativeButtonsArePatchedAndSinglePlayerKeepsTheOriginalCallbacks()
    {
        var targets = DeclaredPatches(typeof(UIGreenPrebuildWarningPanel_Patch)).Select(p => p.Target?.Name).ToArray();
        CollectionAssert.AreEquivalent(new[] { "OnCurrentPlanetWarningToggle", "OnAllPlanetWarningToggle" }, targets);
        Multiplayer.Session = null;
        Check.IsTrue(UIGreenPrebuildWarningPanel_Patch.OnCurrentPlanetWarningToggle_Prefix(null!, true));
        Check.IsTrue(UIGreenPrebuildWarningPanel_Patch.OnAllPlanetWarningToggle_Prefix(null!, true));
    }

    [TestMethod]
    public void HostChangesRefreshNativeWarningsAndBroadcastSharedPreferences()
    {
        var factory = host.factories[0];
        factory.RefreshPrebuildWarning(2);
        Toggle(false, true);
        Check.IsGreaterThan(0, factory.prebuildPool[1].warningId);
        Check.AreEqual(0, host.factories[1].prebuildPool[1].warningId);
        host.warningSystem.WarningLogic(1);
        Check.AreEqual(2, host.warningSystem.warningCounts[405]);
        CollectionAssert.AreEqual(new[] { 101 }, network.Packets.OfType<GreenPrebuildWarningState>().Last().PlanetIds);
        Toggle(false, false);
        host.warningSystem.WarningLogic(2);
        Check.AreEqual(0, factory.prebuildPool[1].warningId);
        Check.IsGreaterThan(0, factory.prebuildPool[2].warningId, "Missing-item warnings must remain.");
        Check.AreEqual(1, host.warningSystem.warningCounts[405]);
    }

    [TestMethod]
    public void GlobalOverridePreservesPlanetChoicesAndAppliesToFuturePrebuilds()
    {
        warnings.ChangeGreenPrebuildWarning(101, true);
        Toggle(true, true);
        Check.IsGreaterThan(0, host.factories[1].prebuildPool[1].warningId);
        var factory = host.factories[1];
        factory.prebuildCursor = 4;
        factory.prebuildPool[3] = new PrebuildData { id = 3, protoId = 2001 };
        factory.RefreshPrebuildWarning(3);
        Check.IsGreaterThan(0, factory.prebuildPool[3].warningId);
        Toggle(true, false);
        Check.IsGreaterThan(0, host.factories[0].prebuildPool[1].warningId);
        Check.AreEqual(0, factory.prebuildPool[1].warningId);
        Check.AreEqual(0, factory.prebuildPool[3].warningId);
        Check.IsTrue(host.preferences.IsGreenPreBuildWarningPlanets(101));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ClientChangesRoundTripThroughServerWithoutEditingTheLocalWarningPool(bool all)
    {
        var client = NewWorld(); Use(client, false);
        client.warningSystem.warningPool[1] = new WarningData { id = 1, signalId = 405, state = 1 };
        client.warningSystem.warningCursor = 2;
        client.factories[0].prebuildPool[1].warningId = 1;
        Toggle(all, true);
        Check.IsFalse(client.preferences.ShouldHaveGreenPrebuildWarning(101));
        Check.AreEqual(1, client.factories[0].prebuildPool[1].warningId);
        Check.AreEqual(1, client.warningSystem.warningPool[1].state);
        var request = network.Packets.OfType<GreenPrebuildWarningRequest>().Single();
        Check.AreEqual(all ? 0 : 101, request.PlanetId);

        Use(host, true);
        ReceiveRequest(request);
        Check.IsTrue(host.preferences.ShouldHaveGreenPrebuildWarning(101));
        Check.IsGreaterThan(0, host.factories[0].prebuildPool[1].warningId);
        var state = network.Packets.OfType<GreenPrebuildWarningState>().Last();
        Use(client, false);
        var codec = new NebulaeNetPacketProcessor();
        new GreenPrebuildWarningStateProcessor().Register(codec, false);
        codec.DispatchInline(codec.Write(state), null);
        Check.IsTrue(client.preferences.ShouldHaveGreenPrebuildWarning(101));
        Check.AreEqual(all, client.preferences.ShouldHaveGreenPrebuildWarning(102));
        Check.AreEqual(1, client.factories[0].prebuildPool[1].warningId);
        Check.AreEqual(1, client.warningSystem.warningPool[1].state);

        // Another player can turn the same world setting back off.
        Use(host, true);
        ReceiveRequest(new GreenPrebuildWarningRequest { PlanetId = request.PlanetId, Enable = false });
        Check.IsFalse(host.preferences.ShouldHaveGreenPrebuildWarning(101));
        Check.AreEqual(0, host.factories[0].prebuildPool[1].warningId);
    }

    [TestMethod]
    public void SavedNativePreferencesRestoreForRejoiningClients()
    {
        warnings.ChangeGreenPrebuildWarning(101, true);
        warnings.ChangeGreenPrebuildWarning(0, true);
        using var bytes = new MemoryStream();
        host.preferences.SaveGreenPreBuildWarningPlanets(new BinaryWriter(bytes));
        bytes.Position = 0;
        host.preferences.greenPrebuildWarningPlanetIds.Clear();
        host.preferences.LoadGreenPreBuildWarningPlanets(new BinaryReader(bytes));
        var saved = RoundTrip(warnings.CaptureGreenPrebuildWarningPreferences());
        var rejoined = NewWorld(); Use(rejoined, false);
        warnings.RestoreGreenPrebuildWarningPreferences(saved);
        Check.IsTrue(rejoined.preferences.IsAllPlanetsGreenPrebuildWarningEnabled());
        Check.IsTrue(rejoined.preferences.IsGreenPreBuildWarningPlanets(101));
        Check.IsFalse(rejoined.preferences.IsGreenPreBuildWarningPlanets(102));
        Check.AreEqual(1, rejoined.warningSystem.warningCursor);
    }

    [TestMethod]
    public void InitialWarningRequestAlsoRestoresSharedSwitchesForNewConnections()
    {
        warnings.ChangeGreenPrebuildWarning(101, true);
        warnings.ChangeGreenPrebuildWarning(0, true);
        var codec = new NebulaeNetPacketProcessor();
        var sent = new List<byte[]>();
        var conn = new NebulaeConnection(new WebSocket("ws://127.0.0.1:1"), new IPEndPoint(IPAddress.Loopback, 1), codec);
        SetField(conn, "sender", new OrderedSendQueue(bytes => bytes,
            (bytes, done) => { sent.Add(bytes); done(true); }, work => work()));
        try { warnings.HandleRequest(new WarningDataRequest(WarningRequestEvent.Signal), conn); }
        finally { conn.StopSending(); }
        Check.HasCount(2, sent);
        var rejoined = NewWorld(); Use(rejoined, false);
        new GreenPrebuildWarningStateProcessor().Register(codec, false);
        new WarningSignalProcessor().Register(codec, false);
        foreach (var bytes in sent) codec.DispatchInline(bytes, null);
        Check.IsTrue(rejoined.preferences.IsAllPlanetsGreenPrebuildWarningEnabled());
        Check.IsTrue(rejoined.preferences.IsGreenPreBuildWarningPlanets(101));
        Check.IsFalse(rejoined.preferences.IsGreenPreBuildWarningPlanets(102));
    }

    [TestMethod]
    public void InvalidPlanetChangesAndSnapshotsDoNotPartiallyMutatePreferences()
    {
        warnings.ChangeGreenPrebuildWarning(-1, true);
        warnings.ChangeGreenPrebuildWarning(104, true);
        Check.IsEmpty(network.Packets);
        warnings.ChangeGreenPrebuildWarning(103, true);
        Check.IsTrue(host.preferences.ShouldHaveGreenPrebuildWarning(103));
        var client = NewWorld(); Use(client, false);
        client.preferences.greenPrebuildWarningPlanetIds.Add(101);
        warnings.RestoreGreenPrebuildWarningPreferences(new GreenPrebuildWarningState { PlanetIds = new[] { 0, -1 } });
        CollectionAssert.AreEquivalent(new[] { 101 }, client.preferences.greenPrebuildWarningPlanetIds.ToArray());
        warnings.RestoreGreenPrebuildWarningPreferences(new GreenPrebuildWarningState());
        Check.IsEmpty(client.preferences.greenPrebuildWarningPlanetIds);
    }

    private static void ReceiveRequest(GreenPrebuildWarningRequest request)
    {
        var codec = new NebulaeNetPacketProcessor();
        new GreenPrebuildWarningRequestProcessor().Register(codec, true);
        var conn = new NebulaeConnection(new WebSocket("ws://127.0.0.1:1"), new IPEndPoint(IPAddress.Loopback, 1), codec);
        try { conn.CompleteHandshake(); codec.DispatchInline(codec.Write(request), conn); }
        finally { conn.StopSending(); }
    }

    private static bool GetModels(ref ModelProtoSet __result) { __result = models; return false; }
    private static IEnumerable<CodeInstruction> SkipUI() => new[] { new CodeInstruction(OpCodes.Ret) };


}
