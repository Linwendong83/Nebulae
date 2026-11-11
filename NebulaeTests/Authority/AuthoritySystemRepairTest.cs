using System.Reflection;
using HarmonyLib;
using NebulaeModel.Authority;
using global::NebulaeModel.DataStructures;
using NebulaeModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class AuthoritySystemRepairTest
{
    [TestMethod]
    public void MovementCannotPoisonHostTransformsOrConstructionRange()
    {
        var packet = new global::NebulaeModel.Packets.Players.PlayerMovement { MovementState = EMovementState.Sail };
        TestAssert.IsTrue(packet.HasFinitePose());
        packet.UPosition = new global::NebulaeModel.DataStructures.Double3(double.NaN, 0, 0);
        TestAssert.IsFalse(packet.HasFinitePose());
        packet.UPosition = default; packet.BuildArea = float.NaN;
        TestAssert.IsFalse(packet.HasFinitePose());
        packet.BuildArea = 80; packet.MovementState = (EMovementState)255;
        TestAssert.IsFalse(packet.HasFinitePose());
        packet.MovementState = EMovementState.Sail; packet.UPosition = new global::NebulaeModel.DataStructures.Double3(double.MaxValue, 0, 0);
        TestAssert.IsFalse(packet.HasFinitePose());
    }

    [TestMethod]
    [DataRow(0, 101, 100, 101)]
    [DataRow(0, 0, 100, 100)]
    [DataRow(0, 0, 0, -1)]
    [DataRow(-1, 101, 100, -1)]
    [DataRow(200, 101, 100, 200)]
    public void StatisticsUseAstroIdsAndDeepSpaceUsesTheGalaxy(int filter, int planet, int star, int expected) =>
        TestAssert.AreEqual(expected, StatisticsAstroFilter.Resolve(filter, planet, star));

    [TestMethod]
    public void StaleCheckpointsCannotRefundCombatConsumption()
    {
        var ledger = new CombatConsumptionLedger();
        ledger.Debit(1601, 3); ledger.Debit(1601, 2);
        TestAssert.AreEqual(5, ledger.Pending(1601, 0));
        TestAssert.AreEqual(2, ledger.Pending(1601, 3));
        TestAssert.AreEqual(0, ledger.Pending(1601, 5));
        TestAssert.IsTrue(CombatConsumptionLedger.TryDelta(3, 5, out var first));
        TestAssert.AreEqual(2, first);
        TestAssert.IsTrue(CombatConsumptionLedger.TryDelta(5, 5, out var duplicate));
        TestAssert.AreEqual(0, duplicate);
        TestAssert.IsFalse(CombatConsumptionLedger.TryDelta(5, 3, out _));
        TestAssert.ThrowsExactly<ArgumentOutOfRangeException>(() => ledger.Pending(1601, 6));
    }

    [TestMethod]
    public void OldAmmoStorageKeepsHostConsumptionAndOnlyAppliesPersonalTransfers()
    {
        var loaded = AccessTools.Field(typeof(StorageComponent), "staticLoaded");
        var wasLoaded = loaded.GetValue(null);
        loaded.SetValue(null, true); // These removal-only fixtures need no Unity item database.
        try
        {
            var current = new StorageComponent(3); current.grids[0].itemId = 1601; current.grids[0].count = 2;
            var observed = new StorageComponent(3); observed.grids[0].itemId = 1601; observed.grids[0].count = 5;
            var personal = new StorageComponent(3); personal.grids[0] = observed.grids[0];
            var merge = typeof(NebulaeWorld.Combat.CombatAuthorityManager).GetMethod("MergeStock", BindingFlags.NonPublic | BindingFlags.Static);
            var unchanged = (StorageComponent)merge.Invoke(null, new object[] { current, observed, personal });
            TestAssert.AreEqual(2, unchanged.grids.Sum(x => x.count));
            personal.grids[0].count = 4;
            var transfer = (StorageComponent)merge.Invoke(null, new object[] { current, observed, personal });
            TestAssert.AreEqual(1, transfer.grids.Sum(x => x.count));
            TestAssert.AreEqual(2, current.grids.Sum(x => x.count), "Validation must not mutate the canonical stock.");
            personal.grids[0].count = 0;
            var error = TestAssert.ThrowsExactly<TargetInvocationException>(() => merge.Invoke(null, new object[] { current, observed, personal }));
            TestAssert.IsInstanceOfType<InvalidOperationException>(error.InnerException);
        }
        finally { loaded.SetValue(null, wasLoaded); }
    }

    private sealed class FailingEnemyPool : IGroundEnemyPools
    {
        public bool Fail = true;
        public int Creates;
        public bool EnemyExists(int enemyId) => Creates > 0;
        public void CreateEnemyShell(int enemyId, in GroundEnemyState state)
        { if (Fail) throw new InvalidOperationException("Display backing pool is unavailable."); Creates++; }
        public void WriteEnemyState(int enemyId, in GroundEnemyState state) { }
        public void RemoveEnemyShell(int enemyId) { }
        public void PlayDeathPresentation(int enemyId, int builderSp, int builderSpMax) { }
    }

    [TestMethod]
    public void FailedNativeShellCreationRemainsUnboundAndRetriesCreation()
    {
        var epoch = new AuthorityEpoch(3, 4); var pool = new FailingEnemyPool();
        var binding = new GroundEnemyBinding(101, pool);
        var key = ObjectKey.Create(epoch, PoolKind.GroundEnemy, 101, 7, 1);
        var state = new GroundEnemyState(GroundEnemyKind.GroundUnit, true, false, 8130, 45, 2, 3, 0,
            101, 101, 1, 0, 1, 10, 0, 20, 0, 0, 0, 1, 0, 0, 0, 500, 900, 2, 0);
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(state, out var bytes));
        TestAssert.ThrowsExactly<InvalidOperationException>(() => binding.ApplyState(key, bytes));
        TestAssert.AreEqual(0, binding.BoundCount);
        pool.Fail = false;
        TestAssert.IsTrue(binding.ApplyState(key, bytes));
        TestAssert.AreEqual(1, binding.BoundCount); TestAssert.AreEqual(1, pool.Creates);
    }

    [TestMethod]
    public void CombatCommandsRejectNonFiniteInputsAndUnknownOperations()
    {
        var intent = new CombatIntent(CombatIntentKind.Bomb, 0, 100, 20, -50);
        TestAssert.IsTrue(CombatIntent.TryDecode(intent.Encode(), out var decoded));
        TestAssert.AreEqual(-50, decoded.Z);
        TestAssert.IsFalse(CombatIntent.TryDecode(new CombatIntent(CombatIntentKind.Bomb, x: double.NaN).Encode(), out _));
        TestAssert.IsFalse(CombatIntent.TryDecode(new CombatIntent((CombatIntentKind)255).Encode(), out _));
        TestAssert.IsFalse(CombatIntent.TryDecode(intent.Encode().Take(28).ToArray(), out _));
    }

    private sealed class BackingPool : IReplicaMirrorObserver, IReplicaBaselineReadiness, ISnapshotAckSink
    {
        public bool Ready;
        public bool Fail;
        public int Installs;
        public int Acks;
        public bool IsReadyForBaseline(ScopeKey scope) => Ready;
        public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state) { }
        public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
        {
            Installs++;
            if (Fail) throw new InvalidDataException("Invalid ruin state.");
        }
        public void OnMemberRemoved(ScopeKey scope, in ObjectKey key) { }
        public void Send(AuthoritySnapshotAckPacket packet) => Acks++;
    }

    [TestMethod]
    public void BaselineWaitsForItsFactoryBeforeAckAndInstallsExactlyOnce()
    {
        var epoch = new AuthorityEpoch(1, 2); var scope = new ScopeKey(PoolKind.GroundEnemy, 101);
        var pool = new BackingPool();
        var replica = new ClientWorldReplica(epoch) { MirrorObserver = pool, SnapshotAckSink = pool };
        replica.Subscribe(scope);
        var snapshot = new FrozenScopeSnapshot(scope, 1, 1, 0, 100,
            new List<SnapshotMemberRecord> { new(ObjectKey.Create(epoch, PoolKind.GroundEnemy, 101, 7, 1), 1, new byte[] { 5 }) });
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(snapshot, out var bytes));
        var chunks = AuthoritySnapshotCodec.ChunkCountFor(bytes.Length); var hash = AuthoritySnapshotCodec.Hash(bytes);
        TestAssert.IsTrue(replica.BeginSnapshot(scope, 1, 1, chunks, bytes.Length, hash));
        TestAssert.AreEqual(1, chunks);
        TestAssert.IsTrue(replica.ReceiveSnapshotChunk(scope, 1, 1, 0, bytes));
        TestAssert.IsTrue(replica.CommitSnapshot(scope, 1, 1, chunks, hash));
        TestAssert.AreEqual(0, pool.Installs); TestAssert.AreEqual(0, pool.Acks);
        pool.Ready = true;
        replica.RetryPendingBaselines((_, install) => install());
        replica.RetryPendingBaselines((_, install) => install());
        TestAssert.AreEqual(1, pool.Installs); TestAssert.AreEqual(1, pool.Acks);
    }

    /// <summary>
    /// A baseline one of whose members the adapter keeps refusing must not be retried once per
    /// frame forever: that throw lands inside the frame boundary, so every frame loses everything
    /// the boundary does after the install. It backs off, gives up, and asks recovery for a new one.
    /// </summary>
    [TestMethod]
    public void ABaselineTheAdapterKeepsRefusingIsBackedOffAndHandedToRecovery()
    {
        var epoch = new AuthorityEpoch(1, 2); var scope = new ScopeKey(PoolKind.GroundEnemy, 101);
        var pool = new BackingPool();
        var replica = new ClientWorldReplica(epoch) { MirrorObserver = pool, SnapshotAckSink = pool };
        replica.Subscribe(scope);
        var snapshot = new FrozenScopeSnapshot(scope, 1, 1, 0, 100,
            new List<SnapshotMemberRecord> { new(ObjectKey.Create(epoch, PoolKind.GroundEnemy, 101, 7, 1), 1, new byte[] { 5 }) });
        TestAssert.IsTrue(AuthoritySnapshotCodec.TryEncode(snapshot, out var bytes));
        var chunks = AuthoritySnapshotCodec.ChunkCountFor(bytes.Length); var hash = AuthoritySnapshotCodec.Hash(bytes);
        TestAssert.IsTrue(replica.BeginSnapshot(scope, 1, 1, chunks, bytes.Length, hash));
        TestAssert.IsTrue(replica.ReceiveSnapshotChunk(scope, 1, 1, 0, bytes));
        TestAssert.IsTrue(replica.CommitSnapshot(scope, 1, 1, chunks, hash));
        TestAssert.AreEqual(0, pool.Installs); // not ready yet: queued, never installed

        pool.Ready = true; pool.Fail = true;
        for (var frame = 0; frame < 1024; frame++) replica.RetryPendingBaselines((_, install) => install());

        TestAssert.IsGreaterThanOrEqualTo(2, pool.Installs);
        TestAssert.IsLessThanOrEqualTo(16, pool.Installs);
        TestAssert.AreEqual(0, pool.Acks);
        TestAssert.IsTrue(replica.ScopesNeedingResync.Contains(scope));
    }

    [TestMethod]
    public void QueuedCombatFactsOwnAllTheirMutableBuffers()
    {
        var source = new AuthorityPlayerCombatStatePacket { CombatData = new byte[] { 1 }, DebitItems = new[] { 1601 }, DebitTotals = new[] { 2 } };
        var copy = (AuthorityPlayerCombatStatePacket)source.CreateOwnedCopy();
        source.CombatData[0] = 99; source.DebitItems[0] = 99; source.DebitTotals[0] = 99;
        TestAssert.AreEqual(1, copy.CombatData[0]); TestAssert.AreEqual(1601, copy.DebitItems[0]); TestAssert.AreEqual(2, copy.DebitTotals[0]);
    }

    [TestMethod]
    public void ChangedCombatPatchesActuallyInstallAgainstTheGameBodies()
    {
        var harmony = new Harmony("nebulae.tests.authority-repair.install");
        var names = new[] { "Authority.PlayerCombatAuthority_Patch", "Authority.CombatTargetScope_Patch", "Authority.SpaceSweepAuthority_Patch",
            "Transpilers.SkillSystem_Transpiler", "Dynamic.Combat.SkillSystem_Patch", "Dynamic.Combat.EnemyDFGroundSystem_Patch",
            "Dynamic.Combat.EnemyDFHiveSystem_Patch", "Authority.FleetCombatAuthority_Patch", "Authority.FleetGroundPoseAuthority_Patch",
            "Authority.UnitCombatAuthority_Patch", "Authority.FleetRemovalAuthority_Patch", "Authority.FleetTickContext",
            "Authority.ProjectilePlayerIdentity_Patch", "Authority.FleetLaunchStar_Patch", "Dynamic.Combat.SkillSystem_Common_Patch", "Authority.HiveDecisionLoop_Patch",
            "Authority.PlayerSkillTarget_Patch", "Authority.SkillReferencesGuard_Patch" };
        try
        {
            foreach (var name in names)
            {
                var type = typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches." + name, true);
                List<MethodInfo> methods;
                try { methods = harmony.CreateClassProcessor(type).Patch(); }
                catch (Exception ex) { throw new InvalidOperationException("Patch install failed: " + name, ex); }
                TestAssert.IsNotEmpty(methods, name);
            }
        }
        finally
        {
            try { harmony.UnpatchSelf(); }
            catch (HarmonyException ex) when (ex.ToString().Contains("ECall"))
            { /* Unity internal calls cannot be prepared by the standalone CLR; game boot verifies cleanup separately. */ }
        }
    }


}
