using HarmonyLib;
using NebulaeModel.Utils;
using NebulaeModel.Networking;
using NebulaeNetwork;
using NebulaeWorld;
using NebulaeWorld.Combat;
using NebulaeWorld.GameDataHistory;
using NebulaeWorld.GameStates;
using NebulaeWorld.Player;
using WebSocketSharp;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class ClientExitMenuTest
{
    private MultiplayerSession previousSession = null!;
    private GameData previousData = null!;
    private UIRoot previousRoot = null!;
    private bool previousLeaving, previousMenu;
    private readonly Harmony harmony = new("nebulae.tests.client-exit-menu");
    private static int ended, exited, disposed;
    private static bool failDispose;
    private static int published, closedAsync, stopped;
    private static bool failPublish;

    [TestInitialize]
    public void Setup()
    {
        previousSession = Multiplayer.Session;
        previousData = GameMain.data;
        previousRoot = UIRoot.instance;
        previousLeaving = Multiplayer.IsLeavingGame;
        previousMenu = Multiplayer.IsInMultiplayerMenu;
        ended = exited = disposed = 0; failDispose = false;
        var session = Uninitialized<MultiplayerSession>();
        session.IsGameLoaded = true;
        session.Client = Uninitialized<Client>();
        session.LocalPlayer = new LocalPlayer();
        session.Life = new PlayerLifeManager();
        session.Research = new ResearchContributionManager();
        session.InventoryTransfers = new PlayerInventoryTransfers();
        Multiplayer.Session = session;
        GameMain.data = Uninitialized<GameData>();
        var player = new Player();
        SetBackingField(player, "mecha", new Mecha());
        SetBackingField(GameMain.data, "mainPlayer", player);
        SetStatic(typeof(UIRoot), "_instance", Uninitialized<UIRoot>());
        Patch(typeof(ResearchContributionManager), nameof(ResearchContributionManager.TakeBackAfterPending), nameof(Skip));
        // Keep a transfer unsettled until the timeout without constructing a joined Unity world.
        harmony.Patch(AccessTools.PropertyGetter(typeof(PlayerInventoryTransfers), nameof(PlayerInventoryTransfers.Pending)),
            prefix: new HarmonyMethod(typeof(ClientExitMenuTest), nameof(PendingTransfer)));
        Patch(typeof(MultiplayerSession), nameof(MultiplayerSession.Dispose), nameof(Dispose));
        Patch(typeof(DSPGame), nameof(DSPGame.EndGame), nameof(End));
        Patch(typeof(DSPGame), nameof(DSPGame.ExitProgram), nameof(Exit));
        harmony.Patch(AccessTools.Method(typeof(UIRealtimeTip), nameof(UIRealtimeTip.Popup), [typeof(string), typeof(bool), typeof(int)]),
            prefix: new HarmonyMethod(typeof(ClientExitMenuTest), nameof(Skip)));
        harmony.Patch(AccessTools.Method(typeof(UIEscMenu), nameof(UIEscMenu.OnButton5Click)),
            prefix: new HarmonyMethod(typeof(UIEscMenu_Patch), nameof(UIEscMenu_Patch.OnButton5Click_Prefix)));
        harmony.Patch(AccessTools.Method(typeof(UIEscMenu), nameof(UIEscMenu.OnButton6Click)),
            prefix: new HarmonyMethod(typeof(UIEscMenu_Patch), nameof(UIEscMenu_Patch.OnButton6Click_Prefix)));
    }

    [TestCleanup]
    public void Cleanup()
    {
        harmony.UnpatchSelf();
        Multiplayer.Session = previousSession;
        GameMain.data = previousData;
        SetStatic(typeof(UIRoot), "_instance", previousRoot);
        Multiplayer.IsLeavingGame = previousLeaving;
        Multiplayer.IsInMultiplayerMenu = previousMenu;
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void RealMenuButtonExitsOnceAfterDeadlineWithoutSimulation(bool exitProgram, bool disposalFails)
    {
        failDispose = disposalFails;
        var menu = Uninitialized<UIEscMenu>();
        Click(menu, exitProgram);
        Click(menu, exitProgram);
        Check.AreEqual(0, ended);
        Check.AreEqual(0, exited);
        Check.IsTrue(Multiplayer.Session.Life.IsExiting);
        ExpireAndDispatch(Multiplayer.Session.Life);
        Check.IsNull(Multiplayer.Session);
        Check.AreEqual(1, disposed);
        Check.AreEqual(1, ended, "Returning to the menu must not invoke EndGame a second time.");
        Check.AreEqual(exitProgram ? 1 : 0, exited);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void QueuedExitCannotTearDownAReplacementSession(bool exitProgram)
    {
        Click(Uninitialized<UIEscMenu>(), exitProgram);
        var life = Multiplayer.Session.Life;
        SetField(life, "exitStarted", 1L);
        Update(); // Queues completion; a disconnect/rejoin wins before it is drained.
        var replacement = Uninitialized<MultiplayerSession>();
        Multiplayer.Session = replacement;
        Dispatch();
        Check.AreSame(replacement, Multiplayer.Session);
        Check.AreEqual(0, disposed);
        Check.AreEqual(0, ended);
        Check.AreEqual(0, exited);
    }

    [TestMethod]
    public void SinglePlayerButtonsKeepTheirOriginalHandlers()
    {
        Multiplayer.Session = null;
        var menu = Uninitialized<UIEscMenu>();
        Click(menu, false);
        Check.AreEqual(1, ended);
        menu = Uninitialized<UIEscMenu>();
        Click(menu, true);
        Check.AreEqual(1, exited);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public void SocketShutdownDoesNotRepeatDeferredSaveOrWaitOnTheUiThread(bool exiting, bool snapshotFails)
    {
        published = closedAsync = stopped = 0; failPublish = snapshotFails;
        SetField(Multiplayer.Session.Life, "exiting", exiting);
        SetBackingField(Multiplayer.Session, "JoinSync", new JoinSyncManager());
        var client = new Client("localhost", 8469, "ws");
        var socket = new WebSocket("ws://localhost:8469/socket");
        SetField(socket, "_readyState", WebSocketState.Open);
        SetField(client, "clientSocket", socket);
        SetField(client, "serverConnection", Uninitialized<NebulaeConnection>());
        harmony.Patch(AccessTools.PropertyGetter(typeof(WebSocket), nameof(WebSocket.IsAlive)),
            prefix: new HarmonyMethod(typeof(ClientExitMenuTest), nameof(SynchronousPing)));
        Patch(typeof(PlayerLifeManager), nameof(PlayerLifeManager.Publish), nameof(Publish));
        Patch(typeof(NebulaeConnection), nameof(NebulaeConnection.StopSending), nameof(StopSending));
        harmony.Patch(AccessTools.Method(typeof(NebulaeConnection), nameof(NebulaeConnection.FlushSendQueue)),
            prefix: new HarmonyMethod(typeof(ClientExitMenuTest), nameof(Flushed)));
        harmony.Patch(AccessTools.Method(typeof(WebSocket), nameof(WebSocket.CloseAsync), [typeof(ushort), typeof(string)]),
            prefix: new HarmonyMethod(typeof(ClientExitMenuTest), nameof(CloseAsync)));
        harmony.Patch(AccessTools.Method(typeof(WebSocket), nameof(WebSocket.Close), [typeof(ushort), typeof(string)]),
            prefix: new HarmonyMethod(typeof(ClientExitMenuTest), nameof(SynchronousClose)));
        if (snapshotFails) Check.ThrowsExactly<InvalidOperationException>(client.Stop);
        else client.Stop();
        Check.AreEqual(exiting ? 0 : 1, published);
        Check.AreEqual(1, stopped);
        Check.AreEqual(1, closedAsync, "A failed snapshot must still start socket shutdown.");
    }

    private void Patch(Type type, string method, string prefix) => harmony.Patch(AccessTools.Method(type, method),
        prefix: new HarmonyMethod(typeof(ClientExitMenuTest), prefix));
    private static bool Skip() => false;
    private static bool PendingTransfer(ref bool __result) { __result = true; return false; }
    private static bool Dispose() { disposed++; if (failDispose) throw new InvalidOperationException("Simulated cleanup failure"); return false; }
    private static bool End() { ended++; return false; }
    private static bool Exit() { exited++; return false; }
    private static bool SynchronousPing() { Check.Fail("IsAlive sends a synchronous Ping on the UI thread."); return false; }
    private static bool Flushed(ref bool __result) { __result = true; return false; }
    private static bool Publish() { published++; if (failPublish) throw new InvalidOperationException("Simulated checkpoint failure"); return false; }
    private static bool StopSending() { stopped++; return false; }
    private static bool CloseAsync() { closedAsync++; return false; }
    private static bool SynchronousClose() { Check.Fail("Socket shutdown blocked the UI thread."); return false; }
    private static void Click(UIEscMenu menu, bool exitProgram) =>
        AccessTools.Method(typeof(UIEscMenu), exitProgram ? nameof(UIEscMenu.OnButton6Click) : nameof(UIEscMenu.OnButton5Click)).Invoke(menu, null);
    private static void ExpireAndDispatch(PlayerLifeManager life) { SetField(life, "exitStarted", 1L); Update(); Update(); Dispatch(); }
    private static void Update() => Multiplayer.Session.Life.ServiceExit(DateTime.UtcNow.Ticks);
    private static void Dispatch() => ((MainThreadActionQueue)AccessTools.Field(typeof(UnityDispatchQueue), "actionsQueue").GetValue(null)).Drain();
}
