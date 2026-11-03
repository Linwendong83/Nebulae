using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld.Authority;
using NebulaeWorld.Authority.Adapters;
using NebulaeWorld.Combat;
using global::NebulaeModel.DataStructures;
using HarmonyLib;
using System.Reflection.Emit;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
internal class GroundBasePresentationTest
{
    private static GroundBaseState State()
    {
        var forms = new byte[3][];
        for (var form = 0; form < 3; form++)
        {
            forms[form] = new byte[GroundBaseStateCodec.PortCount(form)];
            for (var port = 0; port < forms[form].Length; port++) forms[form][port] = (byte)(port % 4);
        }
        return new GroundBaseState
        {
            EnemyId = 17,
            RuinId = 2,
            CoreAlive = true,
            Evolution = [1, 2, 3],
            Incoming = 8,
            Buildings = 21,
            Units = 60,
            X = 1,
            Y = 2,
            Z = 3,
            Forms = forms
        };
    }

    [TestMethod]
    public void RoundTripPreservesAllFormationPortsAndBaseFacts()
    {
        var input = State(); var payload = GroundBaseStateCodec.Encode(input); var restored = GroundBaseStateCodec.Decode(payload);
        TestAssert.IsLessThan(GroundBaseStateCodec.MaxPayload, payload.Length);
        TestAssert.AreEqual(input.EnemyId, restored.EnemyId); TestAssert.AreEqual(input.RuinId, restored.RuinId);
        TestAssert.AreEqual(input.CoreAlive, restored.CoreAlive);
        TestAssert.AreEqual(input.Incoming, restored.Incoming); TestAssert.AreEqual(input.Units, restored.Units);
        TestAssert.AreEqual(input.Buildings, restored.Buildings); TestAssert.AreEqual(input.Z, restored.Z);
        CollectionAssert.AreEqual(input.Evolution, restored.Evolution);
        for (var form = 0; form < 3; form++) CollectionAssert.AreEqual(input.Forms[form], restored.Forms[form]);
    }

    [TestMethod]
    public void FullBaseStateFitsTheExistingReplicationRecordLimit()
    {
        var state = State(); state.Evolution = new byte[GroundBaseStateCodec.MaxEvolutionBytes];
        var payload = GroundBaseStateCodec.Encode(state);
        TestAssert.IsLessThanOrEqualTo(AuthorityLimits.StateRecordMaxBytes, payload.Length);
        var restored = GroundBaseStateCodec.Decode(payload);
        for (var form = 0; form < 3; form++) CollectionAssert.AreEqual(state.Forms[form], restored.Forms[form]);
    }

    [TestMethod]
    public void InvalidCountsTagsAndTruncationAreRejected()
    {
        var input = State(); input.Forms[0] = new byte[73];
        TestAssert.ThrowsExactly<InvalidDataException>(() => GroundBaseStateCodec.Encode(input));
        input = State(); input.Forms[0][0] = 4;
        TestAssert.ThrowsExactly<InvalidDataException>(() => GroundBaseStateCodec.Encode(input));
        var payload = GroundBaseStateCodec.Encode(State());
        payload[0] = 0; payload[1] = 0; payload[2] = 0; payload[3] = 0; // EnemyId <= 0 fails validation
        TestAssert.ThrowsExactly<InvalidDataException>(() => GroundBaseStateCodec.Decode(payload));
        payload = GroundBaseStateCodec.Encode(State()); Array.Resize(ref payload, payload.Length - 1);
        TestAssert.ThrowsExactly<EndOfStreamException>(() => GroundBaseStateCodec.Decode(payload));
        payload = GroundBaseStateCodec.Encode(State()); Array.Resize(ref payload, payload.Length + 1);
        TestAssert.ThrowsExactly<InvalidDataException>(() => GroundBaseStateCodec.Decode(payload));
    }

    [TestMethod]
    public void NativeFormationVacanciesMatchHostAfterMultipleReplacements()
    {
        var formation = new EnemyFormation();
        foreach (var tags in new[] { State().Forms[0], new byte[72], new byte[72].Select(_ => (byte)1).ToArray(), State().Forms[0] })
        {
            DarkFogReplicaBinding.InstallFormation(formation, tags);
            TestAssert.AreEqual(tags.Count(x => x > 0), formation.unitCount);
            TestAssert.AreEqual(tags.Count(x => x == 0), formation.vacancyCount);
            CollectionAssert.AreEqual(tags, formation.units.Skip(1).Select(x => (byte)x).ToArray());
            if (formation.vacancyCount > 0)
            {
                var port = formation.AddUnit();
                TestAssert.AreEqual((byte)0, tags[port - 1]);
                formation.RemoveUnit(port);
            }
        }
    }

    [TestMethod]
    public void LifecycleRunsBeforeHostFrameAndResetCancelsOldActions()
    {
        using var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(new AuthorityEpoch(10, 11), true);
        var events = new List<string>();
        session.BeforeHostFrame = _ => events.Add("frame");
        session.EnqueueLifecycle(() => events.Add("cleanup"));
        session.OnFrameBoundary(1);
        CollectionAssert.AreEqual(new[] { "cleanup", "frame" }, events);
        session.EnqueueLifecycle(() => events.Add("old")); session.Reset();
        session.BeginAuthorityWorld(new AuthorityEpoch(12, 13), true); session.OnFrameBoundary(2);
        TestAssert.DoesNotContain("old", events);
    }

    [TestMethod]
    public void LocalSkillRejectsMissingFactoriesAndRemovedEnemySlots()
    {
        var skills = Uninitialized<SkillSystem>();
        skills.astroFactories = new PlanetFactory[102];
        var caster = new SkillTargetLocal { type = ETargetType.Enemy, id = 1 };
        var target = new SkillTargetLocal { type = ETargetType.Enemy, id = 2 };
        TestAssert.IsFalse(LocalSkillLifetime.IsValid(skills, 101, caster, target));
        var factory = new PlanetFactory { enemyCursor = 3, enemyPool = new EnemyData[3] };
        skills.astroFactories[101] = factory; factory.enemyPool[1].id = 1; factory.enemyPool[2].id = 2;
        TestAssert.IsTrue(LocalSkillLifetime.IsValid(skills, 101, caster, target));
        factory.enemyPool[2].id = 0; TestAssert.IsFalse(LocalSkillLifetime.IsValid(skills, 101, caster, target));
        target.id = 0; TestAssert.IsTrue(LocalSkillLifetime.IsValid(skills, 101, caster, target));
    }

    [TestMethod]
    public void FormationActivationIntentDoesNotRequireAnAmmoType()
    {
        var payload = new CombatIntent(CombatIntentKind.ActivateGroundFormation, 1, 7).Encode();
        TestAssert.IsTrue(CombatIntent.TryDecode(payload, out var restored));
        TestAssert.AreEqual(CombatIntentKind.ActivateGroundFormation, restored.Kind);
        TestAssert.AreEqual(1, restored.Argument); TestAssert.AreEqual(7d, restored.X);
        TestAssert.IsTrue(CombatIntent.TryDecode(new CombatIntent(CombatIntentKind.Wake).Encode(), out _));
    }

    [TestMethod]
    [DataRow(BattleEffectKind.GroundLaser)]
    [DataRow(BattleEffectKind.SpaceLaser)]
    [DataRow(BattleEffectKind.MechaGroundLaser)]
    [DataRow(BattleEffectKind.MechaSpaceLaser)]
    public void HitLasersKeepTheirNativeNegativeDisplayLifetime(BattleEffectKind kind)
    {
        TestAssert.AreEqual(15, BattleVisualManager.RemainingLife(kind, -15));
        TestAssert.AreEqual(14, BattleVisualManager.RemainingLife(kind, -14));
        TestAssert.AreEqual(15, BattleVisualManager.RemainingLife(kind, 15));
        TestAssert.AreEqual(0, BattleVisualManager.RemainingLife(kind, 0));
    }

    [TestMethod]
    public void ReimportingEnemiesRequiresBaseRebindingEvenWhenTheBaseArrayIsReused()
    {
        var binding = new DarkFogReplicaBinding();
        var enemies = new EnemyData[32]; var bases = new DFGBaseComponent[8];
        var original = binding.PoolIdentity(101, enemies, bases);
        TestAssert.AreSame(original, binding.PoolIdentity(101, enemies, bases));
        var imported = binding.PoolIdentity(101, new EnemyData[32], bases);
        TestAssert.AreNotSame(original, imported);
        TestAssert.IsNull(binding.PoolIdentity(101, null!, bases));
    }

    [TestMethod]
    public void NativeProjectilePlayerHitRoutesToTheScopedPlayer()
    {
        var original = AccessTools.Method(typeof(LocalGeneralProjectile), nameof(LocalGeneralProjectile.TickSkillLogic));
        var instructions = PatchProcessor.GetOriginalInstructions(original);
        var patch = AccessTools.Method(typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Authority.ProjectilePlayerIdentity_Patch", true), "Transpiler");
        var patched = ((IEnumerable<CodeInstruction>)patch.Invoke(null, [instructions])!).ToList();
        TestAssert.IsTrue(patched.Any(i => i.Calls(AccessTools.Method(typeof(CombatTargetContext), nameof(CombatTargetContext.TargetPlayerId)))));
        TestAssert.IsFalse(patched.Zip(patched.Skip(1), (a, b) => a.opcode == OpCodes.Ldc_I4_1 && b.StoresField(AccessTools.Field(typeof(SkillTargetLocal), nameof(SkillTargetLocal.id)))).Any(x => x));
    }

    [TestMethod]
    public void ARebuiltCoreHasANewGenerationDespiteKeepingItsNativeSlot()
    {
        var adapter = new GroundEnemySnapshotAdapter(new AuthorityEpoch(33, 44));
        var factory = new PlanetFactory { enemyCursor = 2, enemyPool = new EnemyData[2] };
        AccessTools.Field(typeof(PlanetFactory), "<planet>k__BackingField").SetValue(factory, new PlanetData { id = 101 });
        factory.enemyPool[1] = new EnemyData { id = 1, dfGBaseId = 1 };
        var observe = AccessTools.Method(typeof(GroundEnemySnapshotAdapter), "Observe");
        var alive = (long)observe.Invoke(adapter, [factory, 1])!;
        factory.enemyPool[1].isInvincible = true;
        TestAssert.AreEqual(alive, (long)observe.Invoke(adapter, [factory, 1])!);
        factory.enemyPool[1].isInvincible = false;
        TestAssert.AreEqual(alive + 1, (long)observe.Invoke(adapter, [factory, 1])!);
        TestAssert.AreEqual(alive + 1, (long)observe.Invoke(adapter, [factory, 1])!);
    }

    [TestMethod]
    public void NativeMonitorReadsHostCoreStateInsteadOfTheClearedBuilderSlot()
    {
        var patch = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Authority.DarkFogMonitorPresentation_Patch", true)!;
        foreach (var name in new[] { "_OnLateUpdate", "RefreshValuesComplete", "OnAlertToggleClick" })
        {
            var original = AccessTools.Method(typeof(UIDarkFogMonitorEntry), name);
            var instructions = PatchProcessor.GetOriginalInstructions(original);
            var result = ((IEnumerable<CodeInstruction>)patch.GetMethod("Transpiler")!.Invoke(null, [instructions, original])!).ToList();
            TestAssert.AreEqual(name == "OnAlertToggleClick" ? 2 : 1,
                result.Count(x => x.Calls(patch.GetMethod("CoreProgress"))));
        }
    }

    [TestMethod]
    public void HeadlessCombatUsesFactoryDataWithoutRequiringNativeVisualLoading()
    {
        var planet = new PlanetData { id = 101, factoryLoaded = false, factory = new PlanetFactory() };
        var player = new Player();
        AccessTools.Field(typeof(Player), "<planetData>k__BackingField").SetValue(player, planet);
        TestAssert.IsNull(player.factory, "Vanilla's player factory is visually gated.");
        TestAssert.AreSame(planet.factory, CombatAuthorityManager.FactoryForCombat(planet));
        TestAssert.IsNull(CombatAuthorityManager.FactoryForCombat(null!));
    }

    [TestMethod]
    public void CoreBriefUsesRealBuilderProgressAndResourcesInsteadOfZeroOverZero()
    {
        var state = new GroundEnemyState(GroundEnemyKind.GroundBase, false, false,
            8120, 292, 1, 0, 0, 101, 101, 1, 1, 2,
            0, 200, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
            animationTime: 1, animationState: 4, animationPower: 1, builderState: 4,
            builderSp: 60, builderSpMax: 60, matter: 30000, matterMax: 30000,
            energy: 6000000, energyMax: 6000000, matterChange: 180, energyChange: -12000);
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(state, out var payload));
        TestAssert.IsLessThanOrEqualTo(AuthorityLimits.StateRecordMaxBytes, payload.Length);
        TestAssert.IsTrue(GroundEnemyStateCodec.TryDecode(payload, 0, payload.Length, out var restored, out _));
        var info = new EnemyBriefInfo();
        var patch = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Authority.EnemyBriefPresentation_Patch", true)!;
        patch.GetMethod("Apply", [typeof(EnemyBriefInfo), typeof(GroundEnemyState)])!.Invoke(null, [info, restored]);
        TestAssert.AreEqual(4, info.builderState); TestAssert.AreEqual(60, info.sp); TestAssert.AreEqual(60, info.spMax);
        TestAssert.AreEqual(30000, info.matter); TestAssert.AreEqual(6000000, info.energy);
        TestAssert.AreEqual(180, info.matterChange); TestAssert.AreEqual(-12000, info.energyChange);
        TestAssert.AreEqual(1f, (float)info.sp / info.spMax);
    }

    [TestMethod]
    public void SpaceBuilderBriefUsesRealProgressInsteadOfZeroOverZero()
    {
        var state = new SpaceEnemyState(SpaceEnemyKind.SpaceBuilder, true, false,
            protoId: 8101, modelIndex: 41, owner: 1, port: 0, stateFlags: 0,
            astroId: 1000101, originAstroId: 1000101, dockIndex: 0, builderIndex: 5, level: 0,
            posX: 120.0, posY: 0.0, posZ: -80.0, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
            velX: 0f, velY: 0f, velZ: 0f, hp: 800, hpMax: 900, hpRecover: 0, hpIncoming: 0,
            animationState: 0, builderSp: 240, builderSpMax: 600,
            matter: 12000, matterMax: 20000, matterChange: -360,
            energy: 450000, energyMax: 900000, energyChange: -8400);
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(state, out var payload));
        TestAssert.IsLessThanOrEqualTo(AuthorityLimits.StateRecordMaxBytes, payload.Length);
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryDecode(payload, 0, payload.Length, out var restored, out _));
        var info = new EnemyBriefInfo();
        var patch = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Authority.EnemyBriefPresentation_Patch", true)!;
        patch.GetMethod("Apply", [typeof(EnemyBriefInfo), typeof(SpaceEnemyState)])!.Invoke(null, [info, restored]);
        TestAssert.AreEqual(0, info.builderState, "A building space builder shows 建造中.");
        TestAssert.AreEqual(240, info.sp); TestAssert.AreEqual(600, info.spMax);
        TestAssert.AreEqual(12000, info.matter); TestAssert.AreEqual(20000, info.matterMax);
        TestAssert.AreEqual(-360, info.matterChange);
        TestAssert.AreEqual(450000, info.energy); TestAssert.AreEqual(-8400, info.energyChange);
        TestAssert.AreEqual(.4f, (float)info.sp / info.spMax, 1e-6f);
    }

    [TestMethod]
    public void AClientBriefNeverDividesByZeroWhenNoMirrorHasArrived()
    {
        // A freshly reset EnemyBriefInfo is exactly what a client shell produces: builderState 0 with
        // sp/spMax and productSp/productSpMax at 0/0, which the vanilla panel divides unguarded.
        var info = new EnemyBriefInfo();
        var patch = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Authority.EnemyBriefPresentation_Patch", true)!;
        patch.GetMethod("Guard")!.Invoke(null, [info]);
        TestAssert.IsFalse(float.IsNaN((float)info.sp / info.spMax));
        TestAssert.AreEqual(0f, (float)info.sp / info.spMax);
        TestAssert.IsFalse(float.IsNaN((float)info.productSp / info.productSpMax));
        TestAssert.AreEqual(0f, (float)info.productSp / info.productSpMax);
    }
}
