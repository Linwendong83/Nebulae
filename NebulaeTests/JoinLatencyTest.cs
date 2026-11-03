using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Contracts;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Planet;
using NebulaeModel.Packets.Session;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;
using NebulaeWorld.GameStates;
using NebulaeWorld.Planet;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class JoinLatencyTest
{
    private MultiplayerSession previous = null!;
    private GameData previousData = null!;
    private JoinSyncManager join = null!;
    private PlanetManager planets = null!;
    private Network network = null!;
    private PlanetData planet = null!;
    private Queue<PlanetData> previousQueue = null!;

    [TestInitialize]
    public void SetUp()
    {
        previous = Multiplayer.Session; previousData = GameMain.data;
        var session = Uninitialized<MultiplayerSession>();
        join = new JoinSyncManager { CloseLoading = () => { } }; planets = new PlanetManager(); network = new Network();
        previousQueue = PlanetModelingManager.fctPlanetReqList;
        PlanetModelingManager.fctPlanetReqList = new Queue<PlanetData>();
        session.Network = network; session.Planets = planets; session.Generations = new CombatGenerationManager();
        session.LocalPlayer = new LocalPlayer { Data = new PlayerData(1, 101) };
        AccessTools.Field(typeof(MultiplayerSession), "<JoinSync>k__BackingField").SetValue(session, join);
        Multiplayer.Session = session;
        var star = new StarData { id = 1, planetCount = 1 };
        planet = new PlanetData { id = 101, wanted = true, star = star };
        star.planets = new[] { planet };
        GameMain.data = new GameData { galaxy = new GalaxyData { stars = new[] { star } } };
        join.BeginLocal(); join.Adopt(new JoinSyncBegin { Generation = 17 }, null!);
    }

    [TestCleanup]
    public void TearDown()
    {
        planets.Dispose(); join.Dispose();
        Multiplayer.Session = previous; GameMain.data = previousData;
        PlanetModelingManager.fctPlanetReqList = previousQueue;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FactoryAndTerrainCanArriveInEitherOrderWithoutBlockingTheMissingResponse(bool terrainFirst)
    {
        if (terrainFirst) planet.loaded = true;
        planets.PrefetchFactory(planet); planets.PrefetchFactory(planet);
        TestAssert.AreEqual(1, network.Packets.OfType<FactoryLoadRequest>().Count());
        TestAssert.IsTrue(planets.Loads.Receive(101, out _));
        planets.BufferFactory(Snapshot());
        TestAssert.AreEqual(terrainFirst, planets.Loads.Paused);
        TestAssert.AreEqual(!terrainFirst, network.PacketProcessor.EnablePacketProcessing);
        planet.loaded = true;
        if (!terrainFirst) TestAssert.IsTrue(planets.TryActivateFactory(planet));
        TestAssert.IsFalse(planets.TryActivateFactory(planet));
        TestAssert.IsFalse(network.PacketProcessor.EnablePacketProcessing);
        TestAssert.IsTrue(planets.Loads.Paused);
        planets.CancelFactory(planet);
        TestAssert.IsTrue(network.PacketProcessor.EnablePacketProcessing);
        TestAssert.IsFalse(planets.PendingFactories.ContainsKey(101));
    }

    [TestMethod]
    public void CancelledBufferedFactoryCannotInstallAfterTerrainCompletes()
    {
        planets.PrefetchFactory(planet);
        planets.Loads.Receive(101, out _); planets.BufferFactory(Snapshot());
        planets.CancelFactory(planet); planet.loaded = true;
        TestAssert.IsFalse(planets.TryActivateFactory(planet));
        planets.PrefetchFactory(planet);
        TestAssert.AreEqual(2, network.Packets.OfType<FactoryLoadRequest>().Count());
        TestAssert.IsTrue(planets.Loads.Receive(101, out _));
    }

    [TestMethod]
    public void SpaceRejoinGeneratesNearbyTerrainWithoutRequestingTheBirthPlanetFactory()
    {
        Multiplayer.Session.LocalPlayer.Data.LocalPlanetId = -1;
        GameMain.galaxy.birthPlanetId = 101;
        var previousGenerationQueue = PlanetModelingManager.genPlanetReqList;
        PlanetModelingManager.genPlanetReqList = new Queue<PlanetData>();
        try
        {
            AccessTools.Method(typeof(PlanetModelingManager_Patch), "InternalLoadPlanetsRequestGenerator")
                .Invoke(null, new object[] { new[] { planet } });
            TestAssert.HasCount(1, PlanetModelingManager.genPlanetReqList);
            TestAssert.AreEqual(1, network.Packets.OfType<PlanetDataRequest>().Count());
            TestAssert.AreEqual(0, network.Packets.OfType<FactoryLoadRequest>().Count());
        }
        finally { PlanetModelingManager.genPlanetReqList = previousGenerationQueue; }
    }

    [TestMethod]
    public void InteractionWaitsForRequiredBaselinesAndQueuesAckBeforeOpening()
    {
        var acknowledgements = 0;
        void Ack() { TestAssert.IsFalse(join.CanInteract); acknowledgements++; }
        TestAssert.IsFalse(join.TryOpenInteraction(true, true, Ack));
        join.MarkLoaded();
        TestAssert.IsFalse(join.TryOpenInteraction(true, true, Ack));
        join.Barrier(new JoinSyncBarrier { Generation = 17, Sequence = 0 });
        TestAssert.IsFalse(join.TryOpenInteraction(false, true, Ack));
        TestAssert.IsFalse(join.TryOpenInteraction(true, false, Ack));
        TestAssert.IsTrue(join.TryOpenInteraction(true, true, Ack));
        TestAssert.IsTrue(join.CanInteract);
        TestAssert.IsTrue(join.LocalPending, "The final confirmation must remain a background protocol step.");
        TestAssert.IsTrue(join.TryOpenInteraction(false, false, Ack), "Live resubscriptions must not re-lock an admitted player.");
        TestAssert.AreEqual(1, acknowledgements);
    }

    [TestMethod]
    public void FutureAuthorityTrafficDoesNotPreventInteractionOrGetAppliedEarly()
    {
        using var runtime = new AuthoritySession(new AuthoritySessionState());
        runtime.Identity.ConfirmHandshake(); runtime.BeginAuthorityWorld(new AuthorityEpoch(1, 2), false);
        var scope = new ScopeKey(PoolKind.GroundEnemy, 101);
        var packet = AuthorityWorldStatePacket.Create(new AuthorityEnvelopeHeader(AuthorityFamily.WorldState, new AuthorityEpoch(1, 2), new ConnectionEpoch(3), 1, 99999, 0, 0),
            scope, 1, 0, new byte[4]);
        TestAssert.IsTrue(runtime.TryEnqueueReplicaMessage(packet, new ApplyScope(scope)));
        join.MarkLoaded(); join.Barrier(new JoinSyncBarrier { Generation = 17 });
        TestAssert.IsTrue(join.TryOpenInteraction(true, true, () => { }));
        runtime.OnFrameBoundary(1);
        TestAssert.AreEqual(1, runtime.InboundCount);
        TestAssert.IsTrue(join.CanInteract);
    }

    [TestMethod]
    public void FinalStillChecksReplayIdentityAfterOpeningAndFailureRevokesInteraction()
    {
        join.MarkLoaded(); join.Barrier(new JoinSyncBarrier { Generation = 17 });
        join.TryOpenInteraction(true, true, () => { });
        TestAssert.ThrowsExactly<InvalidOperationException>(() => join.Final(new SyncComplete { JoinGeneration = 18 }));
        TestAssert.ThrowsExactly<InvalidOperationException>(() => join.Final(new SyncComplete { JoinGeneration = 17, JoinSequence = 1 }));
        join.Final(new SyncComplete { JoinGeneration = 17, IsFinal = true });
        TestAssert.IsTrue(join.CanInteract);
        join.CancelLocal();
        TestAssert.IsFalse(join.CanInteract);
        TestAssert.IsFalse(join.CanSimulate);
        TestAssert.IsFalse(join.TryOpenInteraction(true, true, () => TestAssert.Fail()));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DisconnectChecksAdmissionBeforeTheJoinStateIsDisposed(bool admitted)
    {
        join.MarkLoaded();
        if (admitted)
        {
            join.Barrier(new JoinSyncBarrier { Generation = 17 });
            join.TryOpenInteraction(true, true, () => { });
        }
        var checkedAdmission = false;
        network.OnDispose = () =>
        {
            TestAssert.AreEqual(admitted, join.CanInteract);
            checkedAdmission = true;
        };
        Multiplayer.Session.Dispose();
        TestAssert.IsTrue(checkedAdmission);
        TestAssert.IsFalse(join.CanInteract);
        TestAssert.IsFalse(join.CanSimulate);
    }

    [TestMethod]
    public void ATransportShutdownFailureStillRevokesInteractionAndReleasesTheJoin()
    {
        join.MarkLoaded(); join.Barrier(new JoinSyncBarrier { Generation = 17 });
        join.TryOpenInteraction(true, true, () => { });
        network.OnDispose = () => throw new InvalidOperationException("transport failed");
        TestAssert.ThrowsExactly<InvalidOperationException>(() => Multiplayer.Session.Dispose());
        TestAssert.IsNull(Multiplayer.Session.Network);
        TestAssert.IsFalse(join.CanInteract);
        TestAssert.IsFalse(join.CanSimulate);
    }

    [TestMethod]
    public void LoadingDisplayTracksActualReceiveTerrainModelAndFinalizationStages()
    {
        TestAssert.AreEqual(JoinLoadingStage.ReceivingWorld, join.LoadingProgress.Stage);
        TestAssert.AreEqual(0L, join.LoadingProgress.Total);
        join.MarkWorldDataReceived();
        TestAssert.AreEqual(JoinLoadingStage.LoadingPlanet, join.LoadingProgress.Stage);
        AccessTools.Property(typeof(GameData), "localPlanet").SetValue(GameMain.data, planet);
        planet.factingCompletedStage = -1;
        TestAssert.AreEqual(JoinLoadingStage.LoadingPlanet, join.LoadingProgress.Stage);
        planet.loaded = true;
        TestAssert.AreEqual(JoinLoadingStage.ReceivingFactory, join.LoadingProgress.Stage);
        join.BeginTransfer(new JoinDataTransferBegin
        {
            Generation = 17,
            TransferId = 1,
            Stage = (byte)JoinDataStage.Factory,
            PlanetId = 101,
            MessageLengths = new[] { 100000 }
        }, null!);
        TestAssert.AreEqual(JoinLoadingStage.ReceivingFactory, join.LoadingProgress.Stage);
        TestAssert.AreEqual(100000L, join.LoadingProgress.Total);
        join.DataTransfer.Receive(new JoinDataTransferChunk { Generation = 17, TransferId = 1, Data = new byte[50000] });
        TestAssert.AreEqual(50000L, join.LoadingProgress.Received);
        join.DataTransfer.Receive(new JoinDataTransferChunk { Generation = 17, TransferId = 1, Offset = 50000, Data = new byte[50000] });
        TestAssert.AreEqual(JoinLoadingStage.LoadingModels, join.LoadingProgress.Stage);
        TestAssert.AreEqual(0, join.LoadingProgress.CompletedSteps);
        planet.factingCompletedStage = 3;
        TestAssert.AreEqual(3, join.LoadingProgress.CompletedSteps);
        planet.factingCompletedStage = 8;
        TestAssert.AreEqual(JoinLoadingStage.Preparing, join.LoadingProgress.Stage);
    }

    [TestMethod]
    public void OldConnectionTransfersCannotAffectANewJoinOrRepopulateCancelledBuffers()
    {
        var old = Uninitialized<NebulaeConnection>();
        var current = Uninitialized<NebulaeConnection>();
        var begin = new JoinDataTransferBegin
        {
            Generation = 17,
            TransferId = 1,
            Stage = (byte)JoinDataStage.Factory,
            PlanetId = 101,
            MessageLengths = new[] { 12 }
        };
        join.BeginLocal();
        join.Adopt(new JoinSyncBegin { Generation = 18 }, current);
        join.BeginTransfer(begin, old);
        TestAssert.IsFalse(join.DataTransfer.Active);
        begin.Generation = 18;
        join.BeginTransfer(begin, current);
        join.ReceiveTransfer(new JoinDataTransferChunk { Generation = 17, TransferId = 1, Data = new byte[6] }, old);
        TestAssert.AreEqual(0L, join.DataTransfer.ReceivedBytes);
        join.ReceiveTransfer(new JoinDataTransferChunk { Generation = 18, TransferId = 1, Data = new byte[6] }, current);
        TestAssert.AreEqual(6L, join.DataTransfer.ReceivedBytes);
        join.CancelLocal();
        join.BeginTransfer(begin, current);
        TestAssert.IsFalse(join.DataTransfer.Active);
        TestAssert.AreEqual(0L, join.DataTransfer.TotalBytes);
        join.BeginLocal();
        join.BeginTransfer(begin, current);
        TestAssert.IsFalse(join.DataTransfer.Active, "No transport is adopted yet.");
    }

    private static FactoryData Snapshot() => new(101, new byte[] { 1 }, new byte[] { 2 })
    { EnemyGenerations = new byte[4], BuildAssignments = Array.Empty<byte>() };

    private sealed class Network : INetworkProvider
    {
        public Action OnDispose { get; set; } = () => { };
        public List<object> Packets { get; } = new();
        public INetPacketProcessor PacketProcessor { get; } = new NebulaeNetPacketProcessor();
        public void SendPacket<T>(T packet) where T : class, new() => Packets.Add(packet);
        public byte[] WriteOnce<T>(T packet) where T : class, new() => ((NebulaeNetPacketProcessor)PacketProcessor).Write(packet);
        public void Dispose() => OnDispose();
        public void SendToMatching<T>(T packet, Predicate<INebulaePlayer> condition) where T : class, new() => SendPacket(packet);
        public void SendPacketToLocalStar<T>(T packet) where T : class, new() => SendPacket(packet);
        public void SendPacketToLocalPlanet<T>(T packet) where T : class, new() => SendPacket(packet);
        public void SendPacketToPlanet<T>(T packet, int planetId) where T : class, new() => SendPacket(packet);
        public void SendPacketToStar<T>(T packet, int starId) where T : class, new() => SendPacket(packet);
        public void SendPacketExclude<T>(T packet, INebulaeConnection exclude) where T : class, new() => SendPacket(packet);
        public void SendPacketToStarExclude<T>(T packet, int starId, INebulaeConnection exclude) where T : class, new() => SendPacket(packet);
    }
}
