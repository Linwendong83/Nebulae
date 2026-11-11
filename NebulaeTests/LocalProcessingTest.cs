using System.Reflection;
using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Logistics.ControlPanel;
using NebulaePatcher.Patches.Transpilers;
using NebulaeWorld;
using NebulaeWorld.Factory;
using NebulaeWorld.Logistics;
using NebulaeWorld.Planet;
using NebulaeWorld.Statistics;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class LocalProcessingTest
{
    private MultiplayerSession? previous;
    private RecordingBroadcastNetwork network = null!;

    [TestInitialize]
    public void Initialize()
    {
        previous = Multiplayer.Session;
        var session = Uninitialized<MultiplayerSession>();
        network = new RecordingBroadcastNetwork(); session.Network = network;
        session.Client = Uninitialized<global::NebulaeNetwork.Client>();
        session.Planets = new PlanetManager();
        session.Statistics = Uninitialized<StatisticsManager>();
        AccessTools.Field(typeof(StatisticsManager), "<LocalReferences>k__BackingField").SetValue(session.Statistics, new LocalReferenceStatistics());
        Multiplayer.Session = session;
    }

    [TestCleanup]
    public void Cleanup() => Multiplayer.Session = previous;

    [TestMethod]
    public void LocalFilterRespectsStationModeScopeNamesAndMarkerTags()
    {
        var entry = new FacilityDirectoryEntry
        {
            PlanetId = 101,
            EntityId = 9,
            Id = 4,
            Name = "Iron NORTH",
            Type = EControlPanelEntryType.InterstellarStation,
            Items = new[] { 1001 },
            LocalModes = new[] { 2 },
            RemoteModes = new[] { 1 }
        };
        var filter = ControlPanelFilter.Default; filter.searchFilter = "north"; filter.itemsFilter = new[] { 1001 }; filter.stateFilter = 1;
        TestAssert.IsTrue(entry.Matches(filter));
        filter.stateFilter = 2; TestAssert.IsFalse(entry.Matches(filter));
        filter.typeFilter |= ControlPanelFilter.EEntryFilter.LocalStation; TestAssert.IsTrue(entry.Matches(filter));
        filter.searchFilter = "4"; TestAssert.IsTrue(entry.Matches(filter));
        filter.searchFilter = "5"; TestAssert.IsFalse(entry.Matches(filter));
        entry.Type = EControlPanelEntryType.Marker; entry.Tags = "iron;north"; entry.Comment = "reserve";
        filter.typeFilter = ControlPanelFilter.EEntryFilter.Marker; filter.tagFilter = "north;oil"; filter.searchFilter = "RESERVE";
        TestAssert.IsTrue(entry.Matches(filter));
        filter.tagFilter = "oil"; TestAssert.IsFalse(entry.Matches(filter));
    }

    [TestMethod]
    public void DirectoryRoundTripKeepsConfigurationAndRejectsMalformedItemCounts()
    {
        var entry = new FacilityDirectoryEntry
        {
            PlanetId = 203,
            EntityId = 17,
            Id = 3,
            GId = 20,
            ProtoId = 2104,
            Type = EControlPanelEntryType.InterstellarStation,
            Name = "North",
            Items = new[] { 1001, 1002 },
            LocalModes = new[] { 1, 2 },
            RemoteModes = new[] { 2, 1 }
        };
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        entry.Write(writer); stream.Position = 0;
        var decoded = FacilityDirectoryEntry.Read(new BinaryReader(stream));
        TestAssert.AreEqual(20, decoded.GId); TestAssert.AreEqual("North", decoded.Name);
        CollectionAssert.AreEqual(entry.Items, decoded.Items); CollectionAssert.AreEqual(entry.RemoteModes, decoded.RemoteModes);
        stream.Position = stream.Length - 28; writer.Write(-1); stream.Position = 0;
        TestAssert.Throws<InvalidDataException>(() => FacilityDirectoryEntry.Read(new BinaryReader(stream)));
    }

    [TestMethod]
    public void VisibleRowSubscriptionOwnsReusedArraysAndOnlyReplacesCompleteSubscriptions()
    {
        var directory = new FacilityDirectoryManager(); var connection = new RecordingConnection();
        var sent = new List<LCPObjectEntryRequest>(); directory.SendRow = (_, request) => sent.Add(request);
        var packet = new FacilityRowsSubscription { Generation = 2, Indices = new[] { 8 }, Planets = new[] { 203 }, Entities = new[] { 19 }, Types = new short[] { 1 } };
        directory.SetRows(connection, packet); packet.Planets[0] = 999; packet.Entities[0] = 50; packet.Generation = 3;
        directory.FlushRows();
        TestAssert.AreEqual(203, sent[1].AstroId); TestAssert.AreEqual(-19, sent[1].ObjId); TestAssert.AreEqual(2, sent[1].Generation);
        directory.SetRows(connection, new FacilityRowsSubscription { Indices = new[] { 1 }, Planets = Array.Empty<int>() });
        directory.FlushRows(); TestAssert.AreEqual(203, sent[2].AstroId);
        directory.SetRows(connection, new FacilityRowsSubscription { Indices = Array.Empty<int>(), Planets = Array.Empty<int>(), Entities = Array.Empty<int>(), Types = Array.Empty<short>() });
        var count = sent.Count; directory.FlushRows(); TestAssert.HasCount(count, sent);
    }

    [TestMethod]
    public void UnchangedRowValuesEmitNothingAndReconnectReceivesANewBaseline()
    {
        var directory = new FacilityDirectoryManager(); var connection = new RecordingConnection();
        var packet = new LCPObjectEntryEntityInfo { Index = 2, ProtoId = 2104, Id = 1, Name = "North", Generation = 3, AstroId = 203, ObjId = 19 };
        directory.SendChanged(connection, packet, 2); directory.SendChanged(connection, packet, 2);
        TestAssert.HasCount(1, connection.Raw);
        packet.Name = "South"; directory.SendChanged(connection, packet, 2); TestAssert.HasCount(2, connection.Raw);
        directory.Remove(connection); directory.SendChanged(connection, packet, 2); TestAssert.HasCount(3, connection.Raw);
    }

    [TestMethod]
    public void DirectoryConsumersShareOneSubscriptionAndCloseTheLastSubscription()
    {
        var directory = new FacilityDirectoryManager(); var a = new object(); var b = new object();
        directory.Subscribe(a); directory.Subscribe(a); directory.Subscribe(b); directory.Unsubscribe(a);
        TestAssert.HasCount(1, network.Packets);
        directory.Unsubscribe(b); TestAssert.HasCount(2, network.Packets);
        TestAssert.IsTrue(((FacilityDirectoryRequest)network.Packets[0]).Subscribe);
        TestAssert.IsFalse(((FacilityDirectoryRequest)network.Packets[1]).Subscribe);
    }

    [TestMethod]
    public void ClientReferenceCalculationNeverAddressesTheHostsUnrelatedIndex()
    {
        var factory = Uninitialized<PlanetFactory>();
        AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, new PlanetData { id = 203 });
        AccessTools.Field(typeof(PlanetFactory), "<index>k__BackingField").SetValue(factory, 0);
        var authoritative = new FactoryProductionStat(); authoritative.Init(); authoritative.AddRefProductSpeed(1001, 7);
        var local = LocalReferenceStatistics.ResolveStat(new[] { authoritative }, factory);
        TestAssert.AreNotSame(authoritative, local);
        local.AddRefProductSpeed(1001, 20);
        TestAssert.AreEqual(7f, authoritative.productPool[authoritative.productIndices[1001]].refProductSpeed);
        TestAssert.AreSame(local, LocalReferenceStatistics.ResolveStat(Array.Empty<FactoryProductionStat>(), factory));
    }

    [TestMethod]
    public void BothNativeReferenceCalculationMethodsInstallTheScratchPoolLookup()
    {
        var harmony = new Harmony("nebulae.tests.local-reference-pools");
        try
        {
            harmony.CreateClassProcessor(typeof(LocalReferenceStatistics_Transpiler)).Patch();
            foreach (var (type, name) in new[] { (typeof(UIReferenceSpeedTip), nameof(UIReferenceSpeedTip.AddEntryDataWithFactory)),
                         (typeof(ProductionExtraInfoCalculator), nameof(ProductionExtraInfoCalculator.CalculateFactory)) })
                TestAssert.IsTrue(Harmony.GetPatchInfo(AccessTools.Method(type, name)).Transpilers.Any(p => p.owner == harmony.Id));
        }
        finally { harmony.UnpatchSelf(); }
    }

    [TestMethod]
    public void ModifiedUiAndLoadingPatchesBindTheirNativeArguments()
    {
        // The game's UI bodies reference Unity/Mono-only BCL overloads. Validate Harmony's
        // resolved methods and injected argument types without JITing those bodies on CLR.
        var harmony = new Harmony("nebulae.tests.local-ui-binding");
        var patchTools = typeof(Harmony).Assembly.GetType("HarmonyLib.PatchTools", true)!;
        var resolve = AccessTools.Method(patchTools, "GetOriginalMethod");
        foreach (var name in new[] { "UIControlPanelWindow_Patch", "UIControlPanelStationEntry_Patch", "UIControlPanelAdvancedMinerEntry_Patch",
                     "UIControlPanelDispenserEntry_Patch", "UIStationRoutePanel_Patch", "UIStorageWindow_Patch", "UIStorageRefreshGuard_Patch",
                     "UIStorageRefreshTransfer_Patch", "UIReferenceSpeedTip_Patch", "UIStatisticsWindow_Patch", "UIProductEntry_Patch",
                     "UIDashboard_Patch", "UIDEOverview_Patch", "PlanetModelingManager_Patch" })
        {
            var type = Array.Find(typeof(NebulaePatcher.NebulaePlugin).Assembly.GetTypes(), t => t.Name == name)!;
            var processor = harmony.CreateClassProcessor(type);
            var dynamicTargets = AccessTools.DeclaredMethod(type, "TargetMethods");
            foreach (var patch in (System.Collections.IEnumerable)AccessTools.Field(typeof(PatchClassProcessor), "patchMethods").GetValue(processor))
            {
                var info = (HarmonyMethod)AccessTools.Field(patch.GetType(), "info").GetValue(patch);
                var targets = dynamicTargets != null ? (IEnumerable<MethodBase>)dynamicTargets.Invoke(null, null) :
                    new[] { (MethodBase)resolve.Invoke(null, new object[] { info }) };
                foreach (var target in targets)
                {
                    TestAssert.IsNotNull(target, name);
                    var instance = info.method.GetParameters().FirstOrDefault(p => p.Name == "__instance");
                    if (instance != null) TestAssert.IsTrue(instance.ParameterType.IsAssignableFrom(target.DeclaringType), name + "." + info.method.Name);
                }
            }
        }
    }

    [TestMethod]
    public void RuntimeOverlayIsInstalledOnlyAtTheModelingBoundaryAndDuplicateRepliesAreIgnored()
    {
        var manager = new PlanetManager(); var planet = new PlanetData { id = 101, precision = 80, wanted = true, loading = true, data = new PlanetRawData(80) };
        var baseline = new byte[81 * 81 * 2]; planet.modData = baseline; planet.data.modData = baseline;
        manager.BeginTerrainLoad(101); TestAssert.IsFalse(manager.PrepareModeling(planet));
        var host = new PlanetData { id = 101, precision = 80, modData = (byte[])baseline.Clone() }; host.modData[17] = 31;
        manager.ReceiveRuntime(planet, PlanetRuntimeSnapshot.Export(host));
        TestAssert.AreSame(baseline, planet.data.modData); TestAssert.AreEqual((byte)0, planet.modData[17]);
        host.modData[17] = 1; manager.ReceiveRuntime(planet, PlanetRuntimeSnapshot.Export(host));
        TestAssert.IsTrue(manager.PrepareModeling(planet)); TestAssert.AreEqual((byte)31, planet.modData[17]);
        TestAssert.AreSame(planet.modData, planet.data.modData);
    }

    [TestMethod]
    public void UntouchedPlanetDecisionsRequireTheHostFactoryManifest()
    {
        var manager = Multiplayer.Session.Planets;
        TestAssert.IsFalse(manager.IsPristine(new PlanetData { id = 203 }));
        using var writer = new BinaryUtils.Writer(); writer.BinaryWriter.Write(1); writer.BinaryWriter.Write(101);
        manager.ImportFactoryPresence(writer.CloseAndGetBytes());
        TestAssert.IsFalse(manager.IsPristine(new PlanetData { id = 101 }));
        TestAssert.IsTrue(manager.IsPristine(new PlanetData { id = 203 }));
    }

    [TestMethod]
    public void SwitchingStorageRejectsLateSnapshotsFromThePreviousRefresh()
    {
        var storage = new StorageManager(); var first = storage.BeginRefresh(); var second = storage.BeginRefresh();
        TestAssert.IsFalse(storage.AcceptRefresh(first)); TestAssert.IsTrue(storage.AcceptRefresh(second));
    }

    [TestMethod]
    public void CachedStorageIsReadOnlyUntilItsBaselineArrivesIncludingTransfersFromAnotherWindow()
    {
        var grid = Uninitialized<UIStorageGrid>(); var package = Uninitialized<UIStorageGrid>();
        Multiplayer.Session.Storage = new StorageManager { ActiveUIStorageGrid = grid, WindowOpened = true };
        Multiplayer.Session.Storage.BeginRefresh();
        TestAssert.IsFalse(NebulaePatcher.Patches.Dynamic.UI.UIStorageRefreshGuard_Patch.Prefix(grid));
        TestAssert.IsTrue(NebulaePatcher.Patches.Dynamic.UI.UIStorageRefreshGuard_Patch.Prefix(package));
        TestAssert.IsFalse(NebulaePatcher.Patches.Dynamic.UI.UIStorageRefreshTransfer_Patch.Prefix(package, grid));
        Multiplayer.Session.Storage.RefreshPending = false;
        TestAssert.IsTrue(NebulaePatcher.Patches.Dynamic.UI.UIStorageRefreshTransfer_Patch.Prefix(package, grid));
    }

    [TestMethod]
    public void PersonalTutorialAcknowledgementsStayLocalWhileSharedFeatureChangesSendOnlyOnce()
    {
        Multiplayer.Session.History = new NebulaeWorld.GameDataHistory.GameDataHistoryManager();
        var history = new GameHistoryData { featureKeys = new HashSet<int>(), tutorialUnlocked = new HashSet<int>() };
        var harmony = new Harmony("nebulae.tests.local-tutorial-acknowledgements");
        var type = typeof(NebulaePatcher.Patches.Dynamic.Game.GameHistoryData_Patch);
        var keys = (HashSet<int>)AccessTools.Field(typeof(NebulaeWorld.GameStates.GameStatesManager), "preservedFeatureKeys").GetValue(null);
        var tutorials = (HashSet<int>)AccessTools.Field(typeof(NebulaeWorld.GameStates.GameStatesManager), "preservedTutorialUnlocked").GetValue(null);
        var savedKeys = keys.ToArray(); var savedTutorials = tutorials.ToArray();
        try
        {
            foreach (var name in new[] { "RegFeatureKey", "UnlockTutorial" })
                harmony.Patch(AccessTools.Method(typeof(GameHistoryData), name),
                    prefix: new HarmonyMethod(AccessTools.Method(type, name + "_Prefix")),
                    postfix: new HarmonyMethod(AccessTools.Method(type, name + "_Postfix")));
            // Invoke after patch installation so CLR cannot inline the tiny native setters
            // into this test before the detours exist.
            var register = AccessTools.Method(typeof(GameHistoryData), nameof(GameHistoryData.RegFeatureKey));
            var unlock = AccessTools.Method(typeof(GameHistoryData), nameof(GameHistoryData.UnlockTutorial));
            register.Invoke(history, new object[] { FeatureID.ADVISOR_TIP_START + 7 }); unlock.Invoke(history, new object[] { 8 });
            TestAssert.Contains(FeatureID.ADVISOR_TIP_START + 7, history.featureKeys);
            TestAssert.IsTrue(history.TutorialUnlocked(8)); TestAssert.IsEmpty(network.Packets);
            register.Invoke(history, new object[] { 1100002 }); register.Invoke(history, new object[] { 1100002 });
            TestAssert.HasCount(1, network.Packets);
        }
        finally
        {
            harmony.UnpatchSelf(); keys.Clear(); keys.UnionWith(savedKeys); tutorials.Clear(); tutorials.UnionWith(savedTutorials);
        }
    }

    private sealed class RecordingConnection : INebulaeConnection
    {
        public List<byte[]> Raw { get; } = new();
        public bool IsAlive => true;
        public int Id => 1;
        public EConnectionStatus ConnectionStatus { get; set; }
        public void SendPacket<T>(T packet) where T : class, new() { }
        public void SendRawPacket(byte[] rawData) => Raw.Add((byte[])rawData.Clone());
        public bool Equals(INebulaeConnection? other) => ReferenceEquals(this, other);
    }
}
