using System.Reflection;
using HarmonyLib;
using global::NebulaeModel.DataStructures;
using NebulaeWorld;
using NebulaeWorld.Combat;
using UnityEngine;
using NebulaeModel.Packets.Players;
using NebulaeWorld.MonoBehaviours.Remote;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class MechaShieldDamageTest
{
    [TestMethod]
    public void RemoteCombatLimitsEnableNativeBurstChargingAndDamageWithoutResettingPersonalState()
    {
        var history = Uninitialized<GameHistoryData>(); history.energyDamageScale = 1;
        var host = new Mecha
        {
            energyShieldBurstUnlocked = true,
            energyShieldBurstDamageRate = 500,
            energyShieldEnergyRate = 500,
            energyShieldCapacity = 1000000000
        };
        var pilot = new Mecha
        {
            history = history,
            energyShieldEnergy = 800000000,
            energyShieldRechargeEnabled = false,
            energyShieldRechargeSpeed = 1.5f,
            energyShieldBurstProgress = .1
        };
        var ground = pilot.groundCombatModule = new CombatModuleComponent { attackBuilding = false };
        CombatAuthorityManager.RefreshCombatLimits(pilot, host);
        TestAssert.IsTrue(pilot.energyShieldBurstUnlocked);
        TestAssert.AreEqual(500L, pilot.energyShieldBurstDamageRate);
        TestAssert.AreEqual(500L, pilot.energyShieldEnergyRate);
        TestAssert.AreEqual(.1, pilot.energyShieldBurstProgress);
        TestAssert.IsFalse(pilot.energyShieldRechargeEnabled);
        TestAssert.AreEqual(1.5f, pilot.energyShieldRechargeSpeed);
        TestAssert.AreSame(ground, pilot.groundCombatModule);
        TestAssert.IsFalse(ground.attackBuilding);
        for (var i = 0; i < 60; i++) pilot.ChargeShieldBurst();
        TestAssert.IsTrue(pilot.energyShieldBurstReady);
        TestAssert.IsGreaterThan(0, pilot.energyShieldBurstCurrentDamage);
        var energy = pilot.energyShieldEnergy;
        var cost = (long)(pilot.energyShieldBurstProgress * pilot.energyShieldCapacity + .5);
        pilot.ShieldBurstComsumeEnergy(); pilot.ResetShieldBurstProgress();
        TestAssert.AreEqual(energy - cost, pilot.energyShieldEnergy);
        TestAssert.AreEqual(0d, pilot.energyShieldBurstProgress);
    }

    [TestMethod]
    public void MovementFlagsNeverOverwriteShieldAbsorptionOrEnergy()
    {
        var harmony = new Harmony("nebulae.tests.movement-shield-rate");
        // Leave the state-writing body intact; only Unity presentation calls need a test shim.
        harmony.Patch(AccessTools.Method(typeof(SimulatedWorld), nameof(SimulatedWorld.UpdateRemotePlayerRealtimeState)),
            transpiler: new HarmonyMethod(typeof(MechaShieldDamageTest), nameof(WithoutMovementPresentation)));
        try
        {
            var world = new SimulatedWorld();
            var model = Uninitialized<RemotePlayerModel>();
            model.MechaInstance = Uninitialized<Mecha>();
            model.MechaInstance.energyShieldEnergyRate = 500;
            model.MechaInstance.energyShieldEnergy = 100000000;
            using (world.GetRemotePlayersModels(out var players)) players.Add(2, model);
            foreach (var flags in new[] { PlayerMovement.EFlags.hasShield, (PlayerMovement.EFlags)0,
                         PlayerMovement.EFlags.hasShield | PlayerMovement.EFlags.chargeShieldBurst })
            {
                world.UpdateRemotePlayerRealtimeState(new PlayerMovementDisplay { PlayerId = 2, Flags = flags });
                TestAssert.AreEqual(500L, model.MechaInstance.energyShieldEnergyRate,
                    "Reusing this field as 1/2 makes the shield 500/250 times harder to exhaust.");
                TestAssert.AreEqual(100000000L, model.MechaInstance.energyShieldEnergy);
            }
        }
        finally { harmony.UnpatchSelf(); }
    }

    private static IEnumerable<CodeInstruction> WithoutMovementPresentation(IEnumerable<CodeInstruction> codes)
    {
        var move = AccessTools.Method(typeof(RemotePlayerMovement), nameof(RemotePlayerMovement.UpdatePosition));
        var animate = AccessTools.Method(typeof(RemotePlayerAnimation), nameof(RemotePlayerAnimation.UpdateState));
        foreach (var code in codes)
        {
            if (code.Calls(move) || code.Calls(animate))
            {
                var pop = new CodeInstruction(System.Reflection.Emit.OpCodes.Pop);
                pop.labels.AddRange(code.labels); pop.blocks.AddRange(code.blocks);
                yield return pop;
                yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Pop);
            }
            else yield return code;
        }
    }

    [TestMethod]
    public void CombatPropertyPrefixesMatchTheActualGameReturnTypes()
    {
        var patch = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Authority.RemoteCombatPose_Patch", true)!;
        foreach (var method in patch.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            var result = method.GetParameters().SingleOrDefault(p => p.Name == "__result");
            if (result == null) continue;
            var target = method.GetCustomAttributes<HarmonyPatch>().Single().info.methodName;
            TestAssert.AreEqual(AccessTools.PropertyGetter(typeof(Mecha), target).ReturnType,
                result.ParameterType.GetElementType(), target);
        }
    }

    private static SkillSFXHolder silentAudio;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter",
        Justification = "This replacement call must have the same stack signature as SkillAudioLogic.AddPlayerAudio.")]
    private static ref SkillSFXHolder SilentAudio(SkillAudioLogic _, int proto, float life, Vector3 position, int channel) => ref silentAudio;
    private static bool Center(ref Vector3 __result) { __result = default; return false; }
    private static bool Kill(Player __instance) { __instance.isAlive = false; __instance.deathCount++; return false; }
    private static IEnumerable<CodeInstruction> WithoutAudio(IEnumerable<CodeInstruction> codes)
    {
        var audio = AccessTools.Method(typeof(SkillAudioLogic), nameof(SkillAudioLogic.AddPlayerAudio));
        foreach (var code in codes)
        {
            if (code.Calls(audio)) { code.opcode = System.Reflection.Emit.OpCodes.Call; code.operand = AccessTools.Method(typeof(MechaShieldDamageTest), nameof(SilentAudio)); }
            yield return code;
        }
    }

    [TestMethod]
    [DataRow(5000000L, 0, 100000, 4000000L)]
    [DataRow(500000L, 0, 99000, 0L)]
    [DataRow(0L, 0, 98000, 0L)]
    [DataRow(5000000L, 180, 100000, 5000000L)]
    public void NativeShieldAbsorbsDamageBeforeTheBodyAndHonorsRespawnProtection(long shield, int protection, int hp, long expectedShield)
    {
        var oldGame = GameMain.data; var oldSession = Multiplayer.Session; var oldDedicated = Multiplayer.IsDedicated;
        var harmony = new Harmony("nebulae.tests.shield-damage");
        // Patch the getter before the caller is JIT-compiled, so its Unity body cannot be inlined.
        harmony.Patch(AccessTools.PropertyGetter(typeof(Mecha), nameof(Mecha.skillTargetLCenter)), prefix: new HarmonyMethod(typeof(MechaShieldDamageTest), nameof(Center)));
        harmony.Patch(AccessTools.Method(typeof(Mecha), nameof(Mecha.EnergyShieldResist), [typeof(int).MakeByRefType()]),
            transpiler: new HarmonyMethod(typeof(MechaShieldDamageTest), nameof(WithoutAudio)));
        harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.Kill)), prefix: new HarmonyMethod(typeof(MechaShieldDamageTest), nameof(Kill)));
        try
        {
            var game = Uninitialized<GameData>(); var sector = Uninitialized<SpaceSector>(); var skills = Uninitialized<SkillSystem>();
            game.spaceSector = sector; sector.skillSystem = skills;
            var player = Uninitialized<Player>(); var mecha = Uninitialized<Mecha>();
            SetBackingField(player, "mecha", mecha); SetBackingField(game, "mainPlayer", player);
            player.isAlive = true; player.invincibleTicks = protection;
            mecha.player = player; mecha.gameData = game; mecha.hp = 100000;
            mecha.energyShieldEnergy = shield; mecha.energyShieldEnergyRate = 500;
            mecha.energyShieldRechargeEnabled = false;
            skills.mecha = mecha; GameMain.data = game;
            var session = Uninitialized<MultiplayerSession>();
            SetBackingField(session, "Server", Uninitialized<global::NebulaeNetwork.Server>());
            session.LocalPlayer = new LocalPlayer { Data = new PlayerData(2, 101) };
            session.CombatAuthority = new CombatAuthorityManager();
            Multiplayer.Session = session; Multiplayer.IsDedicated = false;
            var patch = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Dynamic.Combat.SkillSystem_Patch", true)!;
            harmony.Patch(AccessTools.Method(typeof(SkillSystem), nameof(SkillSystem.MechaEnergyShieldResist),
                    [typeof(SkillTargetLocal), typeof(int), typeof(int).MakeByRefType()]),
                prefix: new HarmonyMethod(patch.GetMethod("MechaEnergyShieldResist_Prefix")!),
                finalizer: new HarmonyMethod(patch.GetMethod("ShieldResist_Finalizer")!));
            var damage = 2000;
            skills.MechaEnergyShieldResist(new SkillTargetLocal { id = 7, type = ETargetType.Enemy }, 101, ref damage);
            if (damage > 0) mecha.TakeDamage(damage);
            TestAssert.AreEqual(hp, mecha.hp);
            TestAssert.AreEqual(expectedShield, mecha.energyShieldEnergy);
            TestAssert.AreEqual(7, mecha.ammoHatredTarget.id);
            TestAssert.AreEqual(7, mecha.laserHatredTarget.id);
            if (shield > 0 && expectedShield == 0) TestAssert.AreEqual(150, mecha.energyShieldRecoverCD);
            TestAssert.IsFalse(mecha.energyShieldRechargeEnabled, "Disabling recharge does not bypass residual shield energy.");
        }
        finally { harmony.UnpatchSelf(); GameMain.data = oldGame; Multiplayer.Session = oldSession; Multiplayer.IsDedicated = oldDedicated; CombatTargetContext.TargetMecha = null; }
    }

    [TestMethod]
    public void DedicatedPlaceholderIsNotAValidCollisionTargetAndExceptionalScopesRestore()
    {
        var oldSession = Multiplayer.Session; var oldDedicated = Multiplayer.IsDedicated;
        try
        {
            var session = Uninitialized<MultiplayerSession>(); session.CombatAuthority = new CombatAuthorityManager();
            session.LocalPlayer = new LocalPlayer { Data = new PlayerData(1, 101) };
            Multiplayer.Session = session; Multiplayer.IsDedicated = true;
            TestAssert.AreEqual(0, CombatTargetContext.PlayerIdFor(Uninitialized<Mecha>()));
            TestAssert.IsFalse(CombatTargetContext.IsPlayerTarget(1));
            var original = Uninitialized<Mecha>(); var target = Uninitialized<Mecha>();
            var skills = Uninitialized<SkillSystem>(); skills.mecha = original; skills.playerEnergyShieldRadius = 10;
            CombatTargetContext.TargetMecha = target;
            var scope = new PlayerSkillScope(skills);
            skills.mecha = target; skills.playerEnergyShieldRadius = 200; CombatTargetContext.TargetMecha = null;
            var error = new InvalidOperationException("fixture");
            var finalizer = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Authority.CombatTargetScope_Patch", true)!.GetMethod("Finalizer")!;
            // The scope now rides on a per-thread stack instead of a Harmony __state parameter,
            // so drive the exceptional-exit path the way the prefixes do: push, then finalize.
            ScopeStack<PlayerSkillScope>.Push(scope);
            TestAssert.AreSame(error, finalizer.Invoke(null, [error]));
            TestAssert.AreSame(original, skills.mecha); TestAssert.AreSame(target, CombatTargetContext.TargetMecha);
            TestAssert.AreEqual(10f, skills.playerEnergyShieldRadius);
        }
        finally { Multiplayer.Session = oldSession; Multiplayer.IsDedicated = oldDedicated; CombatTargetContext.TargetMecha = null; }
    }
}
