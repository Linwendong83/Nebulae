
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// Tests the canonical state codec, deterministic kind derivation, and client binding that rebuilds
/// shells from host facts. Everything here runs without a game process.
/// </summary>
[TestClass]
public class SpaceEnemyStateTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0A13080808080808, 0x0B13080808080808);

    private static ObjectKey Key(int nativeId, long generation) =>
        ObjectKey.Create(Epoch, PoolKind.SpaceEnemy, AuthorityScope.Sector, nativeId, generation);

    private static SpaceEnemyState HiveUnit(int hp = 500, byte unitBehavior = 0) => new(
        SpaceEnemyKind.SpaceUnit, true, false,
        protoId: 8113, modelIndex: 45, owner: 2, port: 3, stateFlags: 0,
        astroId: 1000101, originAstroId: 1000101, dockIndex: 0, builderIndex: 0, level: 0,
        posX: 4900.0, posY: -350.0, posZ: 1200.0, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 0f, velY: 0f, velZ: 0f, hp: hp, hpMax: 900, hpRecover: 2, hpIncoming: 0,
        unitBehavior: unitBehavior);

    private static SpaceEnemyState HiveCore() => new(
        SpaceEnemyKind.SpaceCore, true, false,
        protoId: 8100, modelIndex: 40, owner: 1, port: 0, stateFlags: 0,
        astroId: 1000101, originAstroId: 1000101, dockIndex: 0, builderIndex: 5, level: 3,
        posX: 0.0, posY: 0.0, posZ: 0.0, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 0f, velY: 0f, velZ: 0f, hp: 5000, hpMax: 8000, hpRecover: 0, hpIncoming: 0);

    private static SpaceEnemyState Relay(int dock = 2) => new(
        SpaceEnemyKind.SpaceRelay, true, true,
        protoId: 8111, modelIndex: 46, owner: 1, port: 0, stateFlags: 0,
        astroId: 1000101, originAstroId: 1000101, dockIndex: dock, builderIndex: 0, level: 0,
        posX: 1920.0, posY: 455.0, posZ: 0.0, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 1f, velY: 0f, velZ: 0f, hp: 1200, hpMax: 1500, hpRecover: 0, hpIncoming: 0);

    private static SpaceEnemyState HiveBuilder() => new(
        SpaceEnemyKind.SpaceBuilder, true, false,
        protoId: 8101, modelIndex: 41, owner: 1, port: 0, stateFlags: 0,
        astroId: 1000101, originAstroId: 1000101, dockIndex: 0, builderIndex: 5, level: 0,
        posX: 120.0, posY: 0.0, posZ: -80.0, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 0f, velY: 0f, velZ: 0f, hp: 800, hpMax: 900, hpRecover: 0, hpIncoming: 0,
        animationTime: .5f, animationState: 0, animationPower: 1f,
        builderSp: 240, builderSpMax: 600,
        matter: 12000, matterMax: 20000, matterChange: -360,
        energy: 450000, energyMax: 900000, energyChange: -8400);

    private static SpaceEnemyState HiveTurret(byte turretState = 0, bool turretInRange = false) => new(
        SpaceEnemyKind.SpaceTurret, true, false,
        protoId: 8107, modelIndex: 47, owner: 1, port: 0, stateFlags: 0,
        astroId: 1000101, originAstroId: 1000101, dockIndex: 0, builderIndex: 4, level: 0,
        posX: 640.0, posY: 40.0, posZ: -120.0, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 0f, velY: 0f, velZ: 0f, hp: 1500, hpMax: 1500, hpRecover: 0, hpIncoming: 0,
        animationTime: 11.5f, prepareLength: .6f, workingLength: 0f, animationState: 2, animationPower: .8f,
        turretState: turretState, turretTargetInShootRange: turretInRange);

    private sealed class FakePools : ISpaceEnemyPools
    {
        public readonly Dictionary<int, SpaceEnemyState> Shells = [];
        public readonly List<int> Removed = [];
        public readonly List<int> Presented = [];

        public bool EnemyExists(int enemyId) => Shells.ContainsKey(enemyId);
        public void CreateEnemyShell(int enemyId, in SpaceEnemyState state) => Shells[enemyId] = state;
        public void WriteEnemyState(int enemyId, in SpaceEnemyState state) => Shells[enemyId] = state;
        public void RemoveEnemyShell(int enemyId)
        {
            TestAssert.IsTrue(Shells.Remove(enemyId), "A removed shell must have existed.");
            Removed.Add(enemyId);
        }

        public void PlayDeathPresentation(int enemyId) => Presented.Add(enemyId);
    }

    [TestMethod]
    public void AStateRoundTripsEveryField()
    {
        var state = HiveUnit(hp: 640);
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(state, out var data));
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryDecode(data, 0, data.Length, out var decoded, out var reject), reject.ToString());
        TestAssert.AreEqual(SpaceEnemyKind.SpaceUnit, decoded.Kind);
        TestAssert.IsTrue(decoded.HasCombatStat);
        TestAssert.AreEqual((short)8113, decoded.ProtoId);
        TestAssert.AreEqual(1000101, decoded.AstroId);
        TestAssert.AreEqual(1000101, decoded.OriginAstroId);
        TestAssert.AreEqual(4900.0, decoded.PosX, 1e-9);
        TestAssert.AreEqual(1f, decoded.RotW, 1e-6f);
        TestAssert.AreEqual(640, decoded.Hp);
        TestAssert.AreEqual(900, decoded.HpMax);
    }

    [TestMethod]
    public void MotionAnchorsAreOnlyAppendedForUnits()
    {
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(HiveUnit(), out var data));
        TestAssert.HasCount(SpaceEnemyStateCodec.SizeFor(SpaceEnemyKind.SpaceUnit), data);
        TestAssert.IsLessThanOrEqualTo(AuthorityLimits.StateRecordMaxBytes, data.Length);
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(HiveUnit(), out var again));
        CollectionAssert.AreEqual(data, again, "Encoding is deterministic: the dirt comparison depends on it.");
    }

    [TestMethod]
    public void AHiveBuilderCarriesItsBuildingPresentationToTheBriefPanel()
    {
        var state = HiveBuilder();
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(state, out var data));
        TestAssert.HasCount(SpaceEnemyStateCodec.FixedSize, data);
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryDecode(data, 0, data.Length, out var decoded, out var reject), reject.ToString());
        TestAssert.AreEqual(240, decoded.BuilderSp);
        TestAssert.AreEqual(600, decoded.BuilderSpMax);
        TestAssert.AreEqual(12000, decoded.Matter);
        TestAssert.AreEqual(20000, decoded.MatterMax);
        TestAssert.AreEqual(-360, decoded.MatterChange);
        TestAssert.AreEqual(450000, decoded.Energy);
        TestAssert.AreEqual(900000, decoded.EnergyMax);
        TestAssert.AreEqual(-8400, decoded.EnergyChange);
        TestAssert.AreEqual(0, decoded.ProductSp);
        TestAssert.AreEqual(0, decoded.ProductSpMax);
        TestAssert.AreEqual(.4f, (float)decoded.BuilderSp / decoded.BuilderSpMax, 1e-6f);
    }

    [TestMethod]
    public void AnImpossibleBuildingPresentationIsRefused()
    {
        var broken = new SpaceEnemyState(SpaceEnemyKind.SpaceBuilder, true, false,
            protoId: 8101, modelIndex: 41, owner: 1, port: 0, stateFlags: 0,
            astroId: 1000101, originAstroId: 1000101, dockIndex: 0, builderIndex: 5, level: 0,
            posX: 120.0, posY: 0.0, posZ: -80.0, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
            velX: 0f, velY: 0f, velZ: 0f, hp: 800, hpMax: 900, hpRecover: 0, hpIncoming: 0,
            animationState: 0, builderSp: 700, builderSpMax: 600);
        TestAssert.IsFalse(SpaceEnemyStateCodec.TryEncode(broken, out _), "sp beyond spMax never encodes.");
    }

    [TestMethod]
    public void AHiveUnitCarriesItsBehaviorToTheBriefPanel()
    {
        // 9 is EEnemyBehavior.Engage — the "攻击中" the panel prints for a unit that is fighting.
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(HiveUnit(unitBehavior: 9), out var data));
        TestAssert.HasCount(SpaceEnemyStateCodec.SizeFor(SpaceEnemyKind.SpaceUnit), data);
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryDecode(data, 0, data.Length, out var decoded, out var reject), reject.ToString());
        TestAssert.AreEqual((byte)9, decoded.UnitBehavior);
        TestAssert.AreEqual((byte)0, HiveUnit().UnitBehavior, "A non-unit record defaults to no behavior.");
    }

    [TestMethod]
    public void AnImpossibleUnitBehaviorIsRefused()
    {
        TestAssert.IsFalse(SpaceEnemyStateCodec.TryEncode(HiveUnit(unitBehavior: 10), out _),
            "A behavior above the largest vanilla value never encodes.");
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(HiveUnit(), out var data));
        var corrupt = (byte[])data.Clone();
        corrupt[SpaceEnemyStateCodec.FixedSize - 6] = 10;
        TestAssert.IsFalse(SpaceEnemyStateCodec.TryDecode(corrupt, 0, corrupt.Length, out _, out var reject));
        TestAssert.AreEqual(AuthorityRejectCode.MalformedEnvelope, reject.Code);
    }

    [TestMethod]
    public void AHiveTurretCarriesItsEngagedStateAndAimToTheClient()
    {
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(HiveTurret(turretState: 2, turretInRange: true), out var data));
        TestAssert.HasCount(SpaceEnemyStateCodec.FixedSize, data);
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryDecode(data, 0, data.Length, out var decoded, out var reject), reject.ToString());
        TestAssert.AreEqual((byte)2, decoded.TurretState);
        TestAssert.IsTrue(decoded.TurretTargetInShootRange);
        TestAssert.AreEqual(.6f, decoded.PrepareLength, 1e-6f);
        TestAssert.AreEqual(.8f, decoded.AnimationPower, 1e-6f);
        TestAssert.IsFalse(HiveTurret().TurretTargetInShootRange);
    }

    [TestMethod]
    public void AnImpossibleTurretStateIsRefused()
    {
        TestAssert.IsFalse(SpaceEnemyStateCodec.TryEncode(HiveTurret(turretState: 3), out _),
            "A state above the largest vanilla value never encodes.");
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(HiveTurret(), out var data));
        var corrupt = (byte[])data.Clone();
        corrupt[data.Length - 5] = 3;
        TestAssert.IsFalse(SpaceEnemyStateCodec.TryDecode(corrupt, 0, corrupt.Length, out _, out var reject));
        TestAssert.AreEqual(AuthorityRejectCode.MalformedEnvelope, reject.Code);
    }

    [TestMethod]
    public void ACorruptBlobIsRefused()
    {
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(HiveUnit(), out var data));
        var unknownKind = (byte[])data.Clone();
        unknownKind[0] = 0;
        TestAssert.IsFalse(SpaceEnemyStateCodec.TryDecode(unknownKind, 0, unknownKind.Length, out _, out var kindReject));
        TestAssert.AreEqual(AuthorityRejectCode.MalformedEnvelope, kindReject.Code);
        var badFlags = (byte[])data.Clone();
        badFlags[1] |= 0x80;
        TestAssert.IsFalse(SpaceEnemyStateCodec.TryDecode(badFlags, 0, badFlags.Length, out _, out _));
        for (var length = 0; length < data.Length; length++)
            TestAssert.IsFalse(SpaceEnemyStateCodec.TryDecode(data, 0, length, out _, out _));
        var padded = new byte[data.Length + 1];
        System.Buffer.BlockCopy(data, 0, padded, 0, data.Length);
        TestAssert.IsFalse(SpaceEnemyStateCodec.TryDecode(padded, 0, padded.Length, out _, out _));
        TestAssert.IsFalse(SpaceEnemyStateCodec.TryEncode(new SpaceEnemyState(), out _), "Unknown kind never encodes.");
    }

    [TestMethod]
    public void KindDerivationPrefersRelayOverCoreOverUnit()
    {
        TestAssert.AreEqual(SpaceEnemyKind.SpaceRelay,
            SpaceEnemyKinds.FromComponentIds(3, 4, 5, 6, 7, 8, 9, dfRelayId: 10, unitId: 11, builderId: 12));
        TestAssert.AreEqual(SpaceEnemyKind.SpaceTinder,
            SpaceEnemyKinds.FromComponentIds(0, 0, 0, 0, 0, 0, dfTinderId: 9, dfRelayId: 0, unitId: 0, builderId: 0));
        TestAssert.AreEqual(SpaceEnemyKind.SpaceCore,
            SpaceEnemyKinds.FromComponentIds(dfSCoreId: 3, 0, 0, 0, 0, 0, 0, 0, unitId: 4, builderId: 5));
        TestAssert.AreEqual(SpaceEnemyKind.SpaceUnit,
            SpaceEnemyKinds.FromComponentIds(0, 0, 0, 0, 0, 0, 0, 0, unitId: 4, builderId: 5));
        TestAssert.AreEqual(SpaceEnemyKind.SpaceTurret,
            SpaceEnemyKinds.FromComponentIds(0, 0, 0, 0, 0, dfSTurretId: 8, 0, 0, 0, 0));
        TestAssert.AreEqual(SpaceEnemyKind.Unknown,
            SpaceEnemyKinds.FromComponentIds(0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        TestAssert.AreEqual(SpaceEnemyKind.SpaceTurret,
            SpaceEnemyKinds.FromComponentIds(0, 0, 0, 0, 0, 8, 0, 0, 0, builderId: 12));
        TestAssert.AreEqual(SpaceEnemyKind.SpaceConnector,
            SpaceEnemyKinds.FromComponentIds(0, 0, 5, 0, 0, 0, 0, 0, 0, builderId: 12));
        TestAssert.AreEqual(SpaceEnemyKind.SpaceReplicator,
            SpaceEnemyKinds.FromComponentIds(0, 0, 0, 6, 0, 0, 0, 0, 0, builderId: 12));
    }

    [TestMethod]
    public void AHostEnemyIsCreatedAsADisplayShell()
    {
        var pools = new FakePools();
        var binding = new SpaceEnemyBinding(pools);
        TestAssert.IsTrue(binding.ApplyState(Key(7, 1), Encoded(HiveUnit())));
        TestAssert.IsTrue(pools.EnemyExists(7));
        TestAssert.AreEqual(SpaceEnemyKind.SpaceUnit, pools.Shells[7].Kind);
        TestAssert.AreEqual(1, binding.BoundCount);
    }

    [TestMethod]
    public void UpdatingABoundEnemyWritesAbsoluteValuesWithoutReallocating()
    {
        var pools = new FakePools();
        var binding = new SpaceEnemyBinding(pools);
        TestAssert.IsTrue(binding.ApplyState(Key(7, 1), Encoded(HiveUnit(hp: 500))));
        TestAssert.IsTrue(binding.ApplyState(Key(7, 1), Encoded(HiveUnit(hp: 300))));
        TestAssert.AreEqual(300, pools.Shells[7].Hp);
        TestAssert.AreEqual(1L, binding.ShellsCreated);
        TestAssert.AreEqual(1, binding.BoundCount);
    }

    [TestMethod]
    public void MigrationKeepsTheSameKey()
    {
        var pools = new FakePools();
        var binding = new SpaceEnemyBinding(pools);
        var key = Key(7, 1);
        var here = HiveUnit();
        TestAssert.IsTrue(binding.ApplyState(key, Encoded(here)));
        var moved = new SpaceEnemyState(SpaceEnemyKind.SpaceUnit, true, true,
            8113, 45, 2, 3, 0, 1000201, 1000101, 0, 0, 0,
            8000.0, -350.0, 2200.0, 0f, 0f, 0f, 1f, 5f, 0f, 0f, 480, 900, 2, 0);
        TestAssert.IsTrue(binding.ApplyState(key, Encoded(moved)));
        TestAssert.AreEqual(1000201, pools.Shells[7].AstroId);
        TestAssert.AreEqual(1000101, pools.Shells[7].OriginAstroId);
        TestAssert.AreEqual(480, pools.Shells[7].Hp);
        TestAssert.AreEqual(1L, binding.ShellsCreated, "Migration updates in place, never respawns.");
    }

    [TestMethod]
    public void ARebuiltSlotIsANewKeyThatNeverInheritsTheOldShell()
    {
        var pools = new FakePools();
        var binding = new SpaceEnemyBinding(pools);
        var first = Key(7, 1);
        TestAssert.IsTrue(binding.ApplyState(first, Encoded(HiveCore())));
        binding.RemoveMember(first);
        TestAssert.IsFalse(pools.EnemyExists(7));
        TestAssert.HasCount(1, pools.Removed);
        var second = Key(7, 2);
        TestAssert.IsTrue(binding.ApplyState(second, Encoded(HiveUnit(hp: 880))));
        TestAssert.IsTrue(pools.EnemyExists(7));
        TestAssert.AreEqual(880, pools.Shells[7].Hp);
    }

    [TestMethod]
    public void ABaselineReconcilesTheWholeScopeAtomically()
    {
        var pools = new FakePools();
        var binding = new SpaceEnemyBinding(pools);
        TestAssert.IsTrue(binding.ApplyState(Key(4, 1), Encoded(HiveCore())));
        TestAssert.IsTrue(binding.ApplyState(Key(9, 1), Encoded(Relay())));
        var members = new List<SnapshotMemberRecord>
        {
            new(Key(4, 1), 7, Encoded(HiveCore())),
            new(Key(9, 1), 6, Encoded(Relay(dock: 3)))
        };
        binding.ReconcileBaseline(members);
        TestAssert.IsTrue(pools.EnemyExists(4));
        TestAssert.IsTrue(pools.EnemyExists(9));
        TestAssert.AreEqual(3, pools.Shells[9].DockIndex);
        TestAssert.AreEqual(2, binding.BoundCount);
        binding.ReconcileBaseline(new List<SnapshotMemberRecord>());
        TestAssert.AreEqual(0, binding.BoundCount);
        TestAssert.AreEqual(2L, binding.MembersRemoved);
    }

    [TestMethod]
    public void AKeyOutsideTheBindingScopeIsRefused()
    {
        var pools = new FakePools();
        var binding = new SpaceEnemyBinding(pools);
        TestAssert.IsFalse(binding.ApplyState(Key(1, 1), null));
        TestAssert.AreEqual(1L, binding.StatesRefused);
        var groundKey = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 1, 1);
        TestAssert.IsFalse(binding.ApplyState(groundKey, Encoded(HiveUnit())));
        TestAssert.AreEqual(2L, binding.StatesRefused);
    }
}
