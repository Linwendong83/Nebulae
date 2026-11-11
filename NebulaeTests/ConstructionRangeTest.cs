using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Factory;
using UnityEngine;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class ConstructionRangeTest
{
    private static bool Position(ref Vector3 __result) { __result = new Vector3(0, 200, 0); return false; }
    private static bool Speed(ref float __result) { __result = 0; return false; }
    private static Vector3 PlayerPosition(Player player) => new(0, 200, 0);
    private static float PlayerSpeed(Player player) => 0;
    private static IEnumerable<CodeInstruction> WithoutUnityPose(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var code in instructions)
        {
            if (code.Calls(AccessTools.PropertyGetter(typeof(Player), nameof(Player.position))))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(ConstructionRangeTest), nameof(PlayerPosition));
            }
            if (code.Calls(AccessTools.PropertyGetter(typeof(Player), nameof(Player.speed))))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(ConstructionRangeTest), nameof(PlayerSpeed));
            }
            yield return code;
        }
    }

    [TestMethod]
    [DataRow(39f, true)]
    [DataRow(40f, true)]
    [DataRow(40.01f, false)]
    [DataRow(300f, false)]
    public void NativeConstructionAndMultiplayerRepairUseTheActualMechaRange(float distance, bool expected)
    {
        var previousGame = GameMain.data;
        var previousSession = Multiplayer.Session;
        var harmony = new Harmony("nebulae.tests.construction-range");
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(Player), nameof(Player.position)),
                prefix: new HarmonyMethod(typeof(ConstructionRangeTest), nameof(Position)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(PlayerController), nameof(PlayerController.speed)),
                prefix: new HarmonyMethod(typeof(ConstructionRangeTest), nameof(Speed)));
            // Replace engine pose reads at their call sites; release JIT can inline the native getters.
            foreach (var method in new[] { AccessTools.Method(typeof(BuildDispatchManager), nameof(BuildDispatchManager.CanQueue)),
                         AccessTools.Method(typeof(RepairDispatchManager), nameof(RepairDispatchManager.WithinRepairRange)) })
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(ConstructionRangeTest), nameof(WithoutUnityPose)));
            var game = Uninitialized<GameData>(); GameMain.data = game;
            var player = Uninitialized<Player>(); player.isAlive = true;
            SetBackingField(player, "controller", Uninitialized<PlayerController>());
            var module = new ConstructionModuleComponent { droneEnabled = true, droneConstructEnabled = true, droneCount = 3 };
            var mecha = new Mecha { player = player, buildArea = 40, constructionModule = module };
            SetBackingField(player, "mecha", mecha);
            SetBackingField(game, "mainPlayer", player);
            game.history = new GameHistoryData { constructionDroneMovement = 1 };
            var factory = Uninitialized<PlanetFactory>();
            var planet = new PlanetData { id = 101, factory = factory, factoryLoaded = true };
            SetBackingField(factory, "planet", planet); SetBackingField(factory, "gameData", game);
            SetBackingField(player, "planetData", planet);
            factory.prebuildCursor = factory.entityCursor = 2;
            var pos = new Vector3(distance, 200, 0);
            factory.prebuildPool = [default, new PrebuildData { id = 1, pos = pos }];
            factory.entityPool = [default, new EntityData { id = 1, pos = pos }];
            factory.constructionSystem = new ConstructionSystem(planet);
            var session = Uninitialized<MultiplayerSession>(); Multiplayer.Session = session;
            session.Server = Uninitialized<global::NebulaeNetwork.Server>();
            var dispatch = new BuildDispatchManager();

            // Execute the installed game's immediate registration path, used by CreatePrebuilds.
            factory.constructionSystem.AddBuildTargetToModules(1, ref pos);
            Check.AreEqual(expected, module.buildTargets?.Any(target => target.objectId == 1) == true);
            Check.AreEqual(expected, dispatch.CanQueue(factory, module, 1));
            Check.AreEqual(expected, RepairDispatchManager.WithinRepairRange(factory, mecha, 1));
            Check.AreEqual(40f, mecha.buildArea);
            SetBackingField(player, "planetData", new PlanetData { id = 102, factoryLoaded = true });
            Check.IsFalse(RepairDispatchManager.WithinRepairRange(factory, mecha, 1));
        }
        finally { harmony.UnpatchSelf(); GameMain.data = previousGame; Multiplayer.Session = previousSession; }
    }

    [TestMethod]
    public void RemotePrebuildReplayCannotExpandTheHostRangeDuringNativeTargetRegistration()
    {
        // Restoring the range after replay is too late: native creation already queued targets.
        var codes = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(BuildToolManager), nameof(BuildToolManager.CreatePrebuildsRequest)));
        var range = AccessTools.Field(typeof(Mecha), nameof(Mecha.buildArea));
        Check.IsFalse(codes.Any(code => code.opcode == OpCodes.Stfld && Equals(code.operand, range)));
    }
}
