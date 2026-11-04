using System.Reflection;
using HarmonyLib;
using NebulaeWorld;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class HeadlessTickRateTest
{
    private readonly Harmony harmony = new("nebulae.tests.headless-tick-rate");
    private MultiplayerSession? previousSession;
    private GameMain game = null!;
    private static readonly MethodInfo TickRate = AccessTools.Method(typeof(GameMain), "DetermineGameTickRate");
    private static readonly FieldInfo Paused = AccessTools.Field(typeof(GameMain), "_fullscreenPaused");
    private static readonly FieldInfo Unlock = AccessTools.Field(typeof(GameMain), "_fullscreenPausedUnlockOneFrame");
    private static readonly FieldInfo CanPause = AccessTools.Field(typeof(MultiplayerSession), "canPause");

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session;
        // No Unity scene is needed: the dedicated prefix must bypass the death branch,
        // even though this instance has no GameData and cannot run the original body.
        game = Uninitialized<GameMain>();
        Multiplayer.Session = Uninitialized<MultiplayerSession>();
        CanPause.SetValue(Multiplayer.Session, true);
        var patches = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType(
            "NebulaePatcher.Patches.Misc.Dedicated_Server_Patches", true)!;
        harmony.Patch(TickRate,
            prefix: new HarmonyMethod(AccessTools.Method(patches, "DetermineGameTickRate_Prefix")),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(GameMain_Patch), "DetermineGameTickRate_Postfix")));
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchSelf();
        Multiplayer.Session = previousSession;
    }

    private int Frames() => (int)TickRate.Invoke(game, null)!;

    [TestMethod]
    public void DedicatedClockRunsWithoutConsultingTheDeadHost()
    {
        TestAssert.AreEqual(1, Frames());
        TestAssert.AreEqual(1, Frames());
    }

    [TestMethod]
    public void AJoiningPlayerDoesNotStopTheDedicatedClock()
    {
        Multiplayer.Session.World = Uninitialized<SimulatedWorld>();
        Multiplayer.Session.World.OnPlayerJoining("new player");
        CanPause.SetValue(Multiplayer.Session, false);
        TestAssert.AreEqual(1, Frames());
        TestAssert.AreEqual(1, Frames());
    }

    [TestMethod]
    public void LoadedJoiningClientCanExecuteCatchupStepsWithoutOverridingManualPause()
    {
        var join = new NebulaeWorld.GameStates.JoinSyncManager();
        AccessTools.Field(typeof(MultiplayerSession), "<JoinSync>k__BackingField").SetValue(Multiplayer.Session, join);
        join.BeginLocal();
        var result = 1;
        GameMain_Patch.DetermineGameTickRate_Postfix(ref result);
        TestAssert.AreEqual(0, result);
        join.MarkLoaded();
        GameMain_Patch.DetermineGameTickRate_Postfix(ref result);
        TestAssert.AreEqual(1, result, "Replica tails wait for native ticks; they must be able to catch up behind the loading UI.");
        Multiplayer.Session.WorldPaused = true;
        GameMain_Patch.DetermineGameTickRate_Postfix(ref result);
        TestAssert.AreEqual(0, result);
    }

    [TestMethod]
    public void UnlockAdvancesExactlyOneFrameWhilePaused()
    {
        Paused.SetValue(game, true);
        Unlock.SetValue(game, true);
        TestAssert.AreEqual(1, Frames());
        TestAssert.IsFalse((bool)Unlock.GetValue(game)!);
        TestAssert.AreEqual(0, Frames());
    }

    [TestMethod]
    public void ExistingMultiplayerNoPausePolicyStillOverridesThePause()
    {
        Paused.SetValue(game, true);
        CanPause.SetValue(Multiplayer.Session, false);
        TestAssert.AreEqual(1, Frames());
    }

    [TestMethod]
    public void WorldPauseStopsTheClockWhileTheMenuPauseStaysDisabled()
    {
        Paused.SetValue(game, true);
        CanPause.SetValue(Multiplayer.Session, false);
        Multiplayer.Session.WorldPaused = true;
        TestAssert.AreEqual(0, Frames());

        // Resuming the world hands the never-pause policy back
        Multiplayer.Session.WorldPaused = false;
        TestAssert.AreEqual(1, Frames());
    }

    [TestMethod]
    public void UnlockIsConsumedBeforeALaterPause()
    {
        Unlock.SetValue(game, true);
        TestAssert.AreEqual(1, Frames());
        Paused.SetValue(game, true);
        TestAssert.AreEqual(0, Frames());
    }
}
