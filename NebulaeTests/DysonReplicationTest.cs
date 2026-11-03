using System.Reflection;
using System.Reflection.Emit;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeModel.Contracts;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Universe;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeNetwork.PacketProcessors.Universe;
using NebulaeWorld;
using NebulaeWorld.Universe;
using UnityEngine;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
[SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Native boundary adapters retain the original call signatures.")]
public class DysonReplicationTest
{
    private MultiplayerSession? previousSession;
    private GameMain? previousMain;
    private GameData? previousData;
    private Harmony harmony = null!;
    private RecordingNetwork network = null!;
    private static readonly List<DysonSphere> released = [];
    private static bool failImport;
    private static int loadingPopups;
    private static Action? cancelLoading;

    [TestInitialize]
    public void Initialize()
    {
        previousSession = Multiplayer.Session; previousMain = GameMain.instance; previousData = GameMain.data;
        var main = Uninitialized<GameMain>(); SetField(main, "m_CachedPtr", new IntPtr(1));
        SetStatic(typeof(GameMain), "<instance>k__BackingField", main);
        GameMain.data = new GameData
        {
            galaxy = new GalaxyData { starCount = 3, stars = new[] { new StarData { index = 0 }, new StarData { index = 1 }, new StarData { index = 2 } } },
            dysonSpheres = new DysonSphere[3],
            factories = Array.Empty<PlanetFactory>(),
            history = new GameHistoryData(),
            gameDesc = new GameDesc(),
            statistics = new GameStatData { production = new ProductionStatistics { factoryStatPool = new[] { new FactoryProductionStat() } } }
        };
        network = new RecordingNetwork();
        Multiplayer.Session = Uninitialized<MultiplayerSession>();
        Multiplayer.Session.Network = network;
        Multiplayer.Session.LocalPlayer = new LocalPlayer { IsHost = false };
        Multiplayer.Session.DysonSpheres = new DysonSphereManager();
        Multiplayer.Session.Launch = new LaunchManager();
        released.Clear(); failImport = false; loadingPopups = 0; cancelLoading = null;
        harmony = new Harmony("nebulae.tests.dyson-replication");
        harmony.Patch(AccessTools.Method(typeof(InGamePopup), nameof(InGamePopup.ShowInfo)),
            prefix: new HarmonyMethod(typeof(DysonReplicationTest), nameof(RecordLoading)));
        harmony.Patch(AccessTools.Method(typeof(DysonSphereManager), "ReleaseSphere"),
            prefix: new HarmonyMethod(typeof(DysonReplicationTest), nameof(Release)));
        Harmony.ReversePatch(AccessTools.Method(typeof(DysonSphereDataProcessor), "ImportSnapshot"),
            new HarmonyMethod(typeof(DysonReplicationTest), nameof(ImportSnapshot)),
            AccessTools.Method(typeof(DysonReplicationTest), nameof(WithoutUnity)), null);
    }

    [TestCleanup]
    public void Cleanup()
    {
        harmony.UnpatchSelf(); Multiplayer.Session = previousSession; GameMain.data = previousData;
        SetStatic(typeof(GameMain), "<instance>k__BackingField", previousMain);
        released.Clear();
    }

    private static bool Release(DysonSphere sphere) { if (sphere != null) released.Add(sphere); return false; }
    private static bool RecordLoading(Action resp1) { loadingPopups++; cancelLoading = resp1; return false; }
    private DysonSphereManager Manager => Multiplayer.Session.DysonSpheres;
    private int Request(int star, bool force = false)
    {
        Manager.RequestDysonSphere(star, false, force: force);
        return network.Packets.OfType<DysonSphereLoadRequest>().Last(p => p.StarIndex == star).RequestId;
    }

    [TestMethod]
    public void OpeningBeforeTheBaselineKeepsTheEditorInactiveAndShowsOneLoadingNotice()
    {
        var editor = Uninitialized<UIDysonEditor>();
        SetField(editor, "<inited>k__BackingField", true);
        for (var frame = 0; frame < 120; frame++)
            Check.IsFalse(UIDysonEditor_Patch.Open_Prefix(editor));
        Check.IsFalse(editor.active);
        Check.AreEqual(1, loadingPopups);
        var load = network.Packets.OfType<DysonSphereLoadRequest>().Single();
        Check.IsTrue(Manager.Commit(0, load.RequestId, new DysonSphere { starData = GameMain.galaxy.stars[0] }, 10));
        Check.IsTrue(Manager.CompleteEditorRequest(0, out var open)); Check.IsTrue(open);
        Check.IsFalse(Manager.CompleteEditorRequest(0, out _));
        Check.IsTrue(UIDysonEditor_Patch.Open_Prefix(editor));
        Check.AreEqual(1, network.Packets.OfType<DysonSphereLoadRequest>().Count(p => p.Event == DysonSphereRequestEvent.List));
    }

    [TestMethod]
    public void CancelledEditorLoadingDoesNotReopenWhenTheBackgroundSnapshotArrives()
    {
        Manager.WaitForEditor(0, true);
        var load = network.Packets.OfType<DysonSphereLoadRequest>().Single();
        cancelLoading!();
        Check.IsTrue(Manager.IsRequesting(0), "Cancel the UI intent, not the shared background snapshot.");
        Check.IsTrue(Manager.Commit(0, load.RequestId, new DysonSphere(), 10));
        Check.IsFalse(Manager.CompleteEditorRequest(0, out _));
    }

    [TestMethod]
    public void RepeatedEditorSelectionRequestsDoNotStackLoadingNotices()
    {
        for (var frame = 0; frame < 120; frame++)
            Manager.WaitForEditor(1, false);
        Check.AreEqual(1, loadingPopups);
        var load = network.Packets.OfType<DysonSphereLoadRequest>().Single();
        Manager.Commit(1, load.RequestId, new DysonSphere(), 10);
        Check.IsTrue(Manager.CompleteEditorRequest(1, out var open)); Check.IsFalse(open);
    }

    [TestMethod]
    public void StaleFailureAndAnotherStarsResponseCannotConsumeTheCurrentEditorIntent()
    {
        Manager.WaitForEditor(0, true);
        var old = network.Packets.OfType<DysonSphereLoadRequest>().Single().RequestId;
        var current = Request(0, true);
        Manager.FailRequest(0, old);
        var other = Request(1);
        Manager.Commit(1, other, new DysonSphere(), 10);
        Check.IsFalse(Manager.CompleteEditorRequest(1, out _));
        Check.IsFalse(Manager.ClearLoadingNotice(1));
        Manager.Commit(0, current, new DysonSphere(), 11);
        Check.IsTrue(Manager.CompleteEditorRequest(0, out var open)); Check.IsTrue(open);
        Check.IsTrue(Manager.ClearLoadingNotice(0));
        Check.IsFalse(Manager.ClearLoadingNotice(0));
        Manager.WaitForEditor(2, true);
        var failed = network.Packets.OfType<DysonSphereLoadRequest>().Last().RequestId;
        Manager.FailRequest(2, failed);
        var retry = Request(2);
        Manager.Commit(2, retry, new DysonSphere(), 12);
        Check.IsFalse(Manager.CompleteEditorRequest(2, out _));
    }

    [TestMethod]
    public void ConcurrentStarsGetSeparateRequestsAndStaleResponsesCannotReplaceANewerLoad()
    {
        var first = Request(0); var second = Request(1);
        Request(0); Check.HasCount(2, network.Packets);
        Check.IsTrue(Manager.Expects(0, first)); Check.IsTrue(Manager.Expects(1, second));
        var newer = Request(0, true);
        var old = new DysonSphere(); GameMain.data.dysonSpheres[0] = old;
        Check.IsFalse(Manager.Commit(0, first, new DysonSphere(), 10));
        Check.AreSame(old, GameMain.data.dysonSpheres[0]);
        Check.IsTrue(Manager.Commit(0, newer, new DysonSphere(), 20));
        Check.IsTrue(Manager.IsReady(0)); Check.IsTrue(Manager.IsRequesting(1));
        Check.HasCount(1, released); Check.AreSame(old, released[0]);
    }

    [TestMethod]
    public void MenuDemoCannotCreateARemoteSphereRequest()
    {
        GameMain.instance.isMenuDemo = true;
        Manager.RequestDysonSphere(0, false);
        Check.IsFalse(Manager.IsRequesting(0));
        Check.HasCount(0, network.Packets);
        GameMain.data.dysonSpheres[0] = new DysonSphere();
        DysonSphere result = null!;
        Check.IsFalse(GameData_Patch.CreateDysonSphere_Prefix(GameMain.data, 0, ref result));
        Check.AreSame(GameMain.data.dysonSpheres[0], result);
        Check.HasCount(0, network.Packets);
        GameMain.instance.isMenuDemo = false;
        Request(0);
        Check.IsTrue(Manager.IsRequesting(0));
    }

    [TestMethod]
    public void APreviousWorldRequestCannotSuppressTheNewWorldBaseline()
    {
        var previous = Request(0);
        GameMain.data = new GameData
        {
            galaxy = GameMain.data.galaxy,
            dysonSpheres = new[] { new DysonSphere(), new DysonSphere(), new DysonSphere() }
        };
        DysonSphere result = null!;
        GameData_Patch.CreateDysonSphere_Prefix(GameMain.data, 0, ref result);
        var current = network.Packets.OfType<DysonSphereLoadRequest>().Last().RequestId;
        Check.AreNotEqual(previous, current);
        Check.IsFalse(Manager.Expects(0, previous));
        Check.IsTrue(Manager.Expects(0, current));
        Check.IsFalse(Manager.Commit(0, previous, new DysonSphere(), 10));
    }

    [TestMethod]
    public void APreviousWorldReadyFlagCannotValidateANewDummy()
    {
        var request = Request(0);
        Manager.Commit(0, request, new DysonSphere(), 10);
        GameMain.data = new GameData { galaxy = GameMain.data.galaxy, dysonSpheres = new DysonSphere[3] };
        Check.IsFalse(Manager.IsReady(0));
    }

    [TestMethod]
    public void AResponseArrivingBeforeTheWorldExistsDoesNotLeaveAPermanentRequest()
    {
        var request = Request(0);
        var world = GameMain.data;
        GameMain.data = null;
        AccessTools.Method(typeof(DysonSphereDataProcessor), "ProcessPacket").Invoke(
            new DysonSphereDataProcessor(), new object?[] { new DysonSphereData(0, Array.Empty<byte>(), DysonSphereRespondEvent.Load, request), null });
        GameMain.data = world;
        Check.IsFalse(Manager.IsRequesting(0));
        Check.AreNotEqual(request, Request(0));
    }

    [TestMethod]
    public void EveryUnreadyDummyRequestsItsOwnBaseline()
    {
        GameMain.data.dysonSpheres[0] = new DysonSphere(); GameMain.data.dysonSpheres[1] = new DysonSphere();
        DysonSphere result = null!;
        GameData_Patch.CreateDysonSphere_Prefix(GameMain.data, 0, ref result);
        GameData_Patch.CreateDysonSphere_Prefix(GameMain.data, 1, ref result);
        Check.HasCount(2, network.Packets);
        Check.IsTrue(Manager.IsRequesting(0)); Check.IsTrue(Manager.IsRequesting(1));
    }

    [TestMethod]
    public void UnloadReleasesResourcesAndInvalidatesOutstandingQueries()
    {
        var id = Request(1); var old = new DysonSphere(); GameMain.data.dysonSpheres[1] = old;
        Manager.UnloadRemoteDysonSpheres();
        Check.IsNull(GameMain.data.dysonSpheres[1]); Check.IsFalse(Manager.Expects(1, id));
        Check.Contains(old, released);
        var retry = Request(1); Check.AreNotEqual(id, retry);
    }

    [TestMethod]
    public void SnapshotCommitsOnlyAfterValidationAndKeepsExistingProductionRegisters()
    {
        var id = Request(0); var old = new DysonSphere(); GameMain.data.dysonSpheres[0] = old;
        var stats = GameMain.data.statistics.production.factoryStatPool;
        using var writer = new BinaryUtils.Writer(); writer.BinaryWriter.Write(3);
        var payload = writer.CloseAndGetBytes();
        failImport = true;
        Check.Throws<InvalidDataException>(() => ImportSnapshot(new DysonSphereData(0, payload, DysonSphereRespondEvent.Load, id, 5)));
        Check.AreSame(old, GameMain.data.dysonSpheres[0]); Check.AreSame(stats, GameMain.data.statistics.production.factoryStatPool);
        Check.IsFalse(Manager.InBlueprint); Check.IsFalse(Manager.IsRequesting(0));
        Check.DoesNotContain(old, released);
        failImport = false; id = Request(0);
        ImportSnapshot(new DysonSphereData(0, payload, DysonSphereRespondEvent.Load, id, 6));
        Check.AreNotSame(old, GameMain.data.dysonSpheres[0]); Check.Contains(old, released);
        Check.AreSame(stats, GameMain.data.statistics.production.factoryStatPool);
        Check.IsTrue(Manager.IsReady(0));
    }

    [TestMethod]
    public void SailAndColorBatchesRespectBothTheirDomainAndWireLimits()
    {
        var sail = new DysonSail { gs = 1 };
        for (var i = 0; i < 600; i++) Manager.RegisterSolarSail(i % 2, ref sail, i % 2 + 1, 1000);
        for (var i = 0; i < 5000; i++) Manager.RegisterColorChange(0, 1, 0, i + 1, (uint)i);
        Manager.RegisterColorChange(1, 2, 0, 17, 123);
        Manager.GameTick();
        var sails = network.Packets.OfType<DysonSailBatchPacket>().ToArray(); Check.HasCount(4, sails);
        Check.AreEqual(600, sails.Sum(p => p.OrbitId.Length)); Check.IsTrue(sails.All(p => p.IsConsistent()));
        Check.IsTrue(sails.All(p => p.OrbitId.All(orbit => orbit == p.StarIndex + 1)));
        var colors = network.Packets.OfType<DysonSphereColorChangeBatchPacket>().ToArray(); Check.HasCount(3, colors);
        Check.AreEqual(5001, colors.Sum(p => p.Index.Length)); Check.IsTrue(colors.All(p => p.IsConsistent()));
        Check.IsTrue(colors.All(p => p.LayerId == p.StarIndex + 1));
    }

    [TestMethod]
    public void ColorDeduplicationEndsWithTheStrokeAndRemoteEditsInvalidateIt()
    {
        Manager.RegisterColorChange(0, 1, 0, 1, 12); Manager.GameTick();
        Manager.RegisterColorChange(0, 1, 0, 1, 12); Manager.GameTick(); Check.HasCount(1, network.Packets);
        Manager.InvalidateColor(0, 1, 0, 1);
        Manager.RegisterColorChange(0, 1, 0, 1, 12); Manager.GameTick(); Check.HasCount(2, network.Packets);
        Manager.GameTick();
        Manager.RegisterColorChange(0, 1, 0, 1, 12); Manager.GameTick(); Check.HasCount(3, network.Packets);
    }

    [TestMethod]
    public void SameColorOnDifferentOrbitsIsNotSuppressedAndCompactionIsNeverBroadcast()
    {
        var sphere = new DysonSphere { starData = GameMain.galaxy.stars[0] };
        var swarm = new DysonSwarm(sphere) { orbitColorsHSVA = new Vector4[32] };
        var color = new Vector4(.1f, .2f, .3f, 1);
        DysonSwarm_Patch.SetOrbitColor_Prefix(swarm, 1, color);
        DysonSwarm_Patch.SetOrbitColor_Prefix(swarm, 2, color);
        Check.HasCount(2, network.Packets);
        GameMain.gameTick = 21600;
        DysonSwarm_Patch.RemoveSailsByOrbit_Prefix(swarm, 0); Check.HasCount(2, network.Packets);
        DysonSwarm_Patch.RemoveSailsByOrbit_Prefix(swarm, 1); Check.HasCount(3, network.Packets);
    }

    [TestMethod]
    public void LaunchBatchesAppendUnfinishedEventsAndDiscardEverythingIncludedInTheSnapshot()
    {
        var launches = Multiplayer.Session.Launch;
        launches.SetBaseline(0, 100);
        var first = new DysonLaunchData(0) { BulletList = [new() { PlanetId = 101, TargetId = 1, Tick = 101 }] };
        launches.ImportPacket(new DysonLaunchDataPacket(first));
        launches.ImportPacket(new DysonLaunchDataPacket(new DysonLaunchData(0)
        { BulletList = [new() { PlanetId = 101, TargetId = 2, Tick = 102 }] }));
        Check.HasCount(2, launches.Snapshots[0].BulletList);
        launches.SetBaseline(0, 101); Check.HasCount(1, launches.Snapshots[0].BulletList);
        Check.AreEqual(102L, launches.Snapshots[0].BulletList[0].Tick);
        launches.ImportPacket(new DysonLaunchDataPacket(first)); Check.HasCount(1, launches.Snapshots[0].BulletList);
        launches.Unregister(0);
        launches.ImportPacket(new DysonLaunchDataPacket(first)); Check.IsTrue(launches.Snapshots.IsEmpty);
    }

    [TestMethod]
    public void LaunchWireKeepsLongGapsAndLargeNodeIdentifiersAndRejectsOversizedBatches()
    {
        var sent = new DysonLaunchData(1)
        { RocketList = [new() { PlanetId = 203, LayerId = 10, TargetId = 70000, Tick = 10000000000L }] };
        var received = RoundTrip(new DysonLaunchDataPacket(sent), PacketUtils.RegisterNestedTypes).Data;
        Check.AreEqual(70000, received.RocketList[0].TargetId); Check.AreEqual(10, received.RocketList[0].LayerId);
        Check.AreEqual(10000000000L, received.RocketList[0].Tick); Check.AreEqual(203, received.RocketList[0].PlanetId);
        sent.BulletList = Enumerable.Repeat(default(DysonLaunchData.Projectile), DysonLaunchData.MaxProjectiles + 1).ToList();
        Check.Throws<ArgumentOutOfRangeException>(() => sent.Serialize(new NetDataWriter()));
    }

    [TestMethod]
    public void HostSplitsAnOversizedLaunchWindowIntoReadablePacketsWithoutLosingEvents()
    {
        var server = new RecordingServer(); Multiplayer.Session.Server = server;
        PacketUtils.RegisterNestedTypes((NebulaeNetPacketProcessor)server.PacketProcessor);
        var connection = new RecordingConnection();
        var threads = AccessTools.Field(typeof(DysonSphereManager), "threadSafe").GetValue(Manager);
        var subscribers = (Dictionary<int, List<INebulaeConnection>>)AccessTools.Field(threads.GetType(), "Subscribers").GetValue(threads);
        subscribers[0] = [connection];
        var count = DysonLaunchData.MaxProjectiles * 2 + 1;
        Multiplayer.Session.Launch.Snapshots[0] = new DysonLaunchData(0)
        { BulletList = Enumerable.Range(0, count).Select(i => new DysonLaunchData.Projectile { PlanetId = 101, Tick = i, TargetId = 1 }).ToList() };
        Multiplayer.Session.Launch.SendBroadcastIfNeeded();
        Check.HasCount(3, connection.Raw);
        var processor = new NebulaeNetPacketProcessor(); PacketUtils.RegisterNestedTypes(processor);
        var restored = new List<DysonLaunchData.Projectile>();
        processor.SubscribeReusable<DysonLaunchDataPacket>(packet => restored.AddRange(packet.Data.BulletList));
        foreach (var raw in connection.Raw) processor.ReadPacket(new NetDataReader(raw));
        Check.HasCount(count, restored);
        Check.AreEqual(count - 1L, restored.Last().Tick);
        Check.IsEmpty(Multiplayer.Session.Launch.Snapshots[0].BulletList);
    }

    [TestMethod]
    public void InvalidSailBatchesAreAtomicAndCannotAddSailsInANormalWorld()
    {
        var sphere = new DysonSphere { starData = GameMain.galaxy.stars[0] };
        sphere.swarm = new DysonSwarm(sphere) { orbitCursor = 2, orbits = new SailOrbit[32] };
        sphere.swarm.orbits[1] = new SailOrbit { id = 1 };
        GameMain.data.dysonSpheres[0] = sphere;
        var packet = new DysonSailBatchPacket(0, new[] { 1, 1 }, new long[] { 1000, 1000 },
            new float[2], new[] { 0f, float.NaN }, new float[2], new float[2],
            new float[2], new float[2], new float[2], new[] { 1f, 1f });
        var processor = new SailProcessor();
        processor.Apply(packet, false); Check.AreEqual(0, sphere.swarm.sailCount);
        packet.StarIndex = -1;
        processor.Apply(packet, false); Check.AreEqual(0, sphere.swarm.sailCount);
        packet.StarIndex = 0; packet.px[1] = 0;
        processor.Apply(packet, true); Check.AreEqual(0, sphere.swarm.sailCount);
    }

    [TestMethod]
    public void HostPowerTotalsSurviveLocalReplicaTicksAndRoundTripWithStatus()
    {
        var sphere = new DysonSphere
        {
            starData = GameMain.galaxy.stars[0],
            energyReqCurrentTick = 7,
            energyGenCurrentTick = 1234,
            energyGenOriginalCurrentTick = 2468,
            grossRadius = 8000
        };
        sphere.swarm = new DysonSwarm(sphere) { sailCursor = 0, energyGenCurrentTick = 456 };
        // Avoid the hive getter; it needs the complete runtime universe.
        var status = RoundTrip(new DysonSphereStatusPacket
        {
            StarIndex = 0,
            EnergyReqCurrentTick = 7,
            EnergyGenCurrentTick = 1234,
            EnergyGenOriginalCurrentTick = 2468,
            SwarmEnergyGenCurrentTick = 456,
            GrossRadius = 8000
        });
        status.Apply(sphere);
        Check.IsFalse(DysonSphere_Patch.BeforeGameTick_Prefix(sphere, 120));
        Check.AreEqual(1234L, sphere.energyGenCurrentTick); Check.AreEqual(456L, sphere.swarm.energyGenCurrentTick);
        Check.AreEqual(7L, sphere.energyReqCurrentTick);
    }

    [TestMethod]
    public void BlueprintExceptionsRestoreTheirOuterScope()
    {
        Manager.InBlueprint = true;
        DysonBlueprintData_Patch.FromBase64String_Prefix(out var outer);
        var error = new InvalidDataException();
        Check.AreSame(error, DysonBlueprintData_Patch.FromBase64String_Finalizer(error, outer));
        Check.IsTrue(Manager.InBlueprint);
        Manager.InBlueprint = false;
        DysonBlueprintData_Patch.FromBase64String_Prefix(out outer);
        DysonBlueprintData_Patch.FromBase64String_Finalizer(error, outer);
        Check.IsFalse(Manager.InBlueprint);
    }

    private static void ImportSnapshot(DysonSphereData packet) => throw new NotSupportedException();
    private static void InitSphere(DysonSphere sphere, GameData data, StarData star)
    { sphere.gameData = data; sphere.starData = star; sphere.swarm = new DysonSwarm(sphere) { sailInfos = Array.Empty<DysonSailInfo>() }; }
    private static void ReadSphere(DysonSphere sphere, BinaryReader reader)
    { if (failImport) throw new InvalidDataException("test corruption"); Check.AreEqual(3, reader.ReadInt32()); }
    private static IEnumerable<CodeInstruction> WithoutUnity(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var original in instructions)
        {
            var code = new CodeInstruction(original);
            if (code.operand is MethodInfo method && method.DeclaringType == typeof(DysonSphere))
            {
                var name = method.Name == "Init" ? nameof(InitSphere) : method.Name == "Import" ? nameof(ReadSphere) : null;
                if (name != null) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(DysonReplicationTest), name); }
            }
            yield return code;
        }
    }

    private sealed class SailProcessor : DysonSailBatchProcessor
    {
        internal void Apply(DysonSailBatchPacket packet, bool host) { IsHost = host; ProcessPacket(packet, null!); }
    }

    private sealed class RecordingConnection : INebulaeConnection
    {
        public List<byte[]> Raw { get; } = [];
        public bool IsAlive => true;
        public int Id => 1;
        public EConnectionStatus ConnectionStatus { get; set; }
        public void SendPacket<T>(T packet) where T : class, new() { }
        public void SendRawPacket(byte[] bytes) => Raw.Add((byte[])bytes.Clone());
        public bool Equals(INebulaeConnection? other) => ReferenceEquals(this, other);
    }

    private sealed class RecordingServer : RecordingNetwork, IServer
    {
        public ushort Port { get; set; }
        public ConcurrentPlayerCollection Players { get; } = new();
        public void Start() { }
        public void Update() { }
        public void Disconnect(INebulaeConnection connection, DisconnectionReason reason, string reasonMessage = "") { }
        public void SendToPlayers<T>(IEnumerable<KeyValuePair<INebulaeConnection, INebulaePlayer>> players, T packet) where T : class, new() { }
    }

    private class RecordingNetwork : INetworkProvider
    {
        public List<object> Packets { get; } = [];
        public INetPacketProcessor PacketProcessor { get; } = new NebulaeNetPacketProcessor();
        public byte[] WriteOnce<T>(T packet) where T : class, new() => PacketProcessor.Write(packet);
        public void SendPacket<T>(T packet) where T : class, new() => Packets.Add(packet);
        public void SendToMatching<T>(T packet, Predicate<INebulaePlayer> condition) where T : class, new() => SendPacket(packet);
        public void SendPacketToLocalStar<T>(T packet) where T : class, new() => SendPacket(packet);
        public void SendPacketToLocalPlanet<T>(T packet) where T : class, new() => SendPacket(packet);
        public void SendPacketToPlanet<T>(T packet, int planetId) where T : class, new() => SendPacket(packet);
        public void SendPacketToStar<T>(T packet, int starId) where T : class, new() => SendPacket(packet);
        public void SendPacketExclude<T>(T packet, INebulaeConnection exclude) where T : class, new() => SendPacket(packet);
        public void SendPacketToStarExclude<T>(T packet, int starId, INebulaeConnection exclude) where T : class, new() => SendPacket(packet);
        public void Dispose() { }
    }
}
