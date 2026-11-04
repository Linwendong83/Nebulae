using HarmonyLib;
using NebulaeModel.Utils;
using NebulaeWorld;
using NebulaeWorld.GameStates;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class TutorialTipTest
{
    private static string identity = "tutorial-player-a";

    [TestMethod]
    [DataRow("singleplayer")]
    [DataRow("host")]
    [DataRow("client")]
    [DataRow("dedicated")]
    public void PopupDecisionLeavesTutorialHistoryUntouchedOnEveryPeer(string mode)
    {
        var previousSession = Multiplayer.Session;
        var previousDedicated = Multiplayer.IsDedicated;
        var previousData = GameMain.data;
        try
        {
            Multiplayer.IsDedicated = mode == "dedicated";
            Multiplayer.Session = mode == "singleplayer" ? null : Uninitialized<MultiplayerSession>();
            if (mode == "client") Multiplayer.Session!.Client = Uninitialized<global::NebulaeNetwork.Client>();
            else if (Multiplayer.Session != null) Multiplayer.Session.Server = Uninitialized<global::NebulaeNetwork.Server>();
            GameMain.data = Uninitialized<GameData>();
            GameMain.data.history = new GameHistoryData { tutorialUnlocked = new HashSet<int> { 7 } };

            // The old prefix unlocked even suppressed tutorials. Only the game's
            // actual popup may unlock them; the decision must preserve existing IDs.
            TestAssert.AreEqual(mode != "dedicated", UITutorialTip_Patch.PopupTutorialTip_Prefix());
            CollectionAssert.AreEquivalent(new[] { 7 }, GameMain.history.tutorialUnlocked.ToArray());
        }
        finally
        {
            Multiplayer.Session = previousSession;
            Multiplayer.IsDedicated = previousDedicated;
            GameMain.data = previousData;
        }
    }

    [TestMethod]
    public void ExistingTutorialRecordsSurviveReconnectAndStaySpecificToThePlayerAndWorld()
    {
        var previousSession = Multiplayer.Session;
        var previousDedicated = Multiplayer.IsDedicated;
        var previousWorld = SaveManager.WorldId;
        var propertyPath = AccessTools.Field(typeof(GameConfig), "propertyPath");
        var previousPath = propertyPath.GetValue(null);
        var keys = (HashSet<int>)AccessTools.Field(typeof(GameStatesManager), "preservedFeatureKeys").GetValue(null);
        var tutorials = (HashSet<int>)AccessTools.Field(typeof(GameStatesManager), "preservedTutorialUnlocked").GetValue(null);
        var previousKeys = keys.ToArray();
        var previousTutorials = tutorials.ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "nebulae-tutorial-" + Guid.NewGuid().ToString("N"));
        var harmony = new Harmony("nebulae.tests.tutorial-records");
        try
        {
            propertyPath.SetValue(null, directory);
            Multiplayer.IsDedicated = false;
            Multiplayer.Session = Uninitialized<MultiplayerSession>();
            Multiplayer.Session.IsGameLoaded = true;
            // Patch the same identity boundary as other fixtures: the crypto call can
            // already be inlined into this getter after an earlier patch is removed.
            harmony.Patch(AccessTools.PropertyGetter(typeof(MetadataManager), nameof(MetadataManager.LocalIdentity)),
                prefix: new HarmonyMethod(typeof(TutorialTipTest), nameof(LocalIdentity)));
            identity = "tutorial-player-a";
            var world = Guid.NewGuid().ToString("N");
            SaveManager.SetWorldId(world);
            int[] savedKeys = { FeatureID.ADVISOR_TIP_START + 7, FeatureID.TUTORIAL_TIP_LOW_SAIL_SPEED };
            // Includes tutorials auto-unlocked by the old suppression patch: keep all of them.
            LocalPlayerPreferences.SaveTutorials(savedKeys, new[] { 7, 8 });
            var path = Path.Combine(directory, "Nebulae", AtomicFile.IdentityFileName(identity), world + ".tutorials");
            var savedBytes = File.ReadAllBytes(path);

            GameHistoryData Restore()
            {
                keys.Clear();
                tutorials.Clear();
                var history = new GameHistoryData
                {
                    featureKeys = new HashSet<int> { FeatureID.ADVISOR_TIP_START + 99, 1100002 },
                    tutorialUnlocked = new HashSet<int> { 99 }
                };
                GameStatesManager.RestorePersonalHistory(history);
                return history;
            }

            void AssertPersonalRecords(GameHistoryData history)
            {
                CollectionAssert.AreEquivalent(new[] { 7, 8 }, history.tutorialUnlocked.ToArray());
                CollectionAssert.AreEquivalent(savedKeys.Append(1100002).ToArray(), history.featureKeys.ToArray());
            }

            AssertPersonalRecords(Restore());
            AssertPersonalRecords(Restore()); // New session, same world and identity.
            CollectionAssert.AreEqual(savedBytes, File.ReadAllBytes(path));

            SaveManager.SetWorldId(Guid.NewGuid().ToString("N"));
            TestAssert.IsEmpty(Restore().tutorialUnlocked);
            SaveManager.SetWorldId(world);
            identity = "tutorial-player-b";
            var otherPlayer = Restore();
            TestAssert.IsEmpty(otherPlayer.tutorialUnlocked);
            CollectionAssert.AreEquivalent(new[] { 1100002 }, otherPlayer.featureKeys.ToArray());
            identity = "tutorial-player-a";
            AssertPersonalRecords(Restore());
            CollectionAssert.AreEqual(savedBytes, File.ReadAllBytes(path));
        }
        finally
        {
            harmony.UnpatchSelf();
            Multiplayer.Session = previousSession;
            Multiplayer.IsDedicated = previousDedicated;
            SaveManager.SetWorldId(previousWorld);
            propertyPath.SetValue(null, previousPath);
            keys.Clear(); keys.UnionWith(previousKeys);
            tutorials.Clear(); tutorials.UnionWith(previousTutorials);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static bool LocalIdentity(ref string __result)
    {
        __result = identity;
        return false;
    }
}
