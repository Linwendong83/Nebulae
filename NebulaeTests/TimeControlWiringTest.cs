using HarmonyLib;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeNetwork.PacketProcessors.Universe.Editor;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld;
using NebulaeWorld.Universe;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class TimeControlWiringTest
{
    private MultiplayerSession previousSession = null!;
    private GameMain previousGame = null!;
    private GameData previousData = null!;
    private double previousSpeed;
    private readonly Harmony harmony = new("nebulae.tests.time-control-wiring");
    private static int requests;

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session; previousGame = GameMain.instance; previousData = GameMain.data;
        previousSpeed = FPSController.SimulateSpeed;
        var game = Uninitialized<GameMain>();
        AccessTools.Field(typeof(GameMain), "m_CachedPtr").SetValue(game, new IntPtr(1));
        SetStatic(typeof(GameMain), "<instance>k__BackingField", game);
        AccessTools.Field(typeof(GameMain), "_running").SetValue(game, true);
        var data = Uninitialized<GameData>();
        var player = Uninitialized<Player>(); player.isAlive = true;
        var controller = Uninitialized<PlayerController>();
        AccessTools.Field(typeof(PlayerController), "actionSail").SetValue(controller, Uninitialized<PlayerMove_Sail>());
        SetBackingField(player, "controller", controller);
        AccessTools.Field(typeof(GameData), "<mainPlayer>k__BackingField").SetValue(data, player);
        GameMain.data = data;
        Multiplayer.Session = Uninitialized<MultiplayerSession>();
        Multiplayer.Session.LocalPlayer = new LocalPlayer { IsHost = true };
        Multiplayer.Session.World = Uninitialized<SimulatedWorld>();
        Multiplayer.Session.WorldSimSpeed = 2f;
    }
    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchSelf();
        Multiplayer.Session = previousSession; GameMain.data = previousData;
        SetStatic(typeof(GameMain), "<instance>k__BackingField", previousGame);
        FPSController.SimulateSpeed = previousSpeed;
    }

    [TestMethod]
    public void ScreenshotAndProfilerNativeButtonsPauseTheSharedHostWorld()
    {
        PatchPauseButtons();
        AccessTools.Method(typeof(UIScreenshotTool), "OnPauseButtonClick").Invoke(Uninitialized<UIScreenshotTool>(), new object[] { 0 });
        TestAssert.IsTrue(Multiplayer.Session.WorldPaused); TestAssert.IsTrue(GameMain.isFullscreenPaused);
        AccessTools.Method(typeof(UIDeepProfilerSettings), "OnGameStatusButtonClick").Invoke(Uninitialized<UIDeepProfilerSettings>(), new object[] { 0 });
        TestAssert.IsFalse(Multiplayer.Session.WorldPaused); TestAssert.IsFalse(GameMain.isFullscreenPaused);
    }

    [TestMethod]
    public void ClientButtonsRequestInsteadOfChangingTheClockAndRespectProfilerEventLocks()
    {
        AccessTools.Field(typeof(MultiplayerSession), "<Client>k__BackingField").SetValue(Multiplayer.Session, Uninitialized<global::NebulaeNetwork.Client>());
        Multiplayer.Session.LocalPlayer.IsHost = false;
        harmony.Patch(AccessTools.Method(typeof(SimulatedWorld), nameof(SimulatedWorld.RequestWorldPause)),
            prefix: new HarmonyMethod(typeof(TimeControlWiringTest), nameof(CaptureRequest)));
        PatchPauseButtons();
        requests = 0;
        AccessTools.Method(typeof(UIScreenshotTool), "OnPauseButtonClick").Invoke(Uninitialized<UIScreenshotTool>(), new object[] { 0 });
        var profiler = Uninitialized<UIDeepProfilerSettings>();
        AccessTools.Method(typeof(UIDeepProfilerSettings), "OnGameStatusButtonClick").Invoke(profiler, new object[] { 0 });
        TestAssert.AreEqual(2, requests); TestAssert.IsFalse(GameMain.isFullscreenPaused);
        AccessTools.Field(typeof(UIDeepProfilerSettings), "event_lock").SetValue(profiler, true);
        AccessTools.Method(typeof(UIDeepProfilerSettings), "OnGameStatusButtonClick").Invoke(profiler, new object[] { 0 });
        TestAssert.AreEqual(2, requests);
    }

    [TestMethod]
    public void NativeUpsToolOpeningKeepsTheSharedSpeedAndAllTranspilersBind()
    {
        // The full Unity UI bodies contain Mono-only ECalls. Transform their actual IL
        // without asking the desktop CLR to JIT those unrelated engine calls.
        foreach (var name in new[] { "_OnOpen", "_OnClose", "_OnUpdate" })
        {
            var code = UIUPSTool_Patch.PreserveSharedTarget(PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(UIUPSTool), name))).ToArray();
            TestAssert.AreEqual(1, code.Count(instruction => instruction.Calls(AccessTools.Method(typeof(UIUPSTool_Patch), "DefaultSpeed"))));
        }
        var clock = FPSControllerClock_Patch.Transpiler(PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(FPSController), "Update"))).ToArray();
        TestAssert.AreEqual(1, clock.Count(instruction => instruction.Calls(AccessTools.Method(typeof(FPSControllerClock_Patch), "SharedFixedDelta"))));
        FPSController.SimulateSpeed = 2d;
        var tool = Uninitialized<UIUPSTool>();
        tool.targetSimSpeed = 1d;
        UIUPSTool_Patch.Lifecycle_Postfix(tool);
        TestAssert.AreEqual(2d, tool.targetSimSpeed);
        TestAssert.AreEqual(2d, FPSController.SimulateSpeed);
    }

    [TestMethod]
    public void SharedSpeedAppliesOnPlanetsAndDedicatedHostsWithoutDoubleScalingSailing()
    {
        FPSController.SimulateSpeed = 2d;
        GameMain.mainPlayer.movementState = EMovementState.Walk;
        TestAssert.AreEqual(1f / 120, FPSControllerClock_Patch.SharedFixedDelta(1f / 60), 1e-7);
        GameMain.mainPlayer.isAlive = false;
        TestAssert.AreEqual(1f / 120, FPSControllerClock_Patch.SharedFixedDelta(1f / 60), 1e-7);
        GameMain.mainPlayer.isAlive = true; GameMain.mainPlayer.movementState = EMovementState.Sail;
        TestAssert.AreEqual(1f / 120, FPSControllerClock_Patch.SharedFixedDelta(1f / 120), 1e-7);
    }

    [TestMethod]
    public void EternalSailsBroadcastAppliesOnlyInSandbox()
    {
        var data = GameMain.data;
        data.gameDesc = new GameDesc { isSandboxMode = true };
        var swarm = Uninitialized<DysonSwarm>(); swarm.gameData = data;
        swarm.sailCapacity = 2; swarm.expiryCursor = 0; swarm.expiryEnding = 1;
        swarm.expiryOrder = new[] { new ExpiryOrder { time = 123 }, new ExpiryOrder() };
        var sphere = Uninitialized<DysonSphere>(); sphere.swarm = swarm;
        data.dysonSpheres = new[] { sphere };
        Multiplayer.Session.DysonSpheres = new DysonSphereManager();
        var processor = new DysonSwarmRemoveOrbitProcessor();
        AccessTools.Field(typeof(PacketProcessor<DysonSwarmRemoveOrbitPacket>), "IsHost").SetValue(processor, false);
        var packet = new DysonSwarmRemoveOrbitPacket(0, 0, SwarmRemoveOrbitEvent.MakeSailsEternal);
        AccessTools.Method(typeof(DysonSwarmRemoveOrbitProcessor), "ProcessPacket").Invoke(processor, new object[] { packet, null! });
        TestAssert.IsTrue(swarm.eternal); TestAssert.AreEqual(long.MaxValue, swarm.expiryOrder[0].time);
        data.gameDesc.isSandboxMode = false; swarm.eternal = false; swarm.expiryOrder[0].time = 123;
        AccessTools.Method(typeof(DysonSwarmRemoveOrbitProcessor), "ProcessPacket").Invoke(processor, new object[] { packet, null! });
        TestAssert.IsFalse(swarm.eternal); TestAssert.AreEqual(123L, swarm.expiryOrder[0].time);
    }

    private static bool CaptureRequest() { requests++; return false; }

    private void PatchPauseButtons()
    {
        harmony.Patch(AccessTools.Method(typeof(UIScreenshotTool), "OnPauseButtonClick"),
            prefix: new HarmonyMethod(typeof(UIScreenshotTool_Patch), nameof(UIScreenshotTool_Patch.OnPauseButtonClick_Prefix)),
            postfix: new HarmonyMethod(typeof(UIScreenshotTool_Patch), nameof(UIScreenshotTool_Patch.OnPauseButtonClick_Postfix)));
        harmony.Patch(AccessTools.Method(typeof(UIDeepProfilerSettings), "OnGameStatusButtonClick"),
            prefix: new HarmonyMethod(typeof(UIDeepProfilerSettings_Patch), nameof(UIDeepProfilerSettings_Patch.OnGameStatusButtonClick_Prefix)),
            postfix: new HarmonyMethod(typeof(UIDeepProfilerSettings_Patch), nameof(UIDeepProfilerSettings_Patch.OnGameStatusButtonClick_Postfix)));
    }
}
