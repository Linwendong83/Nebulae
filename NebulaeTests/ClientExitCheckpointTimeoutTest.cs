using HarmonyLib;
using NebulaeModel.Utils;
using NebulaeWorld;
using NebulaeWorld.Combat;
using NebulaeWorld.GameDataHistory;
using NebulaeWorld.Player;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

/// <summary>
///     A client exit waits for one final personal checkpoint so the host keeps the player's latest
///     inventory. That wait is a round trip the host can refuse or never answer, and it used to have
///     no bound: the reported symptom was a game that could not be quit.
/// </summary>
[TestClass, DoNotParallelize]
public class ClientExitCheckpointTimeoutTest
{
    private MultiplayerSession previous = null!;
    private Harmony harmony = null!;

    [TestInitialize]
    public void Setup()
    {
        previous = Multiplayer.Session;
        var session = Uninitialized<MultiplayerSession>();
        session.IsGameLoaded = true;
        session.Research = new ResearchContributionManager();
        session.InventoryTransfers = new PlayerInventoryTransfers();
        Multiplayer.Session = session;
        // The transfer ledger reads five session subsystems that need a joined world; the exit gate
        // only cares whether any of them still owes an acknowledgement.
        harmony = new Harmony("nebulae.tests.client-exit-timeout");
        harmony.Patch(AccessTools.PropertyGetter(typeof(PlayerInventoryTransfers), nameof(PlayerInventoryTransfers.Pending)),
            prefix: new HarmonyMethod(typeof(ClientExitCheckpointTimeoutTest), nameof(NoPendingTransfers)));
    }

    [TestCleanup]
    public void Cleanup()
    {
        harmony?.UnpatchSelf();
        Multiplayer.Session = previous;
    }

    private static bool NoPendingTransfers(ref bool __result) { __result = false; return false; }

    [TestMethod]
    public void AnExitWhoseFinalCheckpointIsNeverAcknowledgedStillCompletes()
    {
        var completed = 0;
        var life = Exiting(() => completed++);
        var start = DateTime.UtcNow.Ticks;

        life.ServiceExit(start);
        Dispatch();
        TestAssert.AreEqual(0, completed, "The exit must wait while its checkpoint can still settle.");
        TestAssert.IsTrue(life.IsExiting);

        life.ServiceExit(start + Seconds(PlayerLifeManager.ExitTimeoutSeconds - 1));
        Dispatch();
        TestAssert.AreEqual(0, completed, "The timeout must not fire before it is due.");

        life.ServiceExit(start + Seconds(PlayerLifeManager.ExitTimeoutSeconds + 1));
        Dispatch();
        TestAssert.AreEqual(1, completed);
    }

    [TestMethod]
    public void AnAcknowledgedFinalCheckpointCompletesTheExitBeforeTheTimeout()
    {
        var completed = 0;
        var life = Exiting(() => completed++);
        var start = DateTime.UtcNow.Ticks;

        life.ServiceExit(start);
        // The host accepted the queued ticket: no pending packet, no newer request.
        SetField(life, "acceptedTicket", 1L);
        SetField(life, "requestedTicket", 0L);
        SetField(life, "pending", null);

        life.ServiceExit(start + Seconds(1));
        Dispatch();
        TestAssert.AreEqual(1, completed);
    }

    [TestMethod]
    public void TheExitTimeoutRunsFromTheFirstTickInsteadOfSlidingForward()
    {
        var completed = 0;
        var life = Exiting(() => completed++);
        var start = DateTime.UtcNow.Ticks;

        life.ServiceExit(start);
        life.ServiceExit(start + Seconds(PlayerLifeManager.ExitTimeoutSeconds + 1));
        Dispatch();
        TestAssert.AreEqual(1, completed);
    }

    /// <summary>An exit already deferred by the Escape menu, without a game instance to settle it.</summary>
    private static PlayerLifeManager Exiting(Action completed)
    {
        var life = new PlayerLifeManager();
        SetField(life, "exiting", true);
        SetField(life, "afterExitCheckpoint", completed);
        return life;
    }

    private static long Seconds(double seconds) => (long)(TimeSpan.TicksPerSecond * seconds);

    private static void Dispatch() =>
        ((MainThreadActionQueue)AccessTools.Field(typeof(UnityDispatchQueue), "actionsQueue").GetValue(null)).Drain();
}
