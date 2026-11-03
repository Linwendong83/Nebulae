using System.Reflection;
using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Utils;
using NebulaeModel.Packets.Factory.Belt;
using NebulaePatcher.Patches.Authority;
using NebulaeWorld.Authority.Adapters;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
internal class TurretAuthorityTest
{
    private static readonly AuthorityEpoch Epoch = new(7, 11);
    private static ObjectKey Key(long generation = 1, int planet = 101) => ObjectKey.Create(Epoch, PoolKind.Turret, planet, 9, generation);
    private static TurretState State => new()
    {
        EntityId = 9,
        ModelIndex = 400,
        Type = 1,
        Flags = 3,
        ItemId = 1601,
        ItemCount = 5,
        ItemInc = 10,
        BulletCount = 17,
        ItemBulletCount = 30,
        BulletDamage = 100,
        TotalDamage = 1234567,
        KillCount = 42,
        SupernovaTick = -200,
        SupernovaStrength = 3.2f,
        Group = 3,
        VsSettings = 255,
        TargetId = 12,
        TargetAstro = 101,
        TargetType = (byte)ETargetType.Enemy,
        Direction = new UnityEngine.Vector3(1, 0, 0),
        TargetDirection = new UnityEngine.Vector3(0, 0, 1),
        SampleTick = 100
    };
    [TestMethod]
    public void StateRoundTripPreservesDecisionsAndClockAnchor()
    {
        TestAssert.IsTrue(TurretStateCodec.TryEncode(State, out var bytes));
        TestAssert.IsLessThanOrEqualTo(AuthorityLimits.StateRecordMaxBytes, bytes.Length);
        TestAssert.IsTrue(TurretStateCodec.TryDecode(bytes, out var restored));
        TestAssert.AreEqual(State.ItemInc, restored.ItemInc); TestAssert.AreEqual(State.BulletCount, restored.BulletCount);
        TestAssert.AreEqual(State.TotalDamage, restored.TotalDamage); TestAssert.AreEqual(State.SupernovaTick, restored.SupernovaTick);
        TestAssert.AreEqual(State.Direction.x, restored.Direction.x); TestAssert.AreEqual(State.SampleTick, restored.SampleTick);
        TestAssert.AreEqual(State.TargetId, restored.TargetId);
    }
    [TestMethod]
    public void StateRejectsTruncationTrailingBytesAndNonFiniteValues()
    {
        TurretStateCodec.TryEncode(State, out var bytes);
        for (var length = 0; length < bytes.Length; length++) TestAssert.IsFalse(TurretStateCodec.TryDecode(bytes.Take(length).ToArray(), out _));
        TestAssert.IsFalse(TurretStateCodec.TryDecode(bytes.Concat(new byte[] { 0 }).ToArray(), out _));
        var state = State; state.SupernovaStrength = float.NaN; TestAssert.IsFalse(TurretStateCodec.TryEncode(state, out _));
        state = State; state.Flags = 128; TestAssert.IsFalse(TurretStateCodec.TryEncode(state, out _));
    }
    [TestMethod]
    public void DeferredStateUsesLatestFactsAndDoesNotCreateABuilding()
    {
        var ready = false; var applied = new List<TurretState>();
        var mirror = new TurretMirror(Epoch, (_, state) => { if (!ready) return false; applied.Add(state); return true; }, _ => { });
        TestAssert.IsTrue(mirror.Put(Key(), State));
        var newer = State; newer.BulletCount = 3; mirror.Put(Key(), newer);
        TestAssert.AreEqual(1, mirror.PendingCount); TestAssert.IsEmpty(applied);
        ready = true; mirror.Retry(101);
        TestAssert.AreEqual(0, mirror.PendingCount); TestAssert.AreEqual((short)3, applied.Single().BulletCount);
    }
    [TestMethod]
    public void RecycledSlotRejectsOldStateAndTrailingDespawn()
    {
        var seen = new List<long>(); var removed = new List<long>();
        var mirror = new TurretMirror(Epoch, (key, _) => { seen.Add(key.Generation); return true; }, key => removed.Add(key.Generation));
        mirror.Put(Key(), State); mirror.Put(Key(2), State); mirror.Remove(Key());
        TestAssert.IsFalse(mirror.Put(Key(), State)); TestAssert.IsEmpty(removed);
        mirror.Remove(Key(2)); TestAssert.IsFalse(mirror.Put(Key(2), State));
        TestAssert.AreEqual(2L, removed.Single()); TestAssert.HasCount(2, seen);
    }
    [TestMethod]
    public void BaselineAndLeavingPlanetRemoveDeferredBindings()
    {
        var ready = false; var applied = 0;
        var mirror = new TurretMirror(Epoch, (_, _) => { applied++; return ready; }, _ => { });
        mirror.Put(Key(), State); mirror.Install(101, Array.Empty<(ObjectKey, TurretState)>());
        ready = true; mirror.Retry(101); TestAssert.AreEqual(1, applied);
        mirror.Install(101, new[] { (Key(), State) }); TestAssert.AreEqual(2, applied);
        mirror.ClearScope(101); TestAssert.IsFalse(mirror.Put(Key(), State));
        TestAssert.IsFalse(mirror.Put(ObjectKey.Create(new AuthorityEpoch(8, 12), PoolKind.Turret, 101, 9, 1), State));
    }
    [TestMethod]
    public void SignedInventoryTransfersReplayOnlyUntilExplicitAcknowledgement()
    {
        var ledger = new PlayerInventoryTransferLedger();
        ledger.Record(3, new PlayerInventoryTransferLedger.Transfer(1601, -10, 20));
        ledger.Record(5, new PlayerInventoryTransferLedger.Transfer(1601, 4, 8));
        TestAssert.AreEqual(-6, ledger.PendingAfter(0).Sum(x => x.Count));
        TestAssert.IsFalse(ledger.ValidAcknowledgement(4)); TestAssert.IsFalse(ledger.ValidAcknowledgement(6));
        ledger.Acknowledge(3); TestAssert.AreEqual(4, ledger.PendingAfter(3).Single().Count);
        ledger.Acknowledge(5); TestAssert.AreEqual(0, ledger.PendingCount); TestAssert.IsFalse(ledger.ValidAcknowledgement(3));
    }
    [TestMethod]
    public void DuplicateReceiptsCannotDoubleCreditWhileInventoryIsFull()
    {
        var gate = new InventoryReceiptGate(); var key = new CommandKey(Epoch, new ConnectionEpoch(1), 3);
        TestAssert.IsTrue(gate.TryAccept(key));
        TestAssert.IsFalse(gate.TryAccept(key), "A delayed partial credit retains the original receipt identity.");
        var newConnection = new CommandKey(Epoch, new ConnectionEpoch(2), 3);
        TestAssert.IsTrue(gate.TryAccept(newConnection)); gate.Clear(); TestAssert.IsTrue(gate.TryAccept(key));
    }
    [TestMethod]
    public void IntentAndTransferReceiptRoundTrip()
    {
        var intent = new TurretIntent(TurretIntentKind.Deposit, 1601, 100, 400);
        TestAssert.IsTrue(TurretIntent.TryDecode(intent.Encode(), out var restored));
        TestAssert.AreEqual(100, restored.Count); TestAssert.AreEqual(400, restored.Inc);
        TestAssert.IsFalse(TurretIntent.TryDecode(new TurretIntent(TurretIntentKind.Deposit, 1601, -1).Encode(), out _));
        var receipt = TurretAuthorityManager.Pack(1601, 100, 400);
        TestAssert.AreEqual(1601, (int)(receipt >> 32)); TestAssert.AreEqual(100, (int)(receipt >> 16 & 65535)); TestAssert.AreEqual(400, (int)(receipt & 65535));
    }
    [TestMethod]
    public void BeltBatchProcessesAllReceiptsAndPreservesUnconsumedStack()
    {
        var consumed = new Dictionary<int, int>(); var missing = new List<int>();
        var receipts = new[] { new BeltUpdate(1601, 6, 1), new BeltUpdate(1602, 2, 2), new BeltUpdate(1603, 3, 3) };
        BeltPickupBatch.Apply(receipts, (receipt, needed) =>
        {
            if (receipt.BeltId == 2) return 0;
            var amount = Math.Min(4, needed); consumed.TryGetValue(receipt.BeltId, out var old); consumed[receipt.BeltId] = old + amount;
            return amount;
        }, (receipt, _) => missing.Add(receipt.BeltId));
        TestAssert.AreEqual(6, consumed[1]); TestAssert.AreEqual(3, consumed[3]); TestAssert.AreEqual(2, missing.Single());
        var split = BeltPickupBatch.SplitStack(4, 11, 2);
        TestAssert.AreEqual(2, split.Taken); TestAssert.AreEqual(2, split.Count); TestAssert.AreEqual(6, split.Inc);
    }
    [TestMethod]
    public void NativeTurretsWaitForBindingsAndCannotCommitEnemyActivation()
    {
        var previousProbe = AuthorityRuleGuard.Probe;
        try
        {
            foreach (var prefix in new TurretPrefix[] { TurretAuthority_Patch.Internal_Prefix, TurretAuthority_Patch.Aim_Prefix,
                         TurretAuthority_Patch.Shoot_Prefix, TurretAuthority_Patch.Post_Prefix, TurretAuthority_Patch.Search_Prefix })
            {
                var turret = default(TurretComponent);
                AuthorityRuleGuard.Probe = () => new AuthorityGuardSession(true, false, true); TestAssert.IsFalse(prefix(ref turret, null));
                AuthorityRuleGuard.Probe = () => new AuthorityGuardSession(true, true, false); TestAssert.IsTrue(prefix(ref turret, null));
                AuthorityRuleGuard.Probe = null; TestAssert.IsTrue(prefix(ref turret, null));
            }
            AuthorityRuleGuard.Probe = () => new AuthorityGuardSession(true, false, false);
            var activates = true;
            TestAssert.IsFalse(TurretAuthority_Patch.Activation_Prefix(ref activates));
            TestAssert.IsFalse(activates);
        }
        finally { AuthorityRuleGuard.Probe = previousProbe; }
    }
    private delegate bool TurretPrefix(ref TurretComponent turret, PlanetFactory? factory);
    [TestMethod]
    public void NativeBaseRemovalNullsBuildersAndSearchCleanupInvalidatesEveryTurret()
    {
        var pool = new ObjectPool<DFGBaseComponent>(); pool.Reset();
        var groundBase = pool.Add(); groundBase.InitFormations(); groundBase.InitIncomingSkills();
        groundBase.pbuilders = Array.Empty<GrowthPattern_DFGround.Builder>(); var id = groundBase.id;
        pool.Remove(id); TestAssert.IsNull(groundBase.pbuilders);
        var defense = Uninitialized<DefenseSystem>();
        defense.turrets = new DataPool<TurretComponent>(); defense.turrets.Reset();
        ref var turret = ref defense.turrets.Add(); turret.searchPairBeginIndex = 2; turret.searchPairCount = 1;
        defense.turretSearchPairCount = 3; defense.turretSearchPairs = new TurretSearchPair[3];
        DarkFogReplicaBinding.ClearSearchReferences(defense);
        TestAssert.AreEqual(0, turret.searchPairCount); TestAssert.AreEqual(0, turret.searchPairBeginIndex); TestAssert.AreEqual(0, defense.turretSearchPairCount);
    }
    [TestMethod]
    public void InstalledGameTargetsHaveBodiesAndCoverageDetectsMissingPrefixes()
    {
        var harmony = new Harmony("nebulae.tests.turret-authority");
        var targets = new[] { (typeof(TurretComponent), "InternalUpdate", "Internal_Prefix"), (typeof(TurretComponent), "Aim", "Aim_Prefix"),
            (typeof(TurretComponent), "Shoot", "Shoot_Prefix"), (typeof(TurretComponent), "PostGameTick", "Post_Prefix"),
            (typeof(TurretComponent), "Search", "Search_Prefix"), (typeof(TurretComponent), "DeterminActiveEnemyUnits", "Activation_Prefix") };
        try
        {
            TestAssert.HasCount(targets.Length, TurretAuthority_Patch.VerifyCoverage());
            foreach (var (type, method, prefix) in targets)
            {
                var target = AccessTools.Method(type, method);
                TestAssert.IsNotNull(target); TestAssert.IsGreaterThan(5, target.GetMethodBody()!.GetILAsByteArray()!.Length);
                harmony.Patch(target, new HarmonyMethod(AccessTools.Method(typeof(TurretAuthority_Patch), prefix)));
            }
            TestAssert.IsEmpty(TurretAuthority_Patch.VerifyCoverage());
        }
        finally { foreach (var (type, method, _) in targets) harmony.Unpatch(AccessTools.Method(type, method), HarmonyPatchType.All, harmony.Id); }
    }
    [TestMethod]
    public void EveryTurretWindowMutationRegistersItsAuthorityPrefix()
    {
        var targets = new[]
        {
            nameof(UITurretWindow.OnTurretShiftValueChange),
            nameof(UITurretWindow.GroupSelectionBtn_onClick),
            nameof(UITurretWindow.OnPrioritySelectButtonClicked),
            nameof(UITurretWindow.SuperNovaBtn_onClick),
            nameof(UITurretWindow.OnHandFillAmmoButtonClick),
            nameof(UITurretWindow.AmmoBtn_onClick),
            nameof(UITurretWindow.ClearMagBtn_onClick)
        };
        var declaredTarget = typeof(TurretAuthorityUI_Patch).GetCustomAttributes<HarmonyPatch>()
            .Select(attribute => attribute.info.declaringType).Single();
        TestAssert.AreEqual(typeof(UITurretWindow), declaredTarget);
        var prefixes = typeof(TurretAuthorityUI_Patch).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<HarmonyPrefix>() != null).ToArray();
        foreach (var name in targets)
        {
            TestAssert.AreEqual(1, prefixes.Count(prefix => prefix.GetCustomAttributes<HarmonyPatch>()
                .Any(attribute => attribute.info.methodName == name)),
                name + " must register one authority prefix.");
            TestAssert.IsNotNull(AccessTools.Method(declaredTarget, name), name + " must resolve in the installed game.");
        }
    }

}
