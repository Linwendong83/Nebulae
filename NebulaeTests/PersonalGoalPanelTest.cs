using HarmonyLib;
using System.Reflection;
using System.Reflection.Emit;
using NebulaeWorld;
using NebulaeWorld.GameStates;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class PersonalGoalPanelTest
{
    private static UIRoot root = null!;
    private static float panelPosition;
    private static int animationPasses;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FreeExplorationClosesOnlyAfterVanillaExitAnimation(bool host)
    {
        var previousSession = Multiplayer.Session;
        var previousData = GameMain.data;
        var previousScenario = GameMain.gameScenario;
        var harmony = new Harmony("nebulae.tests.personal-goal-panel");
        try
        {
            var goals = new GoalManager();
            var session = Uninitialized<MultiplayerSession>();
            session.Goals = goals;
            if (host) session.Server = Uninitialized<global::NebulaeNetwork.Server>();
            else session.Client = Uninitialized<global::NebulaeNetwork.Client>();
            Multiplayer.Session = session;
            var system = new GoalSystem { queueCursor = 1, goalQueue = new[] { 101 } };
            GameMain.data = new GameData { gameDesc = new GameDesc { goalLevel = EGoalLevel.Full }, goalSystem = system };
            GameMain.gameScenario = Uninitialized<GameScenarioLogic>();
            GameMain.gameScenario.goalLogic = new GoalLogic { isInit = true, initOpenDelayTimer = 8 };
            SetField(goals, "localLevel", EGoalLevel.Off);
            var panel = Uninitialized<UIGoalPanel>();
            panel.goalGroups = new List<UIGoalGroupEntry> { Uninitialized<UIGoalGroupEntry>() };
            panel.collapsePosX = -200;
            SetField(panel, "goalSystem", system);
            SetBackingField(panel, nameof(ManualBehaviour.inited), true);
            SetBackingField(panel, nameof(ManualBehaviour.active), true);
            root = Uninitialized<UIRoot>();
            root.uiGame = Uninitialized<UIGame>();
            root.uiGame.dashboard = Uninitialized<UIDashboard>();
            root.uiGame.dysonEditor = Uninitialized<UIDysonEditor>();
            SetBackingField(root.uiGame, nameof(ManualBehaviour.active), true);

            // Run vanilla Determine with production patches; replace Unity boundaries
            // and record dispatches to its entry, transform and visibility animations.
            harmony.CreateClassProcessor(typeof(PersonalGoalQueue_Patch)).Patch();
            Harmony.ReversePatch(AccessTools.Method(typeof(UIGoalPanel), nameof(UIGoalPanel.Determine)),
                new HarmonyMethod(typeof(PersonalGoalPanelTest), nameof(VanillaDetermine)),
                AccessTools.Method(typeof(PersonalGoalPanelTest), nameof(WithoutUnity)), null);
            panelPosition = 0;
            animationPasses = 0;
            Determine(panel);
            Check.IsTrue(panel.active, "Existing entries must finish their vanilla exit animation.");
            Check.AreEqual(3, animationPasses);

            panel.goalGroups.Clear();
            Determine(panel);
            Check.IsTrue(panel.active, "The empty panel must still finish collapsing.");
            Check.AreEqual(6, animationPasses);

            panelPosition = panel.collapsePosX;
            Determine(panel);
            Check.IsFalse(panel.active, "Close only after entries leave and the panel collapses.");
            Check.AreEqual(6, animationPasses);
            Check.IsFalse(system.IsGoalQueueEmpty(), "Keep the authoritative queue outside UI rendering.");
            Check.AreEqual(1, system.queueCursor);
            Check.AreEqual(101, system.goalQueue[0]);
            Check.AreEqual(EGoalLevel.Full, GameMain.data.gameDesc.goalLevel);
            Check.IsFalse(goals.RenderingPersonal);

            foreach (var level in new[] { EGoalLevel.Key, EGoalLevel.Full })
            {
                SetField(goals, "localLevel", level);
                Determine(panel);
                Check.IsTrue(panel.active, "Re-enabling goals must reopen the panel through vanilla rendering.");
            }
            Multiplayer.Session = null;
            Check.IsFalse(system.IsGoalQueueEmpty(), "Single-player must keep the vanilla queue result.");
        }
        finally
        {
            harmony.UnpatchSelf();
            Multiplayer.Session = previousSession;
            GameMain.data = previousData;
            GameMain.gameScenario = previousScenario;
            root = null!;
        }
    }

    [TestMethod]
    public void FreeExplorationDrainsDisplayGroupsWithoutChangingSharedProgress()
    {
        var previousData = GameMain.data;
        try
        {
            GameMain.data = new GameData { gameDesc = new GameDesc { goalLevel = EGoalLevel.Full } };
            var goals = new GoalManager();
            var shared = Uninitialized<GoalData>();
            shared.isGroup = true;
            shared._stage = EGoalStage.Enabled;
            shared.currentValue = 1;
            shared.targetValue = 3;
            var display = Uninitialized<GoalData>();
            SetField(goals, "displayData", new Dictionary<int, GoalData> { [101] = display });
            SetField(goals, "localLevel", EGoalLevel.Off);
            goals.BeginUiRender();
            Check.AreSame(display, goals.GetDisplayGoal(101, shared));
            Check.IsTrue(Uninitialized<UIGoalPanel>().CheckUILayerIgnore(display),
                "The vanilla zero-target branch must animate the group out and recycle it.");
            Check.AreEqual(0L, display.currentValue);
            Check.AreEqual(1L, shared.currentValue);
            Check.AreEqual(3L, shared.targetValue);
            Check.AreEqual(EGoalStage.Enabled, shared.stage);
            goals.EndUiRender();

            SetField(goals, "localLevel", EGoalLevel.Full);
            goals.BeginUiRender();
            Check.AreSame(display, goals.GetDisplayGoal(101, shared));
            Check.AreEqual(1L, display.currentValue);
            Check.AreEqual(3L, display.targetValue);
            goals.EndUiRender();
            Check.AreEqual(EGoalLevel.Full, GameMain.data.gameDesc.goalLevel);
        }
        finally { GameMain.data = previousData; }
    }

    private static IEnumerable<CodeInstruction> WithoutUnity(IEnumerable<CodeInstruction> instructions)
    {
        var boundaries = new Dictionary<MethodInfo, string>
        {
            [AccessTools.PropertyGetter(typeof(UnityEngine.Time), nameof(UnityEngine.Time.deltaTime))] = nameof(DeltaTime),
            [AccessTools.PropertyGetter(typeof(GameMain), nameof(GameMain.gameTick))] = nameof(GameTick),
            [AccessTools.PropertyGetter(typeof(UIRoot), nameof(UIRoot.instance))] = nameof(Root),
            [AccessTools.PropertyGetter(typeof(GameMain), nameof(GameMain.mainPlayer))] = nameof(MainPlayer),
            [AccessTools.Method(typeof(GoalTools), nameof(GoalTools.CheckSandbox1KeyUnlockTech))] = nameof(SandboxUnlock),
            [AccessTools.PropertyGetter(typeof(UIGoalPanel), nameof(UIGoalPanel.relatePosX))] = nameof(PanelPosition),
            [AccessTools.Method(typeof(ManualBehaviour), nameof(ManualBehaviour._Open))] = nameof(OpenWithoutUnity),
            [AccessTools.Method(typeof(ManualBehaviour), nameof(ManualBehaviour._Close))] = nameof(CloseWithoutUnity),
            [AccessTools.Method(typeof(UIGoalPanel), nameof(UIGoalPanel.DetermineEntry))] = nameof(Animate),
            [AccessTools.Method(typeof(UIGoalPanel), nameof(UIGoalPanel.DetermineTransform))] = nameof(Animate),
            [AccessTools.Method(typeof(UIGoalPanel), nameof(UIGoalPanel.DetermineVisiable))] = nameof(Animate)
        };
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && boundaries.TryGetValue(method, out var replacement))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(PersonalGoalPanelTest), replacement);
            }
            yield return instruction;
        }
    }

    private static void Determine(UIGoalPanel panel)
    {
        PersonalGoalPanel_Patch.Prefix();
        try { VanillaDetermine(panel); }
        finally { PersonalGoalPanel_Patch.Postfix(); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void VanillaDetermine(UIGoalPanel _) => throw new NotImplementedException();

    private static float DeltaTime() => 0.016f;
    private static long GameTick() => 1;
    private static UIRoot Root() => root;
    private static Player MainPlayer() => new Player { isAlive = true };
    private static bool SandboxUnlock() => false;
    private static float PanelPosition(UIGoalPanel _) => panelPosition;
    private static void Animate(UIGoalPanel _) => animationPasses++;
    private static void OpenWithoutUnity(ManualBehaviour panel) => SetBackingField(panel, nameof(ManualBehaviour.active), true);
    private static void CloseWithoutUnity(ManualBehaviour panel) => SetBackingField(panel, nameof(ManualBehaviour.active), false);
}
