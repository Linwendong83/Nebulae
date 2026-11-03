using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;
using UnityEngine.UI;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
[DoNotParallelize]
public class DarkFogMonitorEntryTest
{
    private readonly Harmony harmony = new("nebulae.tests.dark-fog-monitor-entry");
    private MultiplayerSession? previousSession;
    private GameData previousGame = null!;
    private Player player = null!;
    private UIDarkFogMonitorEntry entry = null!;
    private PlanetData planet = null!;
    private bool nextListenerCalled;
    private static string? renderedLocation;
    private static readonly MethodInfo Refresh = AccessTools.Method(typeof(UIDarkFogMonitorEntry),
        nameof(UIDarkFogMonitorEntry.RefreshPlanetNameText));

    [TestInitialize]
    public void SetUp()
    {
        previousSession = Multiplayer.Session;
        previousGame = GameMain.data;
        renderedLocation = null;
        nextListenerCalled = false;
        Multiplayer.Session = Uninitialized<MultiplayerSession>();
        Multiplayer.Session.Client = Uninitialized<global::NebulaeNetwork.Client>();

        planet = new PlanetData { id = 101, name = "Test planet", star = new StarData { id = 1, name = "Test star" } };
        var game = Uninitialized<GameData>();
        game.preferences = new GamePrefsData();
        SetBackingField(game, "localPlanet", planet);
        SetBackingField(game, "localStar", planet.star);
        GameMain.data = game;
        player = Uninitialized<Player>();
        var monitor = Uninitialized<UIDarkFogMonitor>();
        monitor.gameData = game;
        monitor.starmap = Uninitialized<UIStarmap>();
        entry = Uninitialized<UIDarkFogMonitorEntry>();
        entry.monitor = monitor;
        var groundSystem = Uninitialized<EnemyDFGroundSystem>();
        groundSystem.planet = planet;
        entry.targetBase = new DFGBaseComponent
        {
            id = 1,
            groundSystem = groundSystem,
            forms = [],
            incomingSkills = []
        };
        AccessTools.Field(typeof(UIDarkFogMonitorEntry), "locationPlanet").SetValue(entry, planet);
        player.onLocalAstroChange += entry.OnLocalAstroChanged;
        player.onLocalAstroChange += () => nextListenerCalled = true;

        var patch = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType(
            "NebulaePatcher.Patches.Dynamic.UI.UIDarkFogMonitorEntry_Patch", true)!;
        harmony.CreateClassProcessor(patch).Patch();
        // Keep native name selection; shim Unity rendering and the closed starmap's canvas read.
        harmony.Patch(Refresh, transpiler: new HarmonyMethod(typeof(DarkFogMonitorEntryTest), nameof(WithoutTextRendering)));
    }

    [TestCleanup]
    public void TearDown()
    {
        harmony.UnpatchSelf();
        Multiplayer.Session = previousSession!;
        GameMain.data = previousGame;
    }

    [TestMethod]
    [DataRow("missing-base")]
    [DataRow("zero-id")]
    [DataRow("negative-id")]
    [DataRow("missing-system")]
    [DataRow("missing-planet")]
    [DataRow("freed-pool")]
    public void InvalidClientBaseDoesNotInterruptAstroChangeListeners(string state)
    {
        var target = entry.targetBase;
        switch (state)
        {
            case "missing-base": entry.targetBase = null; break;
            case "zero-id": target.id = 0; break;
            case "negative-id": target.id = -1; break;
            case "missing-system": target.groundSystem = null; break;
            case "missing-planet": target.groundSystem.planet = null; break;
            case "freed-pool":
                var pool = new ObjectPool<DFGBaseComponent>();
                pool.Reset();
                pool.buffer[1] = target;
                pool.cursor = 2;
                pool.Free();
                TestAssert.AreEqual(0, target.id);
                TestAssert.IsNull(target.groundSystem);
                break;
            default: throw new ArgumentException(state);
        }

        player.NotifyLocalAstroChange();

        TestAssert.IsTrue(nextListenerCalled, "A stale monitor must not interrupt other astro-change listeners.");
        TestAssert.IsNull(renderedLocation, "Invalid bases must wait for the monitor's normal entry refresh.");
        TestAssert.AreSame(planet, AccessTools.Field(typeof(UIDarkFogMonitorEntry), "locationPlanet").GetValue(entry));
        TestAssert.AreSame(state == "missing-base" ? null : target, entry.targetBase);
    }

    [TestMethod]
    [DataRow("client", "planet")]
    [DataRow("client", "gas")]
    [DataRow("client", "space")]
    [DataRow("host", "planet")]
    [DataRow("host", "gas")]
    [DataRow("host", "space")]
    [DataRow("single-player", "planet")]
    [DataRow("single-player", "gas")]
    [DataRow("single-player", "space")]
    public void ValidBasesKeepNativeNameSelection(string mode, string location)
    {
        SetMode(mode);
        if (location == "gas")
            SetBackingField(GameMain.data, "localPlanet", new PlanetData { id = 102, type = EPlanetType.Gas, star = planet.star });
        else if (location == "space")
        {
            SetBackingField(GameMain.data, "localPlanet", null);
            SetBackingField(GameMain.data, "localStar", null);
        }

        player.NotifyLocalAstroChange();

        var expected = location == "planet" ? "" : location == "gas" ? "Test planet" : "Test planet, Test star";
        TestAssert.AreEqual(expected, renderedLocation);
        TestAssert.IsTrue(nextListenerCalled);
    }

    [TestMethod]
    [DataRow("host")]
    [DataRow("single-player")]
    public void NonClientsStillExecuteTheOriginalMethodForInvalidBases(string mode)
    {
        SetMode(mode);
        entry.targetBase.groundSystem = null;

        // The original dereference proves the client guard has not changed these modes.
        TestAssert.ThrowsExactly<NullReferenceException>(() => player.NotifyLocalAstroChange());
        TestAssert.IsFalse(nextListenerCalled);
    }

    private static void SetMode(string mode)
    {
        if (mode == "single-player") Multiplayer.Session = null!;
        else if (mode == "host")
        {
            Multiplayer.Session.Client = null;
            Multiplayer.Session.Server = Uninitialized<global::NebulaeNetwork.Server>();
        }
    }

    private static void RenderLocation(Text _, string value) => renderedLocation = value;

    private static bool StarmapClosed(UIStarmap _) => false;

    private static IEnumerable<CodeInstruction> WithoutTextRendering(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.ToList();
        var widthStart = codes.FindIndex(code => code.Calls(AccessTools.PropertyGetter(typeof(Text), "preferredWidth"))) - 2;
        TestAssert.IsTrue(widthStart >= 0 && codes[widthStart].opcode == OpCodes.Ldarg_0 &&
            codes[widthStart + 1].LoadsField(AccessTools.Field(typeof(UIDarkFogMonitorEntry), "locationText")),
            "Expected the installed game's text-sizing block.");
        var done = new CodeInstruction(OpCodes.Ret);
        done.labels.AddRange(codes[widthStart].labels);
        codes.RemoveRange(widthStart, codes.Count - widthStart);
        codes.Add(done);
        var setter = AccessTools.PropertySetter(typeof(Text), "text");
        var starmapOpen = AccessTools.PropertyGetter(typeof(UIStarmap), "isFullOpened");
        TestAssert.AreEqual(2, codes.Count(code => code.Calls(setter)));
        foreach (var code in codes)
        {
            if (code.Calls(setter))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(DarkFogMonitorEntryTest), nameof(RenderLocation));
            }
            else if (code.Calls(starmapOpen))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(DarkFogMonitorEntryTest), nameof(StarmapClosed));
            }
        }
        return codes;
    }
}
