using HarmonyLib;
using NebulaeWorld;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class SandboxModeTest
{
    private readonly Harmony harmony = new("nebulae.tests.sandbox-mode");
    private MultiplayerSession? previousSession;
    private GameMain? previousInstance;
    private GameData? previousData;
    private bool previousDedicated;

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session;
        previousInstance = GameMain.instance;
        previousData = GameMain.data;
        previousDedicated = Multiplayer.IsDedicated;

        var game = Uninitialized<GameMain>();
        AccessTools.Field(typeof(GameMain), "m_CachedPtr").SetValue(game, new IntPtr(1));
        AccessTools.Field(typeof(GameMain), "<instance>k__BackingField").SetValue(null, game);
        GameMain.data = Uninitialized<GameData>();
        GameMain.data.gameDesc = new GameDesc();
        Multiplayer.Session = Uninitialized<MultiplayerSession>();

        harmony.Patch(AccessTools.PropertyGetter(typeof(GameMain), nameof(GameMain.sandboxToolsEnabled)),
            prefix: new HarmonyMethod(typeof(GameMain_Patch), nameof(GameMain_Patch.SandboxToolsEnabled_Prefix)));
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchSelf();
        Multiplayer.Session = previousSession;
        Multiplayer.IsDedicated = previousDedicated;
        GameMain.data = previousData;
        AccessTools.Field(typeof(GameMain), "<instance>k__BackingField").SetValue(null, previousInstance);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public void SandboxSaveEnablesToolsForHostsClientsAndDedicatedServers(bool host, bool dedicated)
    {
        Multiplayer.Session.LocalPlayer = new LocalPlayer { IsHost = host };
        Multiplayer.IsDedicated = dedicated;
        GameMain.data.gameDesc.isSandboxMode = true;

        // A load preference or a join snapshot can still write the vanilla local flag.
        GameMain.sandboxToolsEnabled = false;
        TestAssert.IsTrue(GameMain.sandboxToolsEnabled);
        GameMain.sandboxToolsEnabled = true;
        TestAssert.IsTrue(GameMain.sandboxToolsEnabled);
        GameMain.sandboxToolsEnabled = false;
        TestAssert.IsTrue(GameMain.sandboxToolsEnabled);
    }

    [TestMethod]
    public void NormalSaveCannotEnableToolsWithALocalFlag()
    {
        GameMain.data.gameDesc.isSandboxMode = false;
        GameMain.sandboxToolsEnabled = true;
        TestAssert.IsFalse(GameMain.sandboxToolsEnabled);
    }

    [TestMethod]
    public void WorldModeChangesTakeEffectWithoutASeparateToolsUpdate()
    {
        GameMain.sandboxToolsEnabled = false;
        TestAssert.IsFalse(GameMain.sandboxToolsEnabled);
        GameMain.data.gameDesc.isSandboxMode = true;
        TestAssert.IsTrue(GameMain.sandboxToolsEnabled);
        GameMain.data.gameDesc.isSandboxMode = false;
        TestAssert.IsFalse(GameMain.sandboxToolsEnabled);
    }

    [TestMethod]
    public void SinglePlayerKeepsItsIndependentToolsSwitch()
    {
        Multiplayer.Session = null;
        GameMain.data.gameDesc.isSandboxMode = true;
        GameMain.sandboxToolsEnabled = false;
        TestAssert.IsFalse(GameMain.sandboxToolsEnabled);
        GameMain.sandboxToolsEnabled = true;
        TestAssert.IsTrue(GameMain.sandboxToolsEnabled);
    }

    [TestMethod]
    public void BeforeWorldCreationTheVanillaFlagStillWorks()
    {
        GameMain.data = null;
        GameMain.sandboxToolsEnabled = false;
        TestAssert.IsFalse(GameMain.sandboxToolsEnabled);
        GameMain.sandboxToolsEnabled = true;
        TestAssert.IsTrue(GameMain.sandboxToolsEnabled);
    }
}
