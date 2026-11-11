using System.Runtime.CompilerServices;
using HarmonyLib;
using NebulaeWorld;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class UniverseObservationDisplayTest
{
    private readonly Harmony harmony = new("nebulae.tests.universe-observation-display");
    private MultiplayerSession previousSession = null!;
    private GameData previousData = null!;
    private bool previousWhiteMode;
    private UIGame ui = null!;

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session;
        previousData = GameMain.data;
        previousWhiteMode = FactoryModel.whiteMode0;
        FactoryModel.whiteMode0 = false;
        GameMain.data = new GameData { history = new GameHistoryData(), gameDesc = new GameDesc() };
        Multiplayer.Session = Uninitialized<MultiplayerSession>();
        Multiplayer.Session.LocalPlayer = new LocalPlayer { IsHost = false };
        ui = Uninitialized<UIGame>();
        harmony.Patch(AccessTools.PropertyGetter(typeof(UIGame), nameof(UIGame.dfVeinOnFinal)),
            postfix: new HarmonyMethod(typeof(UIGame_Patch), nameof(UIGame_Patch.DfVeinOnFinal_Postfix)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(GameMain), nameof(GameMain.sandboxToolsEnabled)),
            prefix: new HarmonyMethod(typeof(GameMain_Patch), nameof(GameMain_Patch.SandboxToolsEnabled_Prefix)));
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchSelf();
        Multiplayer.Session = previousSession;
        GameMain.data = previousData;
        FactoryModel.whiteMode0 = previousWhiteMode;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SavedDisplayChoiceWaitsForResearchAndSurvivesWorldChanges(bool host)
    {
        Multiplayer.Session.LocalPlayer.IsHost = host;
        ui.dfVeinOn = true; // The choice restored by GamePrefsData, including cross-world preferences.
        Check.IsFalse(ui.dfVeinOnFinal, "Restoring a choice must not grant exploration.");
        Check.IsTrue(ui.dfVeinOn, "Do not erase the saved choice while the technology is locked.");

        GameMain.history.universeObserveLevel = 1;
        Check.IsTrue(ui.dfVeinOnFinal, "The display should become available immediately after research.");

        GameMain.data.history = new GameHistoryData();
        Check.IsFalse(ui.dfVeinOnFinal, "The next world's technology must be checked again.");
        Check.IsTrue(ui.dfVeinOn);

        GameMain.data.gameDesc.isSandboxMode = true;
        Check.IsTrue(ui.dfVeinOnFinal, "Sandbox still allows the display without research.");
        GameMain.data.gameDesc.isSandboxMode = false;
        Check.IsFalse(ui.dfVeinOnFinal);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ResearchAndSandboxNeverOverrideTheNativeDisplayChoiceOrBlueprintView(bool enabled, bool whiteMode)
    {
        ui.dfVeinOn = enabled;
        FactoryModel.whiteMode0 = whiteMode;
        GameMain.history.universeObserveLevel = 4;
        Check.AreEqual(enabled && !whiteMode, ui.dfVeinOnFinal);
        GameMain.history.universeObserveLevel = 0;
        GameMain.data.gameDesc.isSandboxMode = true;
        Check.AreEqual(enabled && !whiteMode, ui.dfVeinOnFinal);
    }

    [TestMethod]
    public void SinglePlayerKeepsItsNativeDisplayBehavior()
    {
        Multiplayer.Session = null!;
        ui.dfVeinOn = true;
        Check.IsTrue(ui.dfVeinOnFinal);
        FactoryModel.whiteMode0 = true;
        Check.IsFalse(ui.dfVeinOnFinal);
    }

    [TestMethod]
    public void BothNativeDetailMethodsUsePreviewReadsWithoutWritingResearch()
    {
        var field = AccessTools.Field(typeof(GameHistoryData), nameof(GameHistoryData.universeObserveLevel));
        var reader = AccessTools.Method(typeof(UIPlanetDetail_Patch), nameof(UIPlanetDetail_Patch.ObserveLevelForDetail));
        var targets = DeclaredPatches(typeof(UIPlanetDetail_Patch))
            .Where(patch => patch.Patch.Name == nameof(UIPlanetDetail_Patch.ObserveLevel_Transpiler)).ToArray();
        CollectionAssert.AreEquivalent(new[] { "OnPlanetDataSet", "RefreshDynamicProperties" },
            targets.Select(patch => patch.Target!.Name).ToArray());
        Check.IsTrue(DeclaredPatches(typeof(UIGame_Patch)).Any(patch =>
            patch.Target == AccessTools.PropertyGetter(typeof(UIGame), nameof(UIGame.dfVeinOnFinal))));

        foreach (var (target, _) in targets)
        {
            var original = PatchProcessor.GetOriginalInstructions(target).ToArray();
            var reads = original.Where(code => code.LoadsField(field)).ToArray();
            var labels = reads.Select(code => code.labels.ToArray()).ToArray();
            var blocks = reads.Select(code => code.blocks.ToArray()).ToArray();
            var patched = UIPlanetDetail_Patch.ObserveLevel_Transpiler(original).ToArray();
            Check.HasCount(2, reads);
            Check.IsFalse(patched.Any(code => code.LoadsField(field) || code.StoresField(field)));
            var replacements = patched.Where(code => code.Calls(reader)).ToArray();
            Check.HasCount(2, replacements);
            for (var i = 0; i < replacements.Length; i++)
            {
                CollectionAssert.AreEqual(labels[i], replacements[i].labels.ToArray());
                CollectionAssert.AreEqual(blocks[i], replacements[i].blocks.ToArray());
            }
        }
    }

    [TestMethod]
    public void NestedAndFailingLobbyPreviewsNeverExposeTheirLevelToOtherDisplays()
    {
        harmony.Patch(AccessTools.Method(typeof(UniverseObservationDisplayTest), nameof(ReadDetail)),
            transpiler: new HarmonyMethod(typeof(UIPlanetDetail_Patch), nameof(UIPlanetDetail_Patch.ObserveLevel_Transpiler)));
        Multiplayer.Session.IsInLobby = true;
        ui.dfVeinOn = true;
        var history = GameMain.history;
        Check.AreEqual(16, ReadDetail(history, () => ReadDetail(history, () =>
        {
            Check.AreEqual(0, history.universeObserveLevel);
            Check.IsFalse(ui.dfVeinOnFinal, "Another display must not inherit the lobby's preview level.");
            return 0;
        })));
        var error = new InvalidOperationException("Preview interrupted");
        Check.AreSame(error, Check.ThrowsExactly<InvalidOperationException>(() => ReadDetail(history, () => throw error)));
        Check.AreEqual(0, history.universeObserveLevel);

        Multiplayer.Session.IsInLobby = false;
        foreach (var level in new[] { 0, 1, 2, 3, 4 })
        {
            history.universeObserveLevel = level;
            Check.AreEqual(level * 2, ReadDetail(history, () => 0));
        }
        Multiplayer.Session = null!;
        Check.AreEqual(8, ReadDetail(history, () => 0));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadDetail(GameHistoryData history, Func<int> nested) =>
        history.universeObserveLevel + nested() + history.universeObserveLevel;
}
