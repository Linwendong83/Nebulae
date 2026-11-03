using System.Reflection;
using NebulaeModel.Authority;
using global::NebulaeModel.DataStructures;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// identity, generation and revision rules of the pure authority model.
/// </summary>
[TestClass]
public class AuthorityIdentityTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0123456789ABCDEF, 0xFEDCBA9876543210);

    [TestMethod]
    public void DefaultObjectKeyIsInvalid()
    {
        var key = default(ObjectKey);
        TestAssert.IsFalse(key.IsValid, "A default key must not be usable as a real object identity.");
        TestAssert.AreEqual(PoolKind.Unknown, key.Kind);
        TestAssert.AreEqual("-", key.Epoch.ToString());
    }

    [TestMethod]
    public void SamePoolSlotWithANewGenerationIsADifferentObject()
    {
        // The host recycled enemy slot 7 on planet 101. Same id, different object.
        var first = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        var second = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 2);

        TestAssert.AreNotEqual(first, second);
        TestAssert.AreNotEqual(first.GetHashCode(), second.GetHashCode(),
            "Generation must participate in the hash, or a dictionary would treat the two as one key.");
    }

    [TestMethod]
    public void GroundAndSpaceEnemiesWithTheSameIdAreDifferentObjects()
    {
        // Ground scope is the planet; the space pool is one global pool. Enemy 7 exists in both.
        var ground = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 5);
        var space = ObjectKey.Create(Epoch, PoolKind.SpaceEnemy, 1000001, 7, 5);

        TestAssert.AreNotEqual(ground, space);
        TestAssert.AreEqual(101, ground.Scope);
        TestAssert.AreEqual(AuthorityScope.Sector, space.Scope);
    }

    [TestMethod]
    public void DifferentPlanetsWithTheSameEnemyIdAreDifferentObjects()
    {
        var onOnePlanet = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 5);
        var onAnother = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 102, 7, 5);
        TestAssert.AreNotEqual(onOnePlanet, onAnother);
    }

    [TestMethod]
    public void SpaceObjectKeepsItsIdentityWhenItsHiveAstroChanges()
    {
        // A hive's astro slot is an attribute of the object, not part of its identity: the sector
        // pool is shared, so a hive moving between slots must not read as a new object.
        var before = ObjectKey.Create(Epoch, PoolKind.SpaceEnemy, 1000001, 7, 5);
        var after = ObjectKey.Create(Epoch, PoolKind.SpaceEnemy, 1000009, 7, 5);
        TestAssert.AreEqual(before, after);

        var hiveBefore = ObjectKey.Create(Epoch, PoolKind.Hive, 1000001, 3, 5);
        var hiveAfter = ObjectKey.Create(Epoch, PoolKind.Hive, 1000001, 3, 5);
        TestAssert.AreEqual(hiveBefore, hiveAfter);
    }

    [TestMethod]
    public void AnObjectKeepsItsIdentityWhenItMigratesAcrossTheGalaxy()
    {
        // the owning star/hive and the current astro are attributes of an object, not
        // relocation identity. Vanilla moves objects this way: a dark fog relay carries a mutable
        // DFRelayComponent.targetAstroId while it flies to another planet, and a fleet craft crosses
        // stars. The key must be derived from the pool the object lives in, not from where it
        // currently is, or every migration would look like a despawn plus a spawn.
        var sectorCraft = ObjectKey.Create(Epoch, PoolKind.SpaceCraft, 1000003, 42, 1);
        // The same craft later orbits a different star's hive; only its attributes changed.
        var sameCraft = ObjectKey.Create(Epoch, PoolKind.SpaceCraft, 1000007, 42, 1);
        TestAssert.AreEqual(sectorCraft, sameCraft);

        // The planet a ground object sits on is *not* an attribute: ground pools are per planet, so
        // the same native id on another planet is genuinely another object.
        var onFirstPlanet = ObjectKey.Create(Epoch, PoolKind.Entity, 101, 42, 1);
        var onSecondPlanet = ObjectKey.Create(Epoch, PoolKind.Entity, 202, 42, 1);
        TestAssert.AreNotEqual(onFirstPlanet, onSecondPlanet);
    }

    [TestMethod]
    public void AMigratingObjectCanBeUpdatedInItsOriginalScopeAfterItMoves()
    {
        // The practical consequence of the previous test: a state update addressed by the key stays
        // valid even though the object's current astro changed, because the key never mentioned it.
        var replica = new ReplicaVersionState(Epoch);
        var scope = replica.GetOrCreateScope(new ScopeKey(PoolKind.SpaceCraft, AuthorityScope.Sector));
        scope.AdoptSubscription(scope.SubscriptionEpoch + 1, SubscriptionPhase.Live);

        var key = ObjectKey.Create(Epoch, PoolKind.SpaceCraft, 1000003, 42, 1);
        TestAssert.AreEqual(ReplicaApplyResult.Applied, scope.Spawn(key, 1));

        // Host-side migration, then a state update for the same object.
        var afterMigration = ObjectKey.Create(Epoch, PoolKind.SpaceCraft, 1000007, 42, 1);
        TestAssert.AreEqual(key, afterMigration);
        TestAssert.AreEqual(ReplicaApplyResult.Applied, scope.ApplyState(afterMigration, 2));
        TestAssert.IsTrue(scope.TryGetVersion(key, out var version));
        TestAssert.AreEqual(2L, version.Revision);
        TestAssert.AreEqual(1, scope.MemberCount, "A migration must not duplicate the member.");
    }

    [TestMethod]
    public void GroundPoolRejectsASectorAstroInsteadOfSilentlySharingTheSectorScope()
    {
        // Mapping a sector astro id to a ground pool would merge every planet into scope 0. That is
        // the exact class of identity bug the scope rule exists to prevent, so it must fail loudly.
        TestAssert.IsFalse(ObjectKey.TryCreate(Epoch, PoolKind.GroundEnemy, 1000001, 7, 5, out _));
        TestAssert.IsFalse(ObjectKey.TryCreate(Epoch, PoolKind.Entity, 0, 7, 5, out _));
        TestAssert.IsFalse(ObjectKey.TryCreate(Epoch, PoolKind.GroundEnemy, 101, 0, 5, out _),
            "Native id 0 is an empty pool slot, never an object.");
        TestAssert.IsFalse(ObjectKey.TryCreate(Epoch, PoolKind.GroundEnemy, 101, 7, 0, out _),
            "Generation 0 means 'no generation', which is not a usable identity.");
        TestAssert.IsFalse(ObjectKey.TryCreate(default, PoolKind.GroundEnemy, 101, 7, 5, out _),
            "A key without a valid authority epoch cannot be trusted.");
    }

    [TestMethod]
    public void CreateThrowsWhereTryCreateRejects()
    {
        TestAssert.ThrowsExactly<ArgumentException>(() =>
            ObjectKey.Create(Epoch, PoolKind.Unknown, 101, 7, 5));
    }

    [TestMethod]
    public void KeysFromAnotherAuthorityEpochNeverMatch()
    {
        var other = new AuthorityEpoch(1, 1);
        var here = ObjectKey.Create(Epoch, PoolKind.Entity, 101, 7, 5);
        var there = ObjectKey.Create(other, PoolKind.Entity, 101, 7, 5);
        TestAssert.AreNotEqual(here, there);

        var replica = new ReplicaVersionState(Epoch);
        var scope = replica.GetOrCreateScope(new ScopeKey(PoolKind.Entity, 101));
        scope.AdoptSubscription(scope.SubscriptionEpoch + 1, SubscriptionPhase.Live);
        TestAssert.AreEqual(ReplicaApplyResult.RejectedInvalid, scope.Spawn(there, 1),
            "A packet from a previous world load must not be able to create an object in this one.");
    }

    [TestMethod]
    public void ScopeRuleAgreesWithTheNormalizationAlreadyShipped()
    {
        // The new protocol must not disagree with the identity the existing code already uses.
        foreach (var planetId in new[] { 1, 101, 204899 })
        {
            TestAssert.AreEqual(planetId, CombatGenerationState.NormalizeAstro(planetId));
            TestAssert.IsTrue(ObjectKey.TryCreate(Epoch, PoolKind.GroundEnemy, planetId, 3, 1, out var key));
            TestAssert.AreEqual(CombatGenerationState.NormalizeAstro(planetId), key.Scope);
        }

        // Hive astros all collapse onto the single sector scope in both implementations.
        TestAssert.AreEqual(0, CombatGenerationState.NormalizeAstro(1000001));
        TestAssert.IsTrue(ObjectKey.TryCreate(Epoch, PoolKind.SpaceEnemy, 1000001, 3, 1, out var sectorKey));
        TestAssert.AreEqual(CombatGenerationState.NormalizeAstro(1000001), sectorKey.Scope);
    }

    [TestMethod]
    public void ScopeValidityFollowsThePoolKind()
    {
        TestAssert.IsTrue(AuthorityScope.IsValidScope(PoolKind.SpaceEnemy, 0));
        TestAssert.IsFalse(AuthorityScope.IsValidScope(PoolKind.SpaceEnemy, 101));
        TestAssert.IsTrue(AuthorityScope.IsValidScope(PoolKind.Hive, 1000001));
        TestAssert.IsFalse(AuthorityScope.IsValidScope(PoolKind.Hive, 101));
        TestAssert.IsTrue(AuthorityScope.IsValidScope(PoolKind.Entity, 101));
        TestAssert.IsFalse(AuthorityScope.IsValidScope(PoolKind.Entity, 0));
        TestAssert.IsFalse(AuthorityScope.IsValidScope(PoolKind.Unknown, 101));
    }

    [TestMethod]
    public void StateForAnObjectThatWasNeverSpawnedIsRejected()
    {
        var replica = new ReplicaVersionState(Epoch);
        var scope = replica.GetOrCreateScope(new ScopeKey(PoolKind.GroundEnemy, 101));
        scope.AdoptSubscription(scope.SubscriptionEpoch + 1, SubscriptionPhase.Live);
        var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);

        // "delta before spawn" must be a rejection, not an implicit create: the object's identity
        // has to be established before its state, or the replica invents objects the host lacks.
        TestAssert.AreEqual(ReplicaApplyResult.RejectedUnknownObject, scope.ApplyState(key, 1));
        TestAssert.AreEqual(ReplicaApplyResult.Applied, scope.Spawn(key, 1));
        TestAssert.AreEqual(ReplicaApplyResult.Applied, scope.ApplyState(key, 2));
    }

    [TestMethod]
    public void OlderRevisionNeverOverwritesNewerState()
    {
        var scope = NewScope(out var key);
        TestAssert.AreEqual(ReplicaApplyResult.Applied, scope.Spawn(key, 5));
        TestAssert.AreEqual(ReplicaApplyResult.Stale, scope.ApplyState(key, 4));
        TestAssert.AreEqual(ReplicaApplyResult.Duplicate, scope.ApplyState(key, 5));
        TestAssert.AreEqual(ReplicaApplyResult.Applied, scope.ApplyState(key, 6));

        TestAssert.IsTrue(scope.TryGetVersion(key, out var version));
        TestAssert.AreEqual(6L, version.Revision);
    }

    [TestMethod]
    public void DeadGenerationIsNeverRevivedByALateAlive()
    {
        var scope = NewScope(out var key);
        TestAssert.AreEqual(ReplicaApplyResult.Applied, scope.Spawn(key, 5));
        TestAssert.AreEqual(ReplicaApplyResult.Applied, scope.MarkDead(key, 6, sequence: 10));
        TestAssert.IsTrue(scope.IsTombstoned(key));
        TestAssert.AreEqual(0, scope.MemberCount);

        // Every later message for this exact key is refused, however new it claims to be.
        TestAssert.AreEqual(ReplicaApplyResult.RejectedTombstone, scope.ApplyState(key, 7));
        TestAssert.AreEqual(ReplicaApplyResult.RejectedTombstone, scope.ApplyState(key, 99));
        TestAssert.AreEqual(ReplicaApplyResult.RejectedTombstone, scope.Spawn(key, 99));
        // A second death for an already dead key is a duplicate rather than a state change, whether
        // it repeats the revision or an older one; death is terminal either way.
        TestAssert.AreEqual(ReplicaApplyResult.Duplicate, scope.MarkDead(key, 6, 10));
        TestAssert.AreEqual(ReplicaApplyResult.Duplicate, scope.MarkDead(key, 5, 11));
        TestAssert.IsTrue(scope.TryGetVersion(key, out var version));
        TestAssert.AreEqual(6L, version.Revision, "The tombstone must keep the revision it was recorded at.");
        TestAssert.AreEqual(10L, version.DeathSequence);
    }

    [TestMethod]
    public void TheNextGenerationIsAFreshObjectAndIsNotBlockedByTheTombstone()
    {
        var scope = NewScope(out var key);
        var next = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 2);

        scope.Spawn(key, 5);
        scope.MarkDead(key, 6, sequence: 10);

        TestAssert.AreEqual(ReplicaApplyResult.Applied, scope.Spawn(next, 1),
            "A recycled slot with a new generation is a different key and must spawn normally.");
        TestAssert.AreEqual(ReplicaApplyResult.Applied, scope.ApplyState(next, 2));
        TestAssert.IsTrue(scope.IsTombstoned(key), "The old generation stays dead.");
        TestAssert.IsFalse(scope.IsTombstoned(next));
    }

    [TestMethod]
    public void ResubscribingDoesNotReviveATombstone()
    {
        var scope = NewScope(out var key);
        scope.Spawn(key, 5);
        scope.MarkDead(key, 6, sequence: 10);

        TestAssert.IsTrue(scope.AdoptSubscription(scope.SubscriptionEpoch + 1, SubscriptionPhase.Snapshotting));
        TestAssert.IsTrue(scope.IsTombstoned(key),
            "Resubscribing must not bring back an old tombstone.");
        TestAssert.AreEqual(ReplicaApplyResult.RejectedTombstone, scope.Spawn(key, 7));
    }

    [TestMethod]
    public void AStaleSubscriptionEpochIsRejected()
    {
        var scope = NewScope(out _);
        var first = scope.SubscriptionEpoch;
        var second = first + 1;
        TestAssert.IsTrue(scope.AdoptSubscription(second, SubscriptionPhase.Snapshotting));

        TestAssert.AreEqual(SequenceResult.Accepted, scope.AcceptSequence(second, 1));
        TestAssert.AreEqual(SequenceResult.WrongSubscriptionEpoch, scope.AcceptSequence(first, 2),
            "A tail packet from the first subscription must not be applied to the second.");
    }

    [TestMethod]
    public void SequenceGapsStopApplicationUntilANewBaselineArrives()
    {
        var scope = NewScope(out _);
        scope.AdoptSubscription(scope.SubscriptionEpoch + 1, SubscriptionPhase.Live);
        var epoch = scope.SubscriptionEpoch;

        TestAssert.AreEqual(SequenceResult.Accepted, scope.AcceptSequence(epoch, 1));
        TestAssert.AreEqual(SequenceResult.Accepted, scope.AcceptSequence(epoch, 2));
        TestAssert.AreEqual(SequenceResult.Duplicate, scope.AcceptSequence(epoch, 2));
        TestAssert.AreEqual(SequenceResult.Duplicate, scope.AcceptSequence(epoch, 1));
        TestAssert.AreEqual(SequenceResult.Gap, scope.AcceptSequence(epoch, 4),
            "Skipping a sequence would silently drop a lifecycle message.");
        TestAssert.AreEqual(SequenceResult.Accepted, scope.AcceptSequence(epoch, 3));
    }

    [TestMethod]
    public void BaselineReclaimsOnlyTheTombstonesItCovers()
    {
        var scope = NewScope(out var first);
        var second = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 8, 1);
        scope.AdoptSubscription(scope.SubscriptionEpoch + 1, SubscriptionPhase.Live);
        scope.Spawn(first, 1);
        scope.Spawn(second, 1);
        scope.MarkDead(first, 2, sequence: 4);
        scope.MarkDead(second, 2, sequence: 9);
        TestAssert.AreEqual(2, scope.TombstoneCount);

        // The host's baseline covers the stream up to sequence 5, so only the first death is
        // provably superseded; the second is still an increment the replica must keep applying.
        scope.InstallBaseline(baselineId: 7, cutoffSequence: 5, members: []);

        TestAssert.IsFalse(scope.IsTombstoned(first), "A death inside the baseline is covered by it.");
        TestAssert.IsTrue(scope.IsTombstoned(second),
            "A death after the cutoff is not covered and must still block the stale spawn.");
        TestAssert.AreEqual(1, scope.TombstoneCount);
    }

    [TestMethod]
    public void BaselineDoesNotRewindAnAlreadyAppliedRevision()
    {
        var scope = NewScope(out var key);
        scope.AdoptSubscription(scope.SubscriptionEpoch + 1, SubscriptionPhase.Live);
        scope.Spawn(key, 1);
        scope.ApplyState(key, 12);

        scope.InstallBaseline(baselineId: 3, cutoffSequence: 0, members: [key]);

        TestAssert.IsTrue(scope.TryGetVersion(key, out var version));
        TestAssert.AreEqual(12L, version.Revision,
            "Installing a baseline must not move a revision backwards, or the next delta would be stale.");
        TestAssert.AreEqual(1, scope.MemberCount);
    }

    [TestMethod]
    public void BaselineRejectsAKeyFromAnotherScopeOrEpoch()
    {
        var scope = NewScope(out _);
        scope.AdoptSubscription(scope.SubscriptionEpoch + 1, SubscriptionPhase.Snapshotting);
        var otherPlanet = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 102, 1, 1);
        TestAssert.ThrowsExactly<ArgumentException>(() =>
            scope.InstallBaseline(1, 0, [otherPlanet]));
    }

    [TestMethod]
    public void IncrementAgainstAnUnknownBaselineIsRejectedEvenWhenItsRevisionIsNewer()
    {
        var scope = NewScope(out var key);
        scope.AdoptSubscription(scope.SubscriptionEpoch + 1, SubscriptionPhase.Live);
        scope.Spawn(key, 1);

        TestAssert.AreEqual(ReplicaApplyResult.RejectedBaseline, scope.ApplyState(key, 50, declaredBaselineId: 4));
        scope.InstallBaseline(baselineId: 4, cutoffSequence: 0, members: [key]);
        TestAssert.AreEqual(ReplicaApplyResult.Applied, scope.ApplyState(key, 50, declaredBaselineId: 4));
    }

    [TestMethod]
    public void HostRevisionCounterAdvancesOncePerChangeAndIsPerGeneration()
    {
        var tracker = new ObjectRevisionTracker();
        var first = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        var second = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 2);

        TestAssert.AreEqual(1L, tracker.Next(first));
        TestAssert.AreEqual(2L, tracker.Next(first));
        TestAssert.AreEqual(1L, tracker.Next(second),
            "A new generation starts its own revision series; the recycled slot's history does not carry over.");
        TestAssert.AreEqual(3L, tracker.Next(first));
    }

    [TestMethod]
    public void TheAuthorityModelDoesNotReferenceUnityOrGameTypes()
    {
        // the model must be usable in a plain .NET test process. If any
        // member of the namespace mentions a Unity or game type, that guarantee is gone, and the
        // pure tests would start needing a game process to run.
        var assembly = typeof(ObjectKey).Assembly;
        var authorityTypes = assembly.GetTypes()
            .Where(type => type.Namespace != null &&
                           type.Namespace.StartsWith("NebulaeModel.Authority", StringComparison.Ordinal))
            .ToArray();
        TestAssert.IsGreaterThanOrEqualTo(4, authorityTypes.Length,
            "Expected the authority model types to be present; found " + authorityTypes.Length);

        var offenders = new List<string>();
        foreach (var type in authorityTypes)
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Instance | BindingFlags.Static |
                                                   BindingFlags.DeclaredOnly))
            {
                foreach (var referenced in ReferencedTypes(member))
                {
                    if (referenced.Assembly == assembly &&
                        (referenced.Namespace ?? string.Empty).StartsWith("NebulaeModel.Authority", StringComparison.Ordinal))
                        continue;
                    if (IsForbidden(referenced))
                    {
                        offenders.Add(type.Name + "." + member.Name + " -> " + (referenced.FullName ?? referenced.Name));
                    }
                }
            }
        }

        TestAssert.IsEmpty(offenders,
            "The authority model must not reference Unity or game types:\n" + string.Join("\n", offenders));
    }

    [TestMethod]
    public void RandomEventSequenceNeverRevivesADeadKeyAndNeverLosesIdentity()
    {
        // a random event sequence must show that a key does not come back
        // after death. The model is the real one: a host recycles pool slots, so a slot's live key
        // changes generation over time, and packets for the previous generation keep arriving late.
        // The seed is fixed so a failure is reproducible.
        var random = new Random(20260930);
        var replica = new ReplicaVersionState(Epoch);
        var scope = replica.GetOrCreateScope(new ScopeKey(PoolKind.GroundEnemy, 101));
        scope.AdoptSubscription(scope.SubscriptionEpoch + 1, SubscriptionPhase.Live);

        const int slotCount = 4;
        var generation = new long[slotCount + 1];
        for (var slot = 1; slot <= slotCount; slot++) generation[slot] = 1;
        var revision = new Dictionary<ObjectKey, long>();
        var dead = new HashSet<ObjectKey>();
        // Independent bookkeeping of which keys the replica should consider live. The model's own
        // MemberCount is checked against it at the end, so a bookkeeping bug in either side shows up.
        var live = new HashSet<ObjectKey>();
        var sequence = 0L;
        var appliedStates = 0;
        var rejectedRevivals = 0;

        for (var step = 0; step < 6000; step++)
        {
            var slot = random.Next(1, slotCount + 1);
            // Occasionally address a stale generation of this slot, which is what a delayed packet
            // does after the host recycled the slot.
            var targetGeneration = generation[slot];
            if (targetGeneration > 1 && random.Next(4) == 0) targetGeneration--;
            var key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, slot, targetGeneration);
            revision.TryGetValue(key, out var current);
            var isDead = dead.Contains(key);
            var isLiveGeneration = targetGeneration == generation[slot];
            var action = random.Next(10);

            if (action <= 5)
            {
                // A state update. This is the most common event in a live world.
                var next = current + random.Next(1, 4);
                var result = scope.ApplyState(key, next);
                if (isDead)
                {
                    TestAssert.AreEqual(ReplicaApplyResult.RejectedTombstone, result,
                        "step " + step + ": dead key " + key + " accepted state as " + result);
                    rejectedRevivals++;
                    continue;
                }
                if (current == 0)
                {
                    TestAssert.AreEqual(ReplicaApplyResult.RejectedUnknownObject, result,
                        "step " + step + ": state was applied before identity was established");
                    continue;
                }
                if (result == ReplicaApplyResult.Applied)
                {
                    TestAssert.IsGreaterThan(current, next);
                    revision[key] = next;
                    appliedStates++;
                }
                continue;
            }

            if (action <= 7)
            {
                // A spawn: the host creates the object in the slot's current generation.
                if (!isLiveGeneration) continue;
                var result = scope.Spawn(key, current + random.Next(1, 3));
                if (isDead)
                {
                    TestAssert.AreEqual(ReplicaApplyResult.RejectedTombstone, result,
                        "step " + step + ": dead key " + key + " respawned as " + result);
                    rejectedRevivals++;
                    continue;
                }
                if (result == ReplicaApplyResult.Applied)
                {
                    revision[key] = scope.TryGetVersion(key, out var version) ? version.Revision : 1;
                    live.Add(key);
                }
                continue;
            }

            if (action == 8)
            {
                // The host kills the object and recycles the slot to the next generation.
                if (isDead || current == 0) continue;
                sequence++;
                TestAssert.AreEqual(ReplicaApplyResult.Applied, scope.MarkDead(key, current, sequence));
                dead.Add(key);
                live.Remove(key);
                TestAssert.IsTrue(scope.IsTombstoned(key));
                if (isLiveGeneration) generation[slot]++;
                continue;
            }

            // A death replayed from an older generation, or one whose revision is behind what the
            // replica already applied. Neither may erase newer state.
            if (current <= 1) continue;
            sequence++;
            if (isDead)
            {
                TestAssert.AreEqual(ReplicaApplyResult.Duplicate, scope.MarkDead(key, current, sequence));
                continue;
            }
            TestAssert.AreEqual(ReplicaApplyResult.Stale,
                scope.MarkDead(key, current - 1, sequence),
                "step " + step + ": a death older than the applied revision was accepted");
        }

        TestAssert.IsGreaterThan(100, appliedStates, "The fuzz run did not exercise enough state updates.");
        TestAssert.IsGreaterThan(50, rejectedRevivals,
            "The fuzz run barely touched tombstoned keys, so it proves little about revivals.");
        TestAssert.IsGreaterThan(20, dead.Count, "The fuzz run did not recycle enough slots.");
        // Every key that ever established identity is either a live member or a tombstone, and no
        // key may be both.
        TestAssert.AreEqual(dead.Count, scope.TombstoneCount);
        TestAssert.AreEqual(live.Count, scope.MemberCount);
    }

    private static ScopeVersionState NewScope(out ObjectKey key)
    {
        var replica = new ReplicaVersionState(Epoch);
        var scope = replica.GetOrCreateScope(new ScopeKey(PoolKind.GroundEnemy, 101));
        key = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        return scope;
    }

    private static IEnumerable<Type> ReferencedTypes(MemberInfo member)
    {
        switch (member)
        {
            case MethodInfo method:
                if (method.ReturnType != null) yield return method.ReturnType;
                foreach (var parameter in method.GetParameters()) yield return parameter.ParameterType;
                break;
            case PropertyInfo property:
                yield return property.PropertyType;
                break;
            case FieldInfo field:
                yield return field.FieldType;
                break;
            case ConstructorInfo constructor:
                foreach (var parameter in constructor.GetParameters()) yield return parameter.ParameterType;
                break;
        }
    }

    private static bool IsForbidden(Type type)
    {
        // A generic or array wrapper hides the real dependency behind its own name, so the
        // arguments and the element type have to be inspected as well.
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
                if (IsForbidden(argument)) return true;
        }
        if (type.IsArray) return IsForbidden(type.GetElementType());

        var fullName = type.FullName ?? type.Name;
        return fullName.StartsWith("UnityEngine", StringComparison.Ordinal) ||
               fullName.StartsWith("Unity", StringComparison.Ordinal) ||
               fullName.StartsWith("GameMain", StringComparison.Ordinal) ||
               fullName.StartsWith("GameLogic", StringComparison.Ordinal) ||
               fullName.StartsWith("PlanetFactory", StringComparison.Ordinal) ||
               fullName.StartsWith("NebulaeWorld", StringComparison.Ordinal) ||
               fullName.StartsWith("NebulaeNetwork", StringComparison.Ordinal);
    }
}
