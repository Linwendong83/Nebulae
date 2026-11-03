using System.Reflection;
using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Utils;
using NebulaeWorld.Combat;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeModel.Authority;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class LocalCombatPredictionTest
{
    private static Mecha RecoveryMecha()
    {
        var player = Uninitialized<Player>(); player.isAlive = true;
        return new Mecha
        {
            player = player,
            history = new GameHistoryData(),
            hp = 500,
            hpMax = 1000,
            hpRecover = 3,
            hpRecoverCD = 2,
            coreEnergy = 1000000,
            coreEnergyCap = 2000000,
            reactorPowerGen = 120000,
            energyShieldEnergy = 10000,
            energyShieldCapacity = 200000,
            energyShieldRechargeEnabled = true,
            energyShieldRechargeSpeed = 2,
            energyShieldRecoverCD = 3,
            laserActiveState = 2,
            laserEnergy = 10000,
            laserEnergyCapacity = 100000,
            laserRecharging = true,
            energyChanges = new double[32],
            groundCombatModule = new CombatModuleComponent(),
            spaceCombatModule = new CombatModuleComponent()
        };
    }

    [TestMethod]
    public void RecoveryMatchesVanillaWhileKeepingCheckpointValuesUntouched()
    {
        var oldTick = GameMain.gameTick;
        try
        {
            GameMain.gameTick = 100;
            var actual = RecoveryMecha(); var native = RecoveryMecha(); long tick = 100; var view = new CombatRecoveryView(() => tick); view.Get(actual);
            for (var i = 0; i < 30; i++)
            {
                tick++;
                native.UpdateCombatStats(1.0 / 60);
                view.Tick(actual, 1.0 / 60);
                var predicted = view.Get(actual);
                TestAssert.AreEqual(native.energyShieldEnergy, predicted.energyShieldEnergy);
                TestAssert.AreEqual(native.laserEnergy, predicted.laserEnergy);
                TestAssert.AreEqual(native.coreEnergy, predicted.coreEnergy, 0.01);
                TestAssert.AreEqual(native.hp, predicted.hp);
                TestAssert.AreEqual(native.energyShieldRecoverCD, predicted.energyShieldRecoverCD);
            }
            TestAssert.AreEqual(10000L, actual.energyShieldEnergy); TestAssert.AreEqual(10000L, actual.laserEnergy);
            TestAssert.AreEqual(1000000d, actual.coreEnergy); TestAssert.AreEqual(500, actual.hp);
            TestAssert.AreEqual(0d, actual.energyChanges.Sum());
            tick = 500;
            var before = view.Get(actual).laserEnergy; view.Tick(actual, 1.0 / 60);
            TestAssert.AreEqual(before, view.Get(actual).laserEnergy, "No unbounded prediction after connection stalls.");
        }
        finally { GameMain.gameTick = oldTick; }
    }

    [TestMethod]
    public void RecoveryResistHistoryDoesNotAliasTheAuthoritativeHistory()
    {
        var actual = RecoveryMecha(); actual.energyShieldResistHistory = new long[60]; actual.energyShieldResistHistory[0] = 100;
        var view = new CombatRecoveryView(); var predicted = view.Get(actual);
        predicted.energyShieldResistHistory[0] = 900;
        TestAssert.AreEqual(100L, actual.energyShieldResistHistory[0]);
        view.Spend(actual, 500, 2000, 30, true);
        TestAssert.AreEqual(9500L, view.Get(actual).laserEnergy); TestAssert.AreEqual(8000L, view.Get(actual).energyShieldEnergy);
        view.Spend(actual, -500, -2000, -30, false);
        TestAssert.AreEqual(10000L, view.Get(actual).laserEnergy); TestAssert.AreEqual(10000L, view.Get(actual).energyShieldEnergy);
    }

    [TestMethod]
    [DataRow(false, EDFTurretType.Laser)]
    [DataRow(false, EDFTurretType.Plasma)]
    [DataRow(true, EDFTurretType.Laser)]
    [DataRow(true, EDFTurretType.Plasma)]
    public void NativeTurretWirePreservesPlayerTargetAndRejectsMalformedState(bool space, EDFTurretType type)
    {
        var bytes = space ? NativeDarkFogTurrets.Export(new DFSTurretComponent
        {
            id = 3,
            enemyId = 8,
            type = type,
            state = EDFTurretState.Aiming,
            target = new SkillTargetLocal { type = ETargetType.Player, id = 2 },
            localDir = Vector3.up
        }) :
            NativeDarkFogTurrets.Export(new DFGTurretComponent
            {
                id = 3,
                enemyId = 8,
                type = type,
                state = EDFTurretState.Aiming,
                target = new SkillTargetLocal { type = ETargetType.Player, id = 2 },
                localDir = Vector3.up
            });
        TestAssert.IsTrue(EnemyTurretStateCodec.Valid(bytes, space));
        TestAssert.IsFalse(EnemyTurretStateCodec.Valid(bytes.Take(bytes.Length - 1).ToArray(), space));
        TestAssert.IsFalse(EnemyTurretStateCodec.Valid(bytes.Concat(new byte[] { 0 }).ToArray(), space));
    }

    [TestMethod]
    public void ShieldBurstInstallsInNativePoolWithDamageIntactAndAPlanetDependencyOnlyForGround()
    {
        var skills = Uninitialized<SkillSystem>(); skills.mechaShieldBursts = new DataPoolRendererMultiMat<GeneralShieldBurst>(); skills.mechaShieldBursts.ResetPool();
        var pool = NativePlayerWeaponPool.Create(skills).Single(x => x.Kind == BattleEffectKind.MechaShieldBurst);
        var burst = new GeneralShieldBurst
        {
            id = 22,
            astroId = 101,
            life = 45,
            lifeMax = 45,
            damageInterval = 5,
            damageTotal = 2000,
            range = 42,
            caster = new SkillTarget { id = 2, type = ETargetType.Player }
        };
        var id = pool.Install(NativeDarkFogTurrets.Export(burst));
        var restored = (GeneralShieldBurst)pool.Read(id);
        TestAssert.AreEqual(1, id); TestAssert.AreEqual(2000, restored.damageTotal); TestAssert.AreEqual(101, NativePlayerWeaponPool.RequiredPlanet(restored));
        restored.astroId = 100;
        TestAssert.AreEqual(0, NativePlayerWeaponPool.RequiredPlanet(restored));
    }

    [TestMethod]
    public void ImpactWireRetainsItsNativeSourceForDeduplication()
    {
        var frame = new BattleVisualFrame(); frame.Effects.Add(new BattleEffectData
        {
            Kind = BattleEffectKind.Impact,
            OriginKind = BattleEffectKind.GroundTowerLaser,
            SourceAstroId = 101,
            SourceObjectId = 93,
            SourceGeneration = 7,
            Id = 1,
            Generation = 2,
            Tick = 300,
            Life = 10,
            Payload = new byte[] { 1 }
        });
        var copy = BattleVisualFrame.Import(frame.Export()).Effects.Single();
        TestAssert.AreEqual(BattleEffectKind.GroundTowerLaser, copy.OriginKind); TestAssert.AreEqual(93, copy.SourceObjectId); TestAssert.AreEqual(7L, copy.SourceGeneration);
    }

    [TestMethod]
    public void SpaceTurretChecksItsTargetPilotInsteadOfTheHostCameraPilot()
    {
        var patch = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Authority.NativeDarkFogTurret_Patch", true)!;
        var original = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(DFSTurretComponent), nameof(DFSTurretComponent.Aim))).ToArray();
        var getter = AccessTools.PropertyGetter(typeof(Player), nameof(Player.sailing));
        TestAssert.AreEqual(1, original.Count(x => x.Calls(getter)));
        var patched = ((IEnumerable<CodeInstruction>)patch.GetMethod("Transpiler", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { original })!).ToArray();
        TestAssert.IsFalse(patched.Any(x => x.Calls(getter)));
        TestAssert.AreEqual(1, patched.Count(x => x.Calls(AccessTools.Method(patch, "TargetSailing"))));
    }

    [TestMethod]
    public void RejectedFleetCommandRollsBackTheReplacementModuleFromTheLatestSnapshot()
    {
        var oldData = GameMain.data; var oldSession = Multiplayer.Session;
        var prediction = new FleetCommandPrediction();
        try
        {
            var game = Uninitialized<GameData>(); var player = Uninitialized<Player>(); var mecha = new Mecha { player = player };
            player.isAlive = true; player.SetHiddenProperty(nameof(Player.mecha), mecha);
            game.SetHiddenProperty(nameof(GameData.mainPlayer), player); game.SetHiddenProperty(nameof(GameData.localStar), new StarData { id = 1 });
            GameMain.data = game;
            var session = Uninitialized<MultiplayerSession>(); session.SetHiddenProperty(nameof(MultiplayerSession.Authority), new AuthoritySessionState());
            Multiplayer.Session = session;
            var original = new CombatModuleComponent
            {
                isSpace = true,
                moduleEnabled = true,
                fleetCount = 1,
                moduleFleets = new[] { new ModuleFleet { fleetEnabled = true, fighters = Array.Empty<ModuleFighter>() } }
            };
            mecha.spaceCombatModule = original;
            prediction.Begin(original, 0, false, 7);
            TestAssert.IsTrue(original.moduleFleets[0].inCommand);
            var snapshot = new CombatModuleComponent
            {
                isSpace = true,
                moduleEnabled = true,
                fleetCount = 1,
                moduleFleets = new[] { new ModuleFleet { fleetEnabled = true, inCommand = true, fighters = Array.Empty<ModuleFighter>() } }
            };
            mecha.spaceCombatModule = snapshot;
            prediction.Tick();
            TestAssert.AreEqual(1, prediction.PendingCount);
            prediction.Receive(new AuthorityCommandResultPacket { Sequence = 7, ResultCode = (byte)CommandResultCode.RejectedResource });
            prediction.Tick();
            TestAssert.IsFalse(snapshot.moduleFleets[0].inCommand, "Rollback must reach the newly imported module.");
            TestAssert.AreEqual(0, prediction.PendingCount);
        }
        finally { prediction.Dispose(); GameMain.data = oldData; Multiplayer.Session = oldSession; }
    }

    [TestMethod]
    public void RemoteFleetCanLaunchFromAFactoryWithoutHostTerrainRendering()
    {
        var oldSession = Multiplayer.Session;
        try
        {
            var player = Uninitialized<Player>();
            var factory = Uninitialized<PlanetFactory>(); factory.combatGroundSystem = Uninitialized<CombatGroundSystem>();
            player.SetHiddenProperty(nameof(Player.planetData), new PlanetData { id = 104, factory = factory, factoryLoaded = false });
            var mecha = new Mecha { player = player };
            var session = Uninitialized<MultiplayerSession>();
            session.SetHiddenProperty(nameof(MultiplayerSession.Server), Uninitialized<global::NebulaeNetwork.Server>());
            session.CombatAuthority = new CombatAuthorityManager(); Multiplayer.Session = session;
            var actorType = typeof(CombatAuthorityManager).GetNestedType("Actor", BindingFlags.NonPublic)!;
            var actor = Activator.CreateInstance(actorType, true)!;
            actorType.GetField("Player")!.SetValue(actor, player); actorType.GetField("Mecha")!.SetValue(actor, mecha);
            ((System.Collections.IDictionary)AccessTools.Field(typeof(CombatAuthorityManager), "actors").GetValue(session.CombatAuthority)).Add((ushort)2, actor);
            var patch = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Authority.PlayerCombatAuthority_Patch", true)!
                .GetMethod("CheckLaunchFleetCondition")!;
            object[] args = { mecha, false, false };
            TestAssert.IsFalse((bool)patch.Invoke(null, args)!); TestAssert.IsTrue((bool)args[2]);
            player.movementState = EMovementState.Sail;
            patch.Invoke(null, args); TestAssert.IsFalse((bool)args[2], "Ground launch still obeys the native movement restriction.");
            args[1] = true; patch.Invoke(null, args); TestAssert.IsTrue((bool)args[2]);
        }
        finally { Multiplayer.Session = oldSession; }
    }

    [TestMethod]
    public void NativeFighterCreationAndLossDoNotWriteTheCameraPilotsFleet()
    {
        var patch = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Authority.FleetOwnerReferences_Patch", true)!;
        var transpile = patch.GetMethod("Transpiler", BindingFlags.Static | BindingFlags.NonPublic)!;
        {
            foreach (var type in new[] { typeof(PlanetFactory), typeof(SpaceSector) })
                foreach (var name in new[] { "SetupReferenceOnCraftCreate", type == typeof(PlanetFactory) ? "KillCraftFinally" : "KillCraftFinal" })
                {
                    var code = ((IEnumerable<CodeInstruction>)transpile.Invoke(null, new object[] { PatchProcessor.GetOriginalInstructions(AccessTools.Method(type, name)) })!).ToArray();
                    TestAssert.IsTrue(code.Any(x => x.Calls(AccessTools.Method(patch, "Module"))));
                    TestAssert.IsFalse(code.Any(x => x.LoadsField(AccessTools.Field(typeof(Mecha), nameof(Mecha.groundCombatModule))) ||
                        x.LoadsField(AccessTools.Field(typeof(Mecha), nameof(Mecha.spaceCombatModule)))));
                }
        }
    }

    [TestMethod]
    public void DedicatedSpaceFleetHasSafeRendererSlotsWithoutAllocatingGpuObjects()
    {
        var oldDedicated = Multiplayer.IsDedicated;
        try
        {
            Multiplayer.IsDedicated = true;
            var patch = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Authority.SpaceFleetSimulation_Patch", true)!;
            var method = patch.GetMethod("Renderer", BindingFlags.Static | BindingFlags.NonPublic)!;
            TestAssert.IsNull(method.Invoke(null, new object[] { Array.Empty<SpaceObjectRenderer>(), 511 }));
            Multiplayer.IsDedicated = false;
            TestAssert.IsNull(method.Invoke(null, new object[] { Array.Empty<SpaceObjectRenderer>(), 511 }));
            var code = ((IEnumerable<CodeInstruction>)patch.GetMethod("Transpiler", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                new object[] { PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(CombatSpaceSystem), nameof(CombatSpaceSystem.GameTick))) })!).ToArray();
            TestAssert.AreEqual(2, code.Count(x => x.Calls(method)), "Both fleet and fighter renderer indexing must be guarded.");
        }
        finally { Multiplayer.IsDedicated = oldDedicated; }
    }
}
