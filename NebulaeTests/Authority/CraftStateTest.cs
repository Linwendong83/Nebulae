
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// the canonical state codec and the client binding that
/// rebuilds display-only shells from host facts. Everything here runs without a game process.
/// </summary>
[TestClass]
public class CraftStateTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0A13080808080808, 0x0C13080808080808);
    private const int Planet = 101;

    private static ObjectKey GroundKey(int nativeId, long generation) =>
        ObjectKey.Create(Epoch, PoolKind.GroundCraft, Planet, nativeId, generation);

    private static ObjectKey SpaceKey(int nativeId, long generation) =>
        ObjectKey.Create(Epoch, PoolKind.SpaceCraft, AuthorityScope.Sector, nativeId, generation);

    private static CraftState GroundFighter(int owner, int hp = 500) => new(
        hasCombatStat: true, isDynamic: true, isSpace: false,
        protoId: 5101, modelIndex: 60, port: 1, prototype: 7, stateFlags: 0,
        astroId: Planet, owner: owner,
        posX: 10.0, posY: 0.0, posZ: 20.0, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 5f, velY: 0f, velZ: 0f, hp: hp, hpMax: 900, hpRecover: 0, hpIncoming: 0);

    private static CraftState SpaceFighter(int owner, int hp = 700) => new(
        hasCombatStat: true, isDynamic: true, isSpace: true,
        protoId: 5201, modelIndex: 61, port: 2, prototype: 7, stateFlags: 0,
        astroId: 1000101, owner: owner,
        posX: 4900.0, posY: -350.0, posZ: 1200.0, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 10f, velY: 0f, velZ: 0f, hp: hp, hpMax: 1100, hpRecover: 0, hpIncoming: 0);

    private sealed class FakePools : ICraftPools
    {
        public readonly Dictionary<int, CraftState> Shells = [];
        public readonly List<int> Removed = [];

        public bool CraftExists(int craftId) => Shells.ContainsKey(craftId);
        public void CreateCraftShell(int craftId, in CraftState state) => Shells[craftId] = state;
        public void WriteCraftState(int craftId, in CraftState state) => Shells[craftId] = state;
        public void RemoveCraftShell(int craftId)
        {
            TestAssert.IsTrue(Shells.Remove(craftId), "A removed shell must have existed.");
            Removed.Add(craftId);
        }
    }

    [TestMethod]
    public void AStateRoundTripsEveryField()
    {
        var state = GroundFighter(owner: 5, hp: 640);
        TestAssert.IsTrue(CraftStateCodec.TryEncode(state, out var data));
        TestAssert.IsTrue(CraftStateCodec.TryDecode(data, 0, data.Length, out var decoded, out var reject), reject.ToString());
        TestAssert.IsTrue(decoded.HasCombatStat);
        TestAssert.IsTrue(decoded.IsDynamic);
        TestAssert.IsFalse(decoded.IsSpace);
        TestAssert.AreEqual((short)5101, decoded.ProtoId);
        TestAssert.AreEqual(5, decoded.Owner);
        TestAssert.AreEqual(10.0, decoded.PosX, 1e-9);
        TestAssert.AreEqual(1f, decoded.RotW, 1e-6f);
        TestAssert.AreEqual(640, decoded.Hp);
        TestAssert.AreEqual(900, decoded.HpMax);
    }

    [TestMethod]
    public void TheEncodedLayoutIsBoundedAndDeterministic()
    {
        TestAssert.IsTrue(CraftStateCodec.TryEncode(GroundFighter(1), out var data));
        TestAssert.HasCount(CraftStateCodec.SizeFor(GroundFighter(1)), data);
        TestAssert.IsLessThanOrEqualTo(AuthorityLimits.StateRecordMaxBytes, data.Length);
        TestAssert.IsTrue(CraftStateCodec.TryEncode(GroundFighter(1), out var again));
        CollectionAssert.AreEqual(data, again, "Encoding is deterministic: the dirt comparison depends on it.");
    }

    [TestMethod]
    public void ACorruptBlobIsRefused()
    {
        TestAssert.IsTrue(CraftStateCodec.TryEncode(GroundFighter(1), out var data));
        var badFlags = (byte[])data.Clone();
        badFlags[0] |= 0x80;
        TestAssert.IsFalse(CraftStateCodec.TryDecode(badFlags, 0, badFlags.Length, out _, out var flagReject));
        TestAssert.AreEqual(AuthorityRejectCode.MalformedEnvelope, flagReject.Code);
        var truncated = new byte[data.Length - 4];
        System.Buffer.BlockCopy(data, 0, truncated, 0, truncated.Length);
        TestAssert.IsFalse(CraftStateCodec.TryDecode(truncated, 0, truncated.Length, out _, out _));
        var padded = new byte[data.Length + 1];
        System.Buffer.BlockCopy(data, 0, padded, 0, data.Length);
        TestAssert.IsFalse(CraftStateCodec.TryDecode(padded, 0, padded.Length, out _, out _));
    }

    [TestMethod]
    public void AHostCraftIsCreatedAsADisplayShell()
    {
        var pools = new FakePools();
        var binding = new CraftBinding(PoolKind.GroundCraft, Planet, pools);
        TestAssert.IsTrue(binding.ApplyState(GroundKey(7, 1), Encoded(GroundFighter(owner: 5))));
        TestAssert.IsTrue(pools.CraftExists(7));
        TestAssert.AreEqual(5, pools.Shells[7].Owner);
        TestAssert.AreEqual(1, binding.BoundCount);
    }

    [TestMethod]
    public void TwoOwnersNeverShareASlot()
    {
        var pools = new FakePools();
        var binding = new CraftBinding(PoolKind.GroundCraft, Planet, pools);
        TestAssert.IsTrue(binding.ApplyState(GroundKey(7, 1), Encoded(GroundFighter(owner: 5, hp: 500))));
        TestAssert.IsTrue(binding.ApplyState(GroundKey(8, 1), Encoded(GroundFighter(owner: 9, hp: 600))));
        TestAssert.AreEqual(5, pools.Shells[7].Owner);
        TestAssert.AreEqual(9, pools.Shells[8].Owner);
        TestAssert.IsTrue(binding.ApplyState(GroundKey(7, 1), Encoded(GroundFighter(owner: 5, hp: 300))));
        TestAssert.AreEqual(300, pools.Shells[7].Hp);
        TestAssert.AreEqual(600, pools.Shells[8].Hp, "One owner's update never touches the other's shell.");
        TestAssert.AreEqual(2, binding.BoundCount);
    }

    [TestMethod]
    public void ASpaceRecordIsRefusedByAGroundBindingAndViceVersa()
    {
        var groundPools = new FakePools();
        var ground = new CraftBinding(PoolKind.GroundCraft, Planet, groundPools);
        TestAssert.IsFalse(ground.ApplyState(GroundKey(7, 1), Encoded(SpaceFighter(owner: 5))));
        TestAssert.AreEqual(1L, ground.StatesRefused);

        var spacePools = new FakePools();
        var space = new CraftBinding(PoolKind.SpaceCraft, AuthorityScope.Sector, spacePools);
        TestAssert.IsFalse(space.ApplyState(SpaceKey(7, 1), Encoded(GroundFighter(owner: 5))));
        TestAssert.AreEqual(1L, space.StatesRefused);
    }

    [TestMethod]
    public void ARebuiltSlotIsANewKeyThatNeverInheritsTheOldShell()
    {
        var pools = new FakePools();
        var binding = new CraftBinding(PoolKind.SpaceCraft, AuthorityScope.Sector, pools);
        var first = SpaceKey(7, 1);
        TestAssert.IsTrue(binding.ApplyState(first, Encoded(SpaceFighter(owner: 5, hp: 700))));
        binding.RemoveMember(first);
        TestAssert.IsFalse(pools.CraftExists(7));
        var second = SpaceKey(7, 2);
        TestAssert.IsTrue(binding.ApplyState(second, Encoded(SpaceFighter(owner: 9, hp: 880))));
        TestAssert.AreEqual(9, pools.Shells[7].Owner);
        TestAssert.AreEqual(880, pools.Shells[7].Hp);
    }

    [TestMethod]
    public void ABaselineReconcilesTheWholeScopeAtomically()
    {
        var pools = new FakePools();
        var binding = new CraftBinding(PoolKind.GroundCraft, Planet, pools);
        TestAssert.IsTrue(binding.ApplyState(GroundKey(7, 1), Encoded(GroundFighter(owner: 5))));
        TestAssert.IsTrue(binding.ApplyState(GroundKey(8, 1), Encoded(GroundFighter(owner: 9))));
        var members = new List<SnapshotMemberRecord>
        {
            new(GroundKey(7, 1), 7, Encoded(GroundFighter(owner: 5, hp: 100))),
            new(GroundKey(8, 1), 6, Encoded(GroundFighter(owner: 9, hp: 200)))
        };
        binding.ReconcileBaseline(members);
        TestAssert.AreEqual(100, pools.Shells[7].Hp);
        TestAssert.AreEqual(200, pools.Shells[8].Hp);
        binding.ReconcileBaseline(new List<SnapshotMemberRecord>());
        TestAssert.AreEqual(0, binding.BoundCount);
        TestAssert.AreEqual(2L, binding.MembersRemoved);
    }

    [TestMethod]
    public void AKeyOutsideTheBindingScopeIsRefused()
    {
        var pools = new FakePools();
        var binding = new CraftBinding(PoolKind.GroundCraft, Planet, pools);
        TestAssert.IsFalse(binding.ApplyState(GroundKey(1, 1), null));
        TestAssert.AreEqual(1L, binding.StatesRefused);
        var otherPlanet = ObjectKey.Create(Epoch, PoolKind.GroundCraft, 102, 1, 1);
        TestAssert.IsFalse(binding.ApplyState(otherPlanet, Encoded(GroundFighter(owner: 1))));
        TestAssert.AreEqual(2L, binding.StatesRefused);
        var wrongKind = ObjectKey.Create(Epoch, PoolKind.SpaceCraft, AuthorityScope.Sector, 1, 1);
        TestAssert.IsFalse(binding.ApplyState(wrongKind, Encoded(GroundFighter(owner: 1))));
        TestAssert.AreEqual(3L, binding.StatesRefused);
    }
}
