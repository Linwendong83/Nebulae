using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// The state-side gate the replicator's capture loop now uses for every member of an enemy or craft
/// scope, not just the moving units. Two properties are load-bearing and both are locked here.
/// </summary>
/// <remarks>
/// <para>
/// The gate must agree with the canonical-byte comparison it sits in front of: if it reported
/// "unchanged" for a record whose bytes moved, a fact would stop reaching subscribers. It must also
/// report "unchanged" for a record that genuinely did not move, which is the whole point — otherwise
/// the encoder still runs every tick and the saving is zero.
/// </para>
/// <para>
/// A third property guards the one case where a naive implementation is wrong: the host allocates a
/// fresh native-component array on every reading, so reference equality would call an unchanged unit
/// "changed" and the gate would never suppress anything.
/// </para>
/// </remarks>
[TestClass]
public class EnemyStateGateTest
{
    private const int Planet = 101;

    private static GroundEnemyState Unit(int hp = 500, byte behavior = 0, long sampleTick = 0, byte[]? nativeUnit = null,
        float posX = 10f) => new(
        GroundEnemyKind.GroundUnit, true, false,
        protoId: 8130, modelIndex: 45, owner: 2, port: 3, stateFlags: 0,
        astroId: Planet, originAstroId: Planet, baseId: 7, builderIndex: 0, level: 1,
        posX: posX, posY: 0f, posZ: 20f, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 0f, velY: 0f, velZ: 0f, hp: hp, hpMax: 900, hpRecover: 2, hpIncoming: 0,
        unitAnimation: 1.25f, unitDisturb: .4f, unitSteering: -.2f, unitSpeed: 12f,
        unitBehavior: behavior, sampleTick: sampleTick, unitStateTick: 17, nativeUnit: nativeUnit);

    private static GroundEnemyState BaseCore(int hp = 5000, int level = 3, float posX = 0f) => new(
        GroundEnemyKind.GroundBase, true, false,
        protoId: 8100, modelIndex: 40, owner: 1, port: 0, stateFlags: 0,
        astroId: Planet, originAstroId: Planet, baseId: 4, builderIndex: 5, level: level,
        posX: posX, posY: 0f, posZ: 0f, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 0f, velY: 0f, velZ: 0f, hp: hp, hpMax: 8000, hpRecover: 0, hpIncoming: 0);

    private static GroundEnemyState Turret(byte state = 0, bool inRange = false) => new(
        GroundEnemyKind.GroundTurret, true, false,
        protoId: 8106, modelIndex: 42, owner: 1, port: 0, stateFlags: 0,
        astroId: Planet, originAstroId: Planet, baseId: 2, builderIndex: 3, level: 1,
        posX: 12f, posY: 0f, posZ: 8f, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 0f, velY: 0f, velZ: 0f, hp: 1200, hpMax: 1200, hpRecover: 0, hpIncoming: 0,
        animationTime: 11.5f, prepareLength: .6f, workingLength: 0f, animationState: 2, animationPower: .8f,
        turretState: state, turretTargetInShootRange: inRange);

    private static GroundEnemyState Builder(int matter = 40, int matterMax = 100) => new(
        GroundEnemyKind.GroundBuilder, true, false,
        protoId: 8101, modelIndex: 41, owner: 1, port: 0, stateFlags: 0,
        astroId: Planet, originAstroId: Planet, baseId: 2, builderIndex: 3, level: 1,
        posX: 5f, posY: 0f, posZ: 6f, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 0f, velY: 0f, velZ: 0f, hp: 400, hpMax: 400, hpRecover: 0, hpIncoming: 0,
        animationTime: 3.5f, animationState: 3, animationPower: 1f,
        builderSp: 120, builderSpMax: 400, matter: matter, matterMax: matterMax,
        energy: 9000, energyMax: 9000, matterChange: -1, energyChange: -2);

    private static SpaceEnemyState SpaceUnit(int hp = 700, byte behavior = 0, long sampleTick = 0, byte[]? nativeUnit = null) => new(
        SpaceEnemyKind.SpaceUnit, true, false,
        protoId: 8111, modelIndex: 51, owner: 2, port: 1, stateFlags: 0,
        astroId: 0, originAstroId: 1000000, dockIndex: 0, builderIndex: 0, level: 2,
        posX: 1.5, posY: 2.5, posZ: 3.5, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 0f, velY: 0f, velZ: 0f, hp: hp, hpMax: 1200, hpRecover: 1, hpIncoming: 0,
        unitAnimation: .5f, unitSteering: .1f, unitSpeed: 8f, unitDisturb: .2f,
        unitBehavior: behavior, sampleTick: sampleTick, unitStateTick: 5, nativeUnit: nativeUnit);

    private static SpaceEnemyState SpaceCore(int hp = 40000, int matter = 10, int matterMax = 50) => new(
        SpaceEnemyKind.SpaceCore, true, false,
        protoId: 8102, modelIndex: 50, owner: 1, port: 0, stateFlags: 0,
        astroId: 0, originAstroId: 1000000, dockIndex: 0, builderIndex: 2, level: 1,
        posX: 0.0, posY: 0.0, posZ: 0.0, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 0f, velY: 0f, velZ: 0f, hp: hp, hpMax: 40000, hpRecover: 0, hpIncoming: 0,
        builderSp: 20, builderSpMax: 40, matter: matter, matterMax: matterMax, matterChange: 1,
        energy: 100, energyMax: 200, energyChange: -1);

    private static SpaceEnemyState SpaceTurret(byte state = 0, bool inRange = false) => new(
        SpaceEnemyKind.SpaceTurret, true, false,
        protoId: 8107, modelIndex: 52, owner: 1, port: 0, stateFlags: 0,
        astroId: 0, originAstroId: 1000000, dockIndex: 0, builderIndex: 2, level: 1,
        posX: 9.0, posY: 0.0, posZ: 9.0, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 0f, velY: 0f, velZ: 0f, hp: 800, hpMax: 800, hpRecover: 0, hpIncoming: 0,
        animationTime: 2.5f, turretState: state, turretTargetInShootRange: inRange);

    private static CraftState Craft(int hp = 300, bool dynamic = false, ushort playerOwner = 0, long sampleTick = 0,
        byte[]? nativeFleet = null, byte[]? nativeUnit = null) => new(
        true, dynamic, false,
        protoId: 5001, modelIndex: 61, port: 2, prototype: 1, stateFlags: 0,
        astroId: Planet, owner: 3,
        posX: 1.0, posY: 2.0, posZ: 3.0, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 0f, velY: 0f, velZ: 0f, hp: hp, hpMax: 600, hpRecover: 0, hpIncoming: 0,
        sampleTick: sampleTick, playerOwner: playerOwner, nativeFleet: nativeFleet, nativeUnit: nativeUnit);

    [TestMethod]
    public void ThePerMemberGatePublishesEveryMovedFactAndSuppressesOnlyAnUnchangedReading()
    {
        // The adapters' gate. The polarity is the whole point and it is invisible when wrong: the
        // adapter hands back the previous bytes, the replicator compares them against what it
        // already sent, finds them equal, and the fact never reaches a subscriber. A building's
        // hitpoints are the case that matters in play — a settled fact, not motion.
        TestAssert.IsFalse(EnemyStatePolicy.HasMoved(BaseCore(), BaseCore(), movingUnit: false), "settled, unchanged");
        TestAssert.IsTrue(EnemyStatePolicy.HasMoved(BaseCore(), BaseCore(hp: 4900), movingUnit: false), "base hitpoints");
        TestAssert.IsTrue(EnemyStatePolicy.HasMoved(BaseCore(), BaseCore(level: 4), movingUnit: false), "evolve level");
        TestAssert.IsTrue(EnemyStatePolicy.HasMoved(BaseCore(), BaseCore(posX: 1f), movingUnit: false), "settled pose");
        TestAssert.IsFalse(EnemyStatePolicy.HasMoved(Builder(), Builder(), movingUnit: false), "builder, unchanged");
        TestAssert.IsTrue(EnemyStatePolicy.HasMoved(Builder(), Builder(matter: 41), movingUnit: false), "manufacture progress");
        TestAssert.IsFalse(EnemyStatePolicy.HasMoved(Turret(), Turret(), movingUnit: false), "turret, unchanged");
        TestAssert.IsTrue(EnemyStatePolicy.HasMoved(Turret(), Turret(state: 2), movingUnit: false), "turret state");
        TestAssert.IsTrue(EnemyStatePolicy.HasMoved(Turret(), Turret(inRange: true), movingUnit: false), "target in shoot range");
        TestAssert.IsFalse(EnemyStatePolicy.HasMoved(SpaceCore(), SpaceCore(), movingUnit: false), "space, unchanged");
        TestAssert.IsTrue(EnemyStatePolicy.HasMoved(SpaceCore(), SpaceCore(hp: 39000), movingUnit: false), "space hitpoints");
        TestAssert.IsTrue(EnemyStatePolicy.HasMoved(SpaceCore(), SpaceCore(matter: 11), movingUnit: false), "space manufacture");
        TestAssert.IsTrue(EnemyStatePolicy.HasMoved(SpaceTurret(), SpaceTurret(state: 1), movingUnit: false), "space turret state");
    }

    [TestMethod]
    public void ThePerMemberGateKeepsTheCheckpointPolicyForAMovingUnit()
    {
        // The moving branch must stay the checkpoint policy rather than the byte equality: a unit
        // whose pose drifted against the host's anchor has moved bytes but nothing a subscriber
        // needs, and the checkpoint clause is what eventually re-anchors it.
        var anchored = Unit(sampleTick: 100);
        TestAssert.IsFalse(EnemyStatePolicy.HasMoved(anchored, anchored, movingUnit: true));
        TestAssert.IsFalse(EnemyStatePolicy.HasMoved(anchored, Unit(sampleTick: 100, posX: 12f), movingUnit: true),
            "A unit that only drifted keeps its checkpoint policy.");
        TestAssert.IsTrue(EnemyStatePolicy.HasMoved(anchored, Unit(sampleTick: 100, hp: 480), movingUnit: true), "unit hitpoints");
        TestAssert.IsTrue(EnemyStatePolicy.HasMoved(anchored, Unit(sampleTick: 100, behavior: 4), movingUnit: true), "unit behavior");
        TestAssert.IsTrue(EnemyStatePolicy.HasMoved(anchored,
            Unit(sampleTick: 100 + EnemyStatePolicy.UnitCheckpointIntervalTicks), movingUnit: true),
            "The checkpoint still re-anchors a unit that changed nothing.");
        TestAssert.IsFalse(EnemyStatePolicy.ShouldPublish(anchored, Unit(sampleTick: 105)));
        TestAssert.IsFalse(EnemyStatePolicy.ShouldPublish(SpaceUnit(sampleTick: 100), SpaceUnit(sampleTick: 105)));
        TestAssert.IsTrue(EnemyStatePolicy.ShouldPublish(SpaceUnit(sampleTick: 100), SpaceUnit(sampleTick: 106)));
        // The two branches are not interchangeable: the reading a moving unit suppresses is one a
        // settled member must publish, because for a settled member the record is the fact.
        var drifted = Unit(sampleTick: 100, posX: 12f);
        TestAssert.IsFalse(EnemyStatePolicy.HasMoved(anchored, drifted, movingUnit: true));
        TestAssert.IsTrue(EnemyStatePolicy.HasMoved(BaseCore(), BaseCore(posX: 1f), movingUnit: false));
    }

    [TestMethod]
    public void AnUnchangedReadingIsSuppressedForEveryKindOfMember()
    {
        // Repeated reads of settled bases, builders, turrets and other enemies need no encoding.
        // Each pair below is the same record read twice, which is what a settled member produces.
        TestAssert.IsTrue(EnemyStatePolicy.SameState(BaseCore(), BaseCore()));
        TestAssert.IsTrue(EnemyStatePolicy.SameState(Builder(), Builder()));
        TestAssert.IsTrue(EnemyStatePolicy.SameState(Turret(), Turret()));
        TestAssert.IsTrue(EnemyStatePolicy.SameState(SpaceCore(), SpaceCore()));
        TestAssert.IsTrue(EnemyStatePolicy.SameState(SpaceTurret(), SpaceTurret()));
        TestAssert.IsTrue(CraftStatePolicy.SameState(Craft(), Craft()));
    }

    [TestMethod]
    public void AMovedFactIsNeverSuppressed()
    {
        // One representative change per field group the gate must notice. Anything here that reports
        // "unchanged" is a fact that would stop reaching subscribers, so each line is a regression
        // guard rather than a style preference.
        TestAssert.IsFalse(EnemyStatePolicy.SameState(BaseCore(), BaseCore(hp: 4900)), "hitpoints");
        TestAssert.IsFalse(EnemyStatePolicy.SameState(BaseCore(), BaseCore(level: 4)), "evolve level");
        TestAssert.IsFalse(EnemyStatePolicy.SameState(Builder(), Builder(matter: 41)), "manufacture progress");
        TestAssert.IsFalse(EnemyStatePolicy.SameState(Turret(), Turret(state: 2)), "turret state");
        TestAssert.IsFalse(EnemyStatePolicy.SameState(Turret(), Turret(inRange: true)), "target in shoot range");
        TestAssert.IsFalse(EnemyStatePolicy.SameState(SpaceCore(), SpaceCore(hp: 39000)), "space hitpoints");
        TestAssert.IsFalse(EnemyStatePolicy.SameState(SpaceCore(), SpaceCore(matter: 11)), "space manufacture");
        TestAssert.IsFalse(EnemyStatePolicy.SameState(SpaceTurret(), SpaceTurret(state: 1)), "space turret state");
        TestAssert.IsFalse(CraftStatePolicy.SameState(Craft(), Craft(hp: 299)), "craft hitpoints");
        TestAssert.IsFalse(CraftStatePolicy.SameState(Craft(), Craft(playerOwner: 9)), "craft owner");
    }

    [TestMethod]
    public void AFreshNativeBlobWithTheSameContentIsStillSuppressed()
    {
        // The host re-encodes the native component on every reading, so an unchanged unit hands the
        // gate two different arrays holding the same bytes. Reference equality would call that
        // "changed" and the suppression would never fire, which is exactly the waste being removed.
        var unit = new EnemyUnitComponent { id = 9, enemyId = 3, protoId = 8128, planetId = Planet, stateTick = 17, level = 2 };
        var first = EnemyUnitStateCodec.Encode(unit);
        var second = EnemyUnitStateCodec.Encode(unit);
        TestAssert.AreNotSame(first, second, "Each encoding is a fresh array, as in production.");

        TestAssert.IsTrue(EnemyStatePolicy.SameState(Unit(nativeUnit: first), Unit(nativeUnit: second)));
        TestAssert.IsTrue(EnemyStatePolicy.SameState(SpaceUnit(nativeUnit: first), SpaceUnit(nativeUnit: second)));
        TestAssert.IsFalse(EnemyStatePolicy.SameState(Unit(nativeUnit: first), Unit(nativeUnit: second, behavior: 4)),
            "A different behavior is a different fact even when the blob matches.");

        unit.stateTick = 18;
        var advanced = EnemyUnitStateCodec.Encode(unit);
        TestAssert.IsFalse(EnemyStatePolicy.SameState(Unit(nativeUnit: first), Unit(nativeUnit: advanced)),
            "A moved native clock must survive the gate.");
    }

    [TestMethod]
    public void TheGateAgreesWithTheByteComparisonItReplaces()
    {
        // The gate's contract is equivalence with the canonical-byte comparison the capture loop
        // already performs after encoding. Sampling the pairs that actually occur in a scope — the
        // same record twice, and the same record with one moved fact — proves both directions of that
        // equivalence without asserting a field list by hand.
        var groundPairs = new (GroundEnemyState, GroundEnemyState)[]
        {
            (BaseCore(), BaseCore()),
            (BaseCore(), BaseCore(hp: 4900)),
            (Builder(), Builder()),
            (Builder(), Builder(matter: 41)),
            (Builder(), Builder(matterMax: 120)),
            (Turret(), Turret()),
            (Turret(), Turret(state: 2)),
            (Turret(), Turret(inRange: true)),
            (Unit(), Unit()),
            (Unit(), Unit(hp: 480)),
            (Unit(), Unit(behavior: 9)),
        };
        foreach (var (a, b) in groundPairs)
        {
            TestAssert.AreEqual(SameBytes(Encoded(a), Encoded(b)), EnemyStatePolicy.SameState(a, b), $"ground: {a.Kind}");
        }

        var spacePairs = new (SpaceEnemyState, SpaceEnemyState)[]
        {
            (SpaceCore(), SpaceCore()),
            (SpaceCore(), SpaceCore(hp: 39000)),
            (SpaceCore(), SpaceCore(matter: 11)),
            (SpaceTurret(), SpaceTurret()),
            (SpaceTurret(), SpaceTurret(state: 1)),
            (SpaceTurret(), SpaceTurret(inRange: true)),
            (SpaceUnit(), SpaceUnit()),
            (SpaceUnit(), SpaceUnit(hp: 690)),
            (SpaceUnit(), SpaceUnit(behavior: 4)),
        };
        foreach (var (a, b) in spacePairs)
        {
            TestAssert.AreEqual(SameBytes(Encoded(a), Encoded(b)), EnemyStatePolicy.SameState(a, b), $"space: {a.Kind}");
        }

        var craftPairs = new (CraftState, CraftState)[]
        {
            (Craft(), Craft()),
            (Craft(), Craft(hp: 299)),
            (Craft(), Craft(playerOwner: 9)),
            (Craft(), Craft(dynamic: true)),
            (Craft(), Craft(dynamic: true, sampleTick: 40)),
        };
        foreach (var (a, b) in craftPairs)
        {
            TestAssert.AreEqual(SameBytes(Encoded(a), Encoded(b)), CraftStatePolicy.SameState(a, b));
        }
    }

    private static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    [TestMethod]
    public void AMovingUnitKeepsItsOwnCheckpointPolicy()
    {
        // A moving unit's pose is recomputed by the receiver from the captured native component, so a
        // reading that differs only in pose must stay suppressed: that is what keeps continuous
        // motion off the wire. The gate added for settled members would publish that drift, which is
        // exactly why moving units keep ShouldPublish and do not move onto it.
        var drifted = Unit(sampleTick: 100, posX: 12f);
        var anchored = Unit(sampleTick: 100, posX: 10f);
        TestAssert.IsFalse(EnemyStatePolicy.ShouldPublish(anchored, drifted),
            "A moving unit that only drifted keeps its checkpoint policy.");
        TestAssert.IsFalse(EnemyStatePolicy.SameState(anchored, drifted),
            "The new gate would publish that drift, so a moving unit must not take this path.");

        // A settled member has no such exemption: its record is authoritative, so a drifted pose is
        // a moved fact and must publish.
        var settled = BaseCore();
        TestAssert.IsTrue(EnemyStatePolicy.SameState(settled, BaseCore()),
            "A settled member that changed nothing is suppressed by the new gate.");
        TestAssert.IsFalse(EnemyStatePolicy.SameState(settled, BaseCore(posX: 1f)),
            "A settled member that drifted has moved a fact.");
        TestAssert.IsFalse(EnemyStatePolicy.SameState(settled, BaseCore(hp: 1)),
            "The checkpoint still fires for a settled member whose committed facts moved.");
    }

    [TestMethod]
    public void TheGateSeesTheBlobOnly()
    {
        // Identity travels in the framing, not in the blob, so two readings of the same member are
        // "the same facts" whenever their blobs match. The checkpoint clause in ShouldPublish is what
        // eventually re-anchors a moving unit, and the replicator's revision numbering is what tells
        // subscribers a new record arrived — neither is the gate's job.
        var a = Unit(sampleTick: 10);
        TestAssert.IsTrue(EnemyStatePolicy.SameState(a, Unit(sampleTick: 10)));
        TestAssert.IsTrue(CraftStatePolicy.SameState(Craft(), Craft()));
    }
}
