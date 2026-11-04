using HarmonyLib;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameStates;
using NebulaeNetwork.PacketProcessors.GameStates;
using NebulaeWorld;
using NebulaeWorld.GameStates;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class WorldPauseSyncTest
{
    private static float capturedFixUPS;
    private static double capturedPoseTime;

    private Harmony harmony = null!; // Assigned by TestInitialize.
    private GameMain game = null!; // Assigned by TestInitialize.
    private MultiplayerSession? previousSession;
    private GameMain? previousInstance;
    private GameData? previousData;
    private GameStatesManager state = null!; // Assigned by TestInitialize.

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session;
        previousInstance = GameMain.instance;
        previousData = GameMain.data;
        game = Uninitialized<GameMain>();
        // UnityEngine.Object's == reads an uninitialized managed object as null (its cached
        // native pointer is zero), which would make every static GameMain property silently
        // no-op. Give the stand-in a non-zero pointer so it compares alive.
        AccessTools.Field(typeof(GameMain), "m_CachedPtr")?.SetValue(game, new IntPtr(1));
        SetStatic(typeof(GameMain), "<instance>k__BackingField", game);
        var session = Uninitialized<MultiplayerSession>();
        session.World = Uninitialized<SimulatedWorld>();
        state = Uninitialized<GameStatesManager>();
        session.State = state;
        Multiplayer.Session = session;

        // SetFixUPS writes through a Unity instance the test host does not have, so record the
        // requested rate here instead. The parameter name is the game's own: Harmony injects
        // arguments by name.
        harmony = new Harmony("nebulae.tests.world-pause");
        harmony.Patch(AccessTools.Method(typeof(FPSController), nameof(FPSController.SetFixUPS)),
            prefix: new HarmonyMethod(typeof(WorldPauseSyncTest), nameof(CaptureFixUPS)));
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchSelf();
        Multiplayer.Session = previousSession;
        SetStatic(typeof(GameMain), "<instance>k__BackingField", previousInstance);
        GameMain.data = previousData;
    }

    [TestMethod]
    public void AutomaticHostPauseBoundsLoadedJoiningClientsAndReleasesOnANewClockSample()
    {
        Multiplayer.Session.Client = Uninitialized<global::NebulaeNetwork.Client>();
        var joining = new JoinSyncManager();
        AccessTools.Field(typeof(MultiplayerSession), "<JoinSync>k__BackingField").SetValue(Multiplayer.Session, joining);
        joining.BeginLocal(); joining.MarkLoaded();
        state.InstallHostClockState(true, 100);
        GameMain.gameTick = 99;
        var frames = 1; GameMain_Patch.DetermineGameTickRate_Postfix(ref frames);
        TestAssert.AreEqual(1, frames, "Finish the real native steps up to the host boundary.");
        GameMain.gameTick = 100;
        GameMain_Patch.DetermineGameTickRate_Postfix(ref frames);
        TestAssert.AreEqual(0, frames);
        TestAssert.IsFalse(Multiplayer.Session.WorldPaused, "Automatic pause must not become a player-requested pause.");
        state.InstallHostClockState(false, 0);
        GameMain_Patch.DetermineGameTickRate_Postfix(ref frames);
        TestAssert.AreEqual(1, frames);
    }

    [TestMethod]
    public void SetWorldPauseWritesBothTheSessionFlagAndTheGameFlag()
    {
        Multiplayer.Session.World.SetWorldPause(true);
        TestAssert.IsTrue(Multiplayer.Session.WorldPaused);
        TestAssert.IsTrue(GameMain.isFullscreenPaused);

        Multiplayer.Session.World.SetWorldPause(false);
        TestAssert.IsFalse(Multiplayer.Session.WorldPaused);
        TestAssert.IsFalse(GameMain.isFullscreenPaused);
    }

    [TestMethod]
    public void PausedEditsAdvanceEveryPeerToTheHostsAbsoluteLimit()
    {
        GameMain.gameTick = 100;
        Multiplayer.Session.World.SetWorldPause(true);
        Multiplayer.Session.World.HostStepWorld();
        Multiplayer.Session.World.HostStepWorld();
        TestAssert.AreEqual(102L, Multiplayer.Session.WorldPauseStepTick);
        var frames = 9;
        GameMain_Patch.DetermineGameTickRate_Postfix(ref frames);
        TestAssert.AreEqual(1, frames);
        GameMain.gameTick = 102;
        GameMain_Patch.DetermineGameTickRate_Postfix(ref frames);
        TestAssert.AreEqual(0, frames);

        // A lagging peer receives the same absolute limit, not another local increment.
        GameMain.gameTick = 99;
        InvokeProcessor(false, new GamePausePacket(true, 102));
        GameMain_Patch.DetermineGameTickRate_Postfix(ref frames);
        TestAssert.AreEqual(1, frames);
        Multiplayer.Session.World.SetWorldPause(false);
        TestAssert.AreEqual(0L, Multiplayer.Session.WorldPauseStepTick);
    }

    [TestMethod]
    public void ALocalFullscreenFlagClearCannotResumeTheSharedClock()
    {
        Multiplayer.Session.World.SetWorldPause(true);
        GameMain.isFullscreenPaused = false;
        var frames = 1;
        GameMain_Patch.DetermineGameTickRate_Postfix(ref frames);
        TestAssert.AreEqual(0, frames);
    }

    [TestMethod]
    public void NoStepRequestChangesARunningWorld()
    {
        Multiplayer.Session.World.HostStepWorld();
        TestAssert.AreEqual(0L, Multiplayer.Session.WorldPauseStepTick);
        TestAssert.IsFalse(Multiplayer.Session.WorldPaused);
    }

    [TestMethod]
    public void RepeatingThePauseStateKeepsQueuedSingleSteps()
    {
        Multiplayer.Session.World.SetWorldPause(true, 102);
        Multiplayer.Session.World.HostSetWorldPause(true);
        TestAssert.AreEqual(102L, Multiplayer.Session.WorldPauseStepTick);
    }

    [TestMethod]
    public void AHostPauseLetsLaggingPeersReachItsCurrentClockBoundary()
    {
        GameMain.gameTick = 100;
        Multiplayer.Session.World.HostSetWorldPause(true);
        TestAssert.AreEqual(100L, Multiplayer.Session.WorldPauseStepTick);
        var frames = 1;
        GameMain_Patch.DetermineGameTickRate_Postfix(ref frames);
        TestAssert.AreEqual(0, frames);
        GameMain.gameTick = 99;
        GameMain_Patch.DetermineGameTickRate_Postfix(ref frames);
        TestAssert.AreEqual(1, frames);
    }

    [TestMethod]
    public void HostWindowCloseResumesAndClearsTheSteppingLimit()
    {
        Multiplayer.Session.LocalPlayer = new LocalPlayer { IsHost = true };
        Multiplayer.Session.World.SetWorldPause(true, 100);
        SimulatedWorld.SyncWorldPauseAfterWindowClosed(false);
        TestAssert.IsFalse(Multiplayer.Session.WorldPaused);
        TestAssert.IsFalse(GameMain.isFullscreenPaused);
        TestAssert.AreEqual(0L, Multiplayer.Session.WorldPauseStepTick);
    }

    [TestMethod]
    public void SyncCompletedKeepsAPauseRequestedBeforeOrDuringJoining()
    {
        Multiplayer.Session.World.SetWorldPause(true);
        Multiplayer.Session.World.IsPlayerJoining = true;
        Multiplayer.Session.CanPause = true;

        Multiplayer.Session.World.OnAllPlayersSyncCompleted();

        TestAssert.IsFalse(Multiplayer.Session.World.IsPlayerJoining);
        TestAssert.IsTrue(Multiplayer.Session.WorldPaused);
        // Join bookkeeping never owns the player's pause flag.
        TestAssert.IsTrue(GameMain.isFullscreenPaused);
        TestAssert.IsFalse(Multiplayer.Session.CanPause);
    }

    [TestMethod]
    public void JoiningDoesNotInstallAPauseOrEnableMenuPausing()
    {
        Multiplayer.Session.CanPause = false;
        Multiplayer.Session.World.OnPlayerJoining("new player");
        TestAssert.IsTrue(Multiplayer.Session.World.IsPlayerJoining);
        TestAssert.IsFalse(GameMain.isFullscreenPaused);
        TestAssert.IsFalse(Multiplayer.Session.CanPause);
        Multiplayer.Session.World.OnAllPlayersSyncCompleted();

        TestAssert.IsFalse(Multiplayer.Session.WorldPaused);
        TestAssert.IsFalse(GameMain.isFullscreenPaused);
    }

    [TestMethod]
    public void HostAppliesARequestedWorldPause()
    {
        InvokeProcessor(isHost: true, new GamePausePacket(true));
        TestAssert.IsTrue(Multiplayer.Session.WorldPaused);
    }

    [TestMethod]
    public void ClientAdoptsTheBroadcastWorldPause()
    {
        InvokeProcessor(isHost: false, new GamePausePacket(true));
        TestAssert.IsTrue(Multiplayer.Session.WorldPaused);
    }

    [TestMethod]
    public void PauseAndResumeRequestsStillWorkWhileAPlayerIsJoining()
    {
        Multiplayer.Session.World.IsPlayerJoining = true;

        InvokeProcessor(isHost: true, new GamePausePacket(true));
        TestAssert.IsTrue(Multiplayer.Session.WorldPaused);
        InvokeProcessor(isHost: true, new GamePausePacket(false));
        TestAssert.IsFalse(Multiplayer.Session.WorldPaused);
    }

    [TestMethod]
    public void ClientFollowsAHostRunningAtHalfSpeed()
    {
        SailInRunningScene();
        ConvergedClient(hostUps: 30f);

        Process(unitsPerSecond: 30f, simSpeed: 0.5f, rtt: 20);

        // 30 actual UPS at 0.5x means fixUPS 60, which is exactly the host's own value.
        TestAssert.AreEqual(60f, capturedFixUPS, 1e-4);
        TestAssert.AreEqual(0.5, FPSController.SimulateSpeed, 1e-4);
    }

    [TestMethod]
    public void ClientFollowsAHostRunningAtQuadrupleSpeed()
    {
        SailInRunningScene();
        ConvergedClient(hostUps: 240f);

        Process(unitsPerSecond: 240f, simSpeed: 4f, rtt: 2);

        TestAssert.AreEqual(60f, capturedFixUPS, 1e-4);
    }

    [TestMethod]
    public void ASlowedSharedWorldCompensatesOnAPlanetToo()
    {
        SailInRunningScene();
        GameMain.mainPlayer.movementState = EMovementState.Walk;
        ConvergedClient(hostUps: 30f);

        Process(unitsPerSecond: 30f, simSpeed: 0.5f, rtt: 20);

        TestAssert.AreEqual(60f, capturedFixUPS, 1e-4);
    }

    [TestMethod]
    public void ClockConvergesByExecutingStepsAtABoundedRate()
    {
        var cases = new (float diff, float averageUPS, float fixUps)[]
        {
            (0f, 60f, 60f),
            (300f, 60f, 240f),
            (6000f, 60f, 240f),
            (-200f, 60f, 1f),
            (-50f, 60f, 10f),
            (0f, 5f, 5f),
        };
        foreach (var (diff, averageUPS, fixUps) in cases)
        {
            var actualFixUps = GameStatesManager.ComputeFixUPS(diff, averageUPS, 1f);
            TestAssert.AreEqual(fixUps, actualFixUps, 1e-4, $"diff {diff}");
        }
    }

    [TestMethod]
    public void LoadingABaselineInstallsItsClockOnceBeforeLocalSimulationStarts()
    {
        GameMain.gameTick = 0;
        AccessTools.Field(typeof(GameStatesManager), "initialSimulationTick").SetValue(state, 123456L);
        state.InstallWorldClock();
        TestAssert.AreEqual(123456L, GameMain.gameTick);
        GameMain.gameTick++;
        state.InstallWorldClock();
        TestAssert.AreEqual(123457L, GameMain.gameTick);
    }

    [TestMethod]
    public void InstallingTheClockUpdatesOrbitalPosesBeforeRestorationEvenWhenPaused()
    {
        capturedPoseTime = -1;
        harmony.Patch(AccessTools.Method(typeof(GalaxyData), nameof(GalaxyData.UpdatePoses)),
            prefix: new HarmonyMethod(typeof(WorldPauseSyncTest), nameof(CapturePoseTime)));
        var data = Uninitialized<GameData>(); data.galaxy = Uninitialized<GalaxyData>();
        AccessTools.Field(typeof(GameStatesManager), "initialSimulationTick").SetValue(state, 360000L);
        Multiplayer.Session.WorldPaused = true;
        state.InstallWorldClock(data);
        TestAssert.AreEqual(6000d, capturedPoseTime);
        TestAssert.AreEqual(360000L, GameMain.gameTick);
        capturedPoseTime = -1;
        state.InstallWorldClock(data);
        TestAssert.AreEqual(-1d, capturedPoseTime, "Clock and ephemeris installation is idempotent.");
    }

    private static bool CapturePoseTime(double time) { capturedPoseTime = time; return false; }

    [TestMethod]
    public void FirstClockObservationImmediatelyStartsBoundedCatchup()
    {
        GameMain.gameTick = 100;
        capturedFixUPS = -1;
        state.ProcessGameStateUpdatePacket(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 30, 700, 60f, 1f);
        TestAssert.AreEqual(240f, capturedFixUPS);
        TestAssert.AreEqual(100L, GameMain.gameTick);
        TestAssert.IsTrue((bool)AccessTools.Field(typeof(GameStatesManager), "hasClockSample").GetValue(state));
    }

    [TestMethod]
    [DataRow(100L, 6000L, 20)]
    [DataRow(6000L, 100L, 20)]
    [DataRow(100L, 6000L, 1000)]
    public void ClockObservationsNeverSkipOrRewindExecutedTicks(long localTick, long hostTick, int rtt)
    {
        GameMain.gameTick = localTick;
        ConvergedClient(60f);
        state.ProcessGameStateUpdatePacket(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - rtt,
            hostTick, 60f, 1f);
        TestAssert.AreEqual(localTick, GameMain.gameTick);
    }

    private void Process(float unitsPerSecond, float simSpeed, int rtt)
    {
        var sentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - rtt;
        state.ProcessGameStateUpdatePacket(sentTime, GameMain.gameTick, unitsPerSecond, simSpeed);
    }

    // A client whose UPS estimate already agrees with the host, so one packet is enough.
    private void ConvergedClient(float hostUps)
    {
        AccessTools.Field(typeof(GameStatesManager), "hasClockSample").SetValue(state, true);
        AccessTools.Field(typeof(GameStatesManager), "serverUPS").SetValue(state, hostUps);
    }

    private void SailInRunningScene()
    {
        // GameMain.mainPlayer is a computed property over data.mainPlayer
        var data = Uninitialized<GameData>();
        var player = Uninitialized<Player>();
        player.movementState = EMovementState.Sail;
        player.isAlive = true;
        AccessTools.Field(typeof(GameData), "<mainPlayer>k__BackingField").SetValue(data, player);
        GameMain.data = data;
        AccessTools.Field(typeof(GameMain), "_running").SetValue(game, true);
    }

    private static void InvokeProcessor(bool isHost, GamePausePacket packet)
    {
        var processor = new GamePauseProcessor();
        AccessTools.Field(typeof(PacketProcessor<GamePausePacket>), "IsHost").SetValue(processor, isHost);
        AccessTools.Method(typeof(GamePauseProcessor), "ProcessPacket").Invoke(processor, new object?[] { packet, null });
    }

    private static bool CaptureFixUPS(double _fixups)
    {
        capturedFixUPS = (float)_fixups;
        return false;
    }

}

