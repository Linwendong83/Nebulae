
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// the canonical factory combat state codec, the host's slot generation
/// tracker, and the client binding that rebuilds entity → combat/construct references from host
/// facts. Everything here runs without a game process.
/// </summary>
[TestClass]
public class FactoryCombatStateTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0A08080808080808, 0x0B08080808080808);

    private static ObjectKey Key(int nativeId, long generation) =>
        ObjectKey.Create(Epoch, PoolKind.Entity, 101, nativeId, generation);

    private static FactoryCombatState Damaged(int hp, int hpMax) => new(
        true, hp, hpMax, hpRecover: 2, hpIncoming: -5,
        hasConstructStat: true, damageRate: 250.5f, repairerCount: 1, repairerModuleId: 7, repairerValue: 0.5f);

    // ---------------------------------------------------------------- codec

    [TestMethod]
    public void ACombatStateRoundTripsEveryField()
    {
        var state = Damaged(hp: 640, hpMax: 1000);

        TestAssert.IsTrue(FactoryCombatStateCodec.TryEncode(state, out var data));
        TestAssert.IsTrue(FactoryCombatStateCodec.TryDecode(data, 0, data.Length, out var decoded, out var reject),
            reject.ToString());

        TestAssert.IsTrue(decoded.HasCombatStat);
        TestAssert.AreEqual(640, decoded.Hp);
        TestAssert.AreEqual(1000, decoded.HpMax);
        TestAssert.AreEqual(2, decoded.HpRecover);
        TestAssert.AreEqual(-5, decoded.HpIncoming);
        TestAssert.IsTrue(decoded.HasConstructStat);
        TestAssert.AreEqual(250.5f, decoded.DamageRate, "The rate survives the wire bit-exactly.");
        TestAssert.AreEqual(1, decoded.RepairerCount);
        TestAssert.AreEqual(7, decoded.RepairerModuleId);
        TestAssert.AreEqual(0.5f, decoded.RepairerValue, 1e-6f);
    }

    [TestMethod]
    public void HasNoStatIsAnEncodedStatementNotAnAbsence()
    {
        // The full-health state must survive the wire as its own statement — the client's binding
        // clears references on exactly this value, so a decode-as-default would be a full heal.
        var state = new FactoryCombatState();

        TestAssert.IsTrue(FactoryCombatStateCodec.TryEncode(state, out var data));
        TestAssert.IsTrue(FactoryCombatStateCodec.TryDecode(data, 0, data.Length, out var decoded, out _));
        TestAssert.IsFalse(decoded.HasCombatStat);
        TestAssert.IsFalse(decoded.HasConstructStat);
    }

    [TestMethod]
    public void TheEncodedLayoutIsFixedAndBounded()
    {
        TestAssert.IsTrue(FactoryCombatStateCodec.TryEncode(Damaged(1, 2), out var data));
        TestAssert.HasCount(33, data, "Flags + 4 HP ints + 4 construct ints: the layout is fixed.");
        TestAssert.IsLessThanOrEqualTo(AuthorityLimits.StateRecordMaxBytes, data.Length);

        TestAssert.IsTrue(FactoryCombatStateCodec.TryEncode(Damaged(1, 2), out var again));
        CollectionAssert.AreEqual(data, again, "Encoding is deterministic: the dirt comparison depends on it.");
    }

    [TestMethod]
    public void ACorruptStateBlobIsRefusedRatherThanDecodedAsFullHealth()
    {
        TestAssert.IsTrue(FactoryCombatStateCodec.TryEncode(Damaged(1, 2), out var data));

        // Unknown flag bits mean the sender speaks a layout this receiver does not know.
        var foreignFlags = (byte[])data.Clone();
        foreignFlags[0] |= 0x80;
        TestAssert.IsFalse(FactoryCombatStateCodec.TryDecode(foreignFlags, 0, foreignFlags.Length, out _, out var flagReject));
        TestAssert.AreEqual(AuthorityRejectCode.MalformedEnvelope, flagReject.Code);

        var truncated = new byte[data.Length - 4];
        Buffer.BlockCopy(data, 0, truncated, 0, truncated.Length);
        TestAssert.IsFalse(FactoryCombatStateCodec.TryDecode(truncated, 0, truncated.Length, out _, out _));

        var padded = new byte[data.Length + 2];
        Buffer.BlockCopy(data, 0, padded, 0, data.Length);
        TestAssert.IsFalse(FactoryCombatStateCodec.TryDecode(padded, 0, padded.Length, out _, out _),
            "Trailing bytes mean the two sides disagree about the layout.");
    }

    // ---------------------------------------------------------------- slot generations

    [TestMethod]
    public void AReusedSlotGetsANewGenerationEveryTime()
    {
        var tracker = new SlotGenerationTracker();

        TestAssert.AreEqual(1L, tracker.ObserveOccupied(7));
        tracker.ObserveOccupied(8);
        tracker.EndScan();
        TestAssert.AreEqual(2, tracker.AliveSlots);

        // Slot 7 dies; slot 8 survives.
        tracker.ObserveOccupied(8);
        tracker.EndScan();
        TestAssert.AreEqual(1, tracker.AliveSlots);
        TestAssert.AreEqual(1L, tracker.GenerationOf(7), "Death retires the generation but does not forget it.");

        // The slot is reused: generation 2. Reuse again after another death: generation 3.
        TestAssert.AreEqual(2L, tracker.ObserveOccupied(7));
        tracker.ObserveOccupied(8);
        tracker.EndScan();

        tracker.ObserveOccupied(8);
        tracker.EndScan();
        TestAssert.AreEqual(3L, tracker.ObserveOccupied(7));
    }

    [TestMethod]
    public void ObservingTheSameSlotTwiceInOneScanIsTheSameLiveObject()
    {
        var tracker = new SlotGenerationTracker();
        TestAssert.AreEqual(1L, tracker.ObserveOccupied(7));
        TestAssert.AreEqual(1L, tracker.ObserveOccupied(7));
        tracker.EndScan();
        TestAssert.AreEqual(1, tracker.AliveSlots);
    }

    // ---------------------------------------------------------------- binding

    /// <summary>A plain record of the client-local pools the binding writes through.</summary>
    private sealed class FakePools : IFactoryCombatPools
    {
        public readonly HashSet<int> Entities = [];
        public readonly Dictionary<int, int> EntityCombatRef = [];
        public readonly Dictionary<int, int> EntityConstructRef = [];
        public readonly Dictionary<int, FactoryCombatState> CombatStats = [];
        public readonly Dictionary<int, FactoryCombatState> ConstructStats = [];
        public readonly List<int> ReleasedCombat = [];
        public readonly List<int> ReleasedConstruct = [];

        private int combatCursor = 1;
        private int constructCursor = 1;

        public bool EntityExists(int entityId) => Entities.Contains(entityId);

        public int ReadEntityCombatStat(int entityId) => EntityCombatRef.TryGetValue(entityId, out var v) ? v : 0;

        public void WriteEntityCombatStat(int entityId, int statId) => EntityCombatRef[entityId] = statId;

        public int AllocateCombatStat()
        {
            var id = combatCursor++;
            CombatStats[id] = new FactoryCombatState();
            return id;
        }

        public void ReleaseCombatStat(int statId)
        {
            TestAssert.IsTrue(CombatStats.Remove(statId), "A released stat must still be allocated.");
            ReleasedCombat.Add(statId);
        }

        public void WriteCombatStat(int statId, in FactoryCombatState state, int planetId, int entityId) =>
            CombatStats[statId] = state;

        public int ReadEntityConstructStat(int entityId) => EntityConstructRef.TryGetValue(entityId, out var v) ? v : 0;

        public void WriteEntityConstructStat(int entityId, int statId) => EntityConstructRef[entityId] = statId;

        public int AllocateConstructStat()
        {
            var id = constructCursor++;
            ConstructStats[id] = new FactoryCombatState();
            return id;
        }

        public void ReleaseConstructStat(int statId)
        {
            TestAssert.IsTrue(ConstructStats.Remove(statId), "A released stat must still be allocated.");
            ReleasedConstruct.Add(statId);
        }

        public void WriteConstructStat(int statId, in FactoryCombatState state, int entityId) =>
            ConstructStats[statId] = state;
    }

    [TestMethod]
    public void AHostDamagedBuildingIsReboundToLocalStats()
    {
        // "两建筑有不同 pool stat index 仍导入正确": from the client's view the host's raw stat
        // indexes do not even appear — each entity gets its own local stat, values absolute.
        var pools = new FakePools();
        pools.Entities.Add(1);
        pools.Entities.Add(2);
        var bindings = new List<(ObjectKey, int)>();
        var binding = new FactoryCombatBinding(101, pools, (key, id) => bindings.Add((key, id)));

        var first = Key(1, 1);
        var second = Key(2, 1);
        TestAssert.IsTrue(binding.ApplyState(first, Encoded(Damaged(640, 1000))));
        TestAssert.IsTrue(binding.ApplyState(second, Encoded(Damaged(120, 200))));

        var firstStat = pools.ReadEntityCombatStat(1);
        var secondStat = pools.ReadEntityCombatStat(2);
        TestAssert.IsTrue(firstStat > 0 && secondStat > 0);
        TestAssert.AreNotEqual(firstStat, secondStat, "Two entities never share one local stat.");
        TestAssert.AreEqual(640, pools.CombatStats[firstStat].Hp);
        TestAssert.AreEqual(120, pools.CombatStats[secondStat].Hp);
        TestAssert.AreEqual(250.5f, pools.ConstructStats[pools.ReadEntityConstructStat(1)].DamageRate, 1e-6f);
        TestAssert.HasCount(2, bindings, "Allocated local ids are also recorded as component bindings.");
        TestAssert.AreEqual(2, binding.BoundCount);
    }

    [TestMethod]
    public void UpdatingABoundEntityWritesAbsoluteValuesWithoutReallocating()
    {
        var pools = new FakePools();
        pools.Entities.Add(1);
        var binding = new FactoryCombatBinding(101, pools);

        TestAssert.IsTrue(binding.ApplyState(Key(1, 1), Encoded(Damaged(640, 1000))));
        var allocated = pools.ReadEntityCombatStat(1);
        TestAssert.IsTrue(binding.ApplyState(Key(1, 1), Encoded(Damaged(300, 1000))));

        TestAssert.AreEqual(allocated, pools.ReadEntityCombatStat(1));
        TestAssert.AreEqual(300, pools.CombatStats[allocated].Hp);
        TestAssert.IsEmpty(pools.ReleasedCombat);
        TestAssert.AreEqual(2L, binding.StatsAllocated, "One combat stat and one construct stat, no more.");
    }

    [TestMethod]
    public void FullHealthClearsTheReferenceAndRecyclesTheLocalStat()
    {
        var pools = new FakePools();
        pools.Entities.Add(1);
        var binding = new FactoryCombatBinding(101, pools);
        var key = Key(1, 1);

        TestAssert.IsTrue(binding.ApplyState(key, Encoded(Damaged(640, 1000))));
        var localStat = pools.ReadEntityCombatStat(1);
        TestAssert.IsGreaterThan(0, localStat);

        // Host: the building was repaired to full — combat and construct records are both gone.
        var healed = new FactoryCombatState();
        TestAssert.IsTrue(binding.ApplyState(key, Encoded(healed)));

        TestAssert.AreEqual(0, pools.ReadEntityCombatStat(1));
        TestAssert.AreEqual(0, pools.ReadEntityConstructStat(1));
        CollectionAssert.AreEqual(new[] { localStat }, pools.ReleasedCombat,
            "The binding's own stat is recycled, not leaked.");
        TestAssert.HasCount(1, pools.ReleasedConstruct, "The construct stat goes back with the combat stat.");
        TestAssert.AreEqual(0, binding.BoundCount);
    }

    [TestMethod]
    public void ADanglingImportedReferenceIsClearedWithoutOwningAStat()
    {
        // A factory import leaves entity references pointing at the host's global pool. The host
        // says "no stat": the reference must be zeroed even though this binding never allocated one.
        var pools = new FakePools();
        pools.Entities.Add(1);
        pools.EntityCombatRef[1] = 9123; // dangling import
        pools.EntityConstructRef[1] = 77;
        var binding = new FactoryCombatBinding(101, pools);

        TestAssert.IsTrue(binding.ApplyState(Key(1, 1), Encoded(new FactoryCombatState())));

        TestAssert.AreEqual(0, pools.ReadEntityCombatStat(1));
        TestAssert.AreEqual(0, pools.ReadEntityConstructStat(1));
        TestAssert.IsEmpty(pools.ReleasedCombat, "Nothing was allocated, so nothing is released.");
        TestAssert.AreEqual(2L, binding.ReferencesCleared);
    }

    [TestMethod]
    public void ARebuiltSlotIsANewKeyThatNeverInheritsTheOldBinding()
    {
        // 满血→再受损→死亡→同 entity ID 重建: the destroyed entity's local stat is released with
        // its key, and the rebuilt one (new generation, same slot) gets a fresh stat.
        var pools = new FakePools();
        pools.Entities.Add(3);
        var binding = new FactoryCombatBinding(101, pools);

        var firstGeneration = Key(3, 1);
        TestAssert.IsTrue(binding.ApplyState(firstGeneration, Encoded(Damaged(500, 900))));
        var firstStat = pools.ReadEntityCombatStat(3);

        binding.RemoveMember(firstGeneration);
        TestAssert.AreEqual(0, pools.ReadEntityCombatStat(3));
        CollectionAssert.AreEqual(new[] { firstStat }, pools.ReleasedCombat);
        TestAssert.AreEqual(1L, binding.MembersRemoved);

        var secondGeneration = Key(3, 2);
        TestAssert.IsTrue(binding.ApplyState(secondGeneration, Encoded(Damaged(880, 900))));
        var secondStat = pools.ReadEntityCombatStat(3);
        TestAssert.AreNotEqual(firstStat, secondStat, "The rebuild allocates fresh, not the dead key's slot.");
        TestAssert.AreEqual(880, pools.CombatStats[secondStat].Hp);
    }

    [TestMethod]
    public void ABaselineReconcilesTheWholeScopeAtomically()
    {
        var pools = new FakePools();
        pools.Entities.Add(1);
        pools.Entities.Add(2);
        pools.Entities.Add(3);
        var binding = new FactoryCombatBinding(101, pools);

        // Live state before the baseline: entities 1 and 2 are damaged.
        TestAssert.IsTrue(binding.ApplyState(Key(1, 1), Encoded(Damaged(640, 1000))));
        TestAssert.IsTrue(binding.ApplyState(Key(2, 1), Encoded(Damaged(120, 200))));

        // The baseline keeps 1 (now healed), keeps 2 (still damaged), drops nothing else — and
        // entity 3's dangling import is cleared by its explicit no-stat record.
        pools.EntityCombatRef[3] = 555;
        var members = new List<SnapshotMemberRecord>
        {
            new(Key(1, 1), 5, Encoded(new FactoryCombatState())),
            new(Key(2, 1), 6, Encoded(Damaged(100, 200))),
            new(Key(3, 1), 1, Encoded(new FactoryCombatState()))
        };
        binding.ReconcileBaseline(members);

        TestAssert.AreEqual(0, pools.ReadEntityCombatStat(1), "The baseline's no-stat record heals the binding.");
        TestAssert.AreEqual(100, pools.CombatStats[pools.ReadEntityCombatStat(2)].Hp);
        TestAssert.AreEqual(0, pools.ReadEntityCombatStat(3), "The dangling import is repaired by the baseline too.");
        TestAssert.AreEqual(1, binding.BoundCount, "Only keys the baseline names — and that still hold stats — stay bound.");
        TestAssert.HasCount(1, pools.ReleasedCombat, "Entity 1's superseded stat was recycled by its no-stat record.");

        // A baseline that drops the bound key releases it with the key.
        binding.ReconcileBaseline(new List<SnapshotMemberRecord>());
        TestAssert.AreEqual(0, binding.BoundCount);
        TestAssert.AreEqual(1L, binding.MembersRemoved);
        TestAssert.HasCount(2, pools.ReleasedCombat, "Dropped keys release their local stats exactly once.");
    }

    [TestMethod]
    public void ARecordForAnEntityTheClientCannotSeeIsDeferredNotGuessed()
    {
        var pools = new FakePools();
        var binding = new FactoryCombatBinding(101, pools);

        TestAssert.IsFalse(binding.ApplyState(Key(4, 1), Encoded(Damaged(640, 1000))));
        TestAssert.AreEqual(1L, binding.StatesDeferred);
        TestAssert.AreEqual(0L, binding.StatsAllocated);
        TestAssert.IsEmpty(pools.CombatStats, "Nothing was allocated against a factory that does not exist yet.");

        // The factory imports: the next record for the key converges.
        pools.Entities.Add(4);
        TestAssert.IsTrue(binding.ApplyState(Key(4, 1), Encoded(Damaged(640, 1000))));
        TestAssert.AreEqual(2L, binding.StatsAllocated, "Combat and construct, one each.");
    }

    [TestMethod]
    public void AKeyOutsideTheBindingScopeIsRefused()
    {
        var pools = new FakePools();
        var binding = new FactoryCombatBinding(101, pools);

        TestAssert.IsFalse(binding.ApplyState(Key(1, 1), null),
            "Null state bytes are refused, never decoded as an empty record.");
        TestAssert.AreEqual(1L, binding.StatesRefused);

        // A different planet's entity is another binding's business.
        var otherPlanet = ObjectKey.Create(Epoch, PoolKind.Entity, 102, 1, 1);
        TestAssert.IsFalse(binding.ApplyState(otherPlanet, Encoded(Damaged(1, 2))));
        TestAssert.AreEqual(2L, binding.StatesRefused);
    }
}
