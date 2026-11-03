using global::NebulaeModel.DataStructures;
using NebulaeModel.Authority;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Session;
using NebulaeWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>Validates the real network serializer and session identity lifecycle.</summary>
[TestClass]
internal class AuthorityEnvelopeSerializationTest
{
    private static readonly AuthorityEpoch Epoch = new(0xDEADBEEFCAFEBABE, 0x0123456789ABCDEF);

    [TestMethod]
    public void ScopeControlCarriesItsObservedStarInTheSingleWireContract()
    {
        var sent = AuthorityScopeControlPacket.Create(new AuthorityEnvelopeHeader(AuthorityFamily.ScopeControl,
            Epoch, new ConnectionEpoch(1), 1, 0, 2, 0), ScopeControlOp.Subscribe,
            new ScopeKey(PoolKind.SpaceEnemy, 0), false, ScopeRecoveryReason.None, 17, 18);
        sent.ObservedStarId = 64;
        sent.RequestId = 123;
        RoundTrip(sent, out var received);
        TestAssert.AreEqual(64, received.ObservedStarId);
        TestAssert.AreEqual(123L, received.RequestId);
        TestAssert.AreEqual(17L, received.SubscriptionEpoch);
        TestAssert.AreEqual(18L, received.LastAppliedSequence);
    }

    private static void RoundTrip<T>(T sent, out T received) where T : class, new() =>
        received = TestSupport.RoundTrip(sent, processor => processor.RegisterNestedType<PlayerData>(() => new PlayerData()));

    [TestMethod]
    public void ACommandEnvelopeRoundTripsEveryHeaderFieldAndItsPayload()
    {
        var target = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 4);
        var header = new AuthorityEnvelopeHeader(AuthorityFamily.Command, Epoch,
            new ConnectionEpoch(9), sequence: 42, hostTick: 1234, claimedPlayerId: 3, payloadLength: 5);
        var sent = AuthorityCommandPacket.Create(header, target, category: 2, payload: [1, 2, 3, 4, 5]);

        RoundTrip(sent, out var received);
        TestAssert.IsNotNull(received);
        TestAssert.AreEqual(Epoch.High, received.EpochHigh);
        TestAssert.AreEqual(Epoch.Low, received.EpochLow);
        TestAssert.AreEqual(9UL, received.ConnectionEpoch);
        TestAssert.AreEqual(42L, received.Sequence);
        TestAssert.AreEqual(1234L, received.HostTick);
        TestAssert.AreEqual((ushort)3, received.ClaimedPlayerId);
        TestAssert.AreEqual((byte)PoolKind.GroundEnemy, received.TargetKind);
        TestAssert.AreEqual(101, received.TargetScope);
        TestAssert.AreEqual(7, received.TargetNativeId);
        TestAssert.AreEqual(4L, received.TargetGeneration);
        TestAssert.AreEqual((byte)2, received.Category);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, received.Payload);
        TestAssert.AreEqual(AuthorityFamily.Command, received.Family);
        TestAssert.AreEqual(5, received.DeclaredPayloadLength);
    }

    [TestMethod]
    public void ACommandTargetReassemblesIntoTheOriginalKey()
    {
        var target = ObjectKey.Create(Epoch, PoolKind.SpaceCraft, 1000007, 3, 8);
        var header = new AuthorityEnvelopeHeader(AuthorityFamily.Command, Epoch,
            new ConnectionEpoch(1), 1, 1, 1, 0);
        var sent = AuthorityCommandPacket.Create(header, target, 0, []);
        RoundTrip(sent, out var received);
        TestAssert.IsTrue(received.TryGetTargetKey(out var key));
        TestAssert.AreEqual(target, key);
    }

    [TestMethod]
    public void ASnapshotBeginRoundTripsItsScopeAndBaseline()
    {
        var scope = new ScopeKey(PoolKind.GroundEnemy, 204899);
        var header = new AuthorityEnvelopeHeader(AuthorityFamily.SnapshotBegin, Epoch,
            default, 3, 77, 0, 0);
        var sent = AuthoritySnapshotBeginPacket.Create(header, scope, baselineId: 12, subscriptionEpoch: 2,
            cutoffSequence: 500, totalBytes: 4096, chunkCount: 4, snapshotHash: 0xABCDEF);
        sent.RequestId = 123;

        RoundTrip(sent, out var received);
        TestAssert.IsTrue(received.TryGetScopeKey(out var readScope));
        TestAssert.AreEqual(scope, readScope);
        TestAssert.AreEqual(12L, received.BaselineId);
        TestAssert.AreEqual(2L, received.SubscriptionEpoch);
        TestAssert.AreEqual(500L, received.CutoffSequence);
        TestAssert.AreEqual(4096L, received.TotalBytes);
        TestAssert.AreEqual(4, received.ChunkCount);
        TestAssert.AreEqual(0xABCDEFUL, received.SnapshotHash);
        TestAssert.AreEqual(123L, received.RequestId);
    }

    [TestMethod]
    public void ASnapshotChunkRoundTripsItsBytesAndDeclaresItsLength()
    {
        var header = new AuthorityEnvelopeHeader(AuthorityFamily.SnapshotChunk, Epoch,
            default, 4, 78, 0, 0);
        var data = new byte[1024];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)i;
        var sent = AuthoritySnapshotChunkPacket.Create(header, new ScopeKey(PoolKind.GroundEnemy, 101), baselineId: 12, subscriptionEpoch: 1, chunkIndex: 3, data);

        RoundTrip(sent, out var received);
        TestAssert.AreEqual(12L, received.BaselineId);
        TestAssert.AreEqual(3, received.ChunkIndex);
        CollectionAssert.AreEqual(data, received.Data);
        TestAssert.AreEqual(1024, received.DeclaredPayloadLength);
    }

    [TestMethod]
    public void AWorldStateEnvelopeRoundTripsItsBlobAndScope()
    {
        var scope = new ScopeKey(PoolKind.GroundEnemy, 101);
        var header = new AuthorityEnvelopeHeader(AuthorityFamily.WorldState, Epoch,
            default, 5, 79, 0, 0);
        var sent = AuthorityWorldStatePacket.Create(header, scope, declaredBaselineId: 12, recordCount: 1,
            data: [9, 8, 7]);

        RoundTrip(sent, out var received);
        TestAssert.IsTrue(received.TryGetScopeKey(out var readScope));
        TestAssert.AreEqual(scope, readScope);
        TestAssert.AreEqual(12L, received.DeclaredBaselineId);
        TestAssert.AreEqual(1, received.RecordCount);
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, received.Data);
        TestAssert.IsTrue(received.AreRecordsConsistent());
    }

    [TestMethod]
    public void ACommandResultAndAckRoundTrip()
    {
        var header = new AuthorityEnvelopeHeader(AuthorityFamily.CommandResult, Epoch,
            new ConnectionEpoch(2), 6, 80, 0, 0);
        var outcome = new CommandOutcome(CommandResultCode.Applied, appliedHostTick: 81, transactionId: 900,
            resourceRevision: 44);
        RoundTrip(AuthorityCommandResultPacket.Create(header, outcome), out var result);
        TestAssert.AreEqual((byte)CommandResultCode.Applied, result.ResultCode);
        TestAssert.AreEqual(81L, result.AppliedHostTick);
        TestAssert.AreEqual(900L, result.TransactionId);
        TestAssert.AreEqual(44L, result.ResourceRevision);

        var ackHeader = new AuthorityEnvelopeHeader(AuthorityFamily.SnapshotAck, Epoch,
            new ConnectionEpoch(2), 7, 82, 0, 0);
        RoundTrip(AuthoritySnapshotAckPacket.Create(ackHeader, baselineId: 12, lastAppliedSequence: 99,
            accepted: true), out var ack);
        TestAssert.AreEqual(12L, ack.BaselineId);
        TestAssert.AreEqual(99L, ack.LastAppliedSequence);
        TestAssert.IsTrue(ack.Accepted);
    }

    [TestMethod]
    public void AWelcomeRoundTripsItsWorldIdentityAndClock()
    {
        var header = new AuthorityEnvelopeHeader(AuthorityFamily.Welcome,
            Epoch, new ConnectionEpoch(17), sequence: 1, hostTick: 731, claimedPlayerId: 7, payloadLength: 0);
        var sent = AuthorityWelcomePacket.Create(header);
        RoundTrip(sent, out var received);
        TestAssert.AreEqual(Epoch.High, received.EpochHigh);
        TestAssert.AreEqual(Epoch.Low, received.EpochLow);
        TestAssert.AreEqual(17UL, received.ConnectionEpoch);
        TestAssert.AreEqual(1L, received.Sequence);
        TestAssert.AreEqual(731L, received.HostTick);
        TestAssert.AreEqual(731L, received.WelcomeHostTick);
        TestAssert.AreEqual((ushort)7, received.ClaimedPlayerId);
        var processor = new NebulaeNetPacketProcessor();
        TestAssert.HasCount(66, processor.Write(sent),
            "The welcome wire contract includes only identity and tick fields.");
    }

    [TestMethod]
    public void AHandshakeResponseRoundTripsGameAndPlayerFacts()
    {
        var sent = new HandshakeResponse
        {
            GalaxyAlgo = 20221001,
            GoalLevel = 3,
            GalaxySeed = 726,
            StarCount = 64,
            ResourceMultiplier = 1.5f,
            IsPeaceMode = false,
            IsSandboxMode = true,
            SavedThemeIds = new[] { 1, 3 },
            CombatSettingsData = new byte[] { 9, 8 },
            IsNewPlayer = true,
            LocalPlayerData = new PlayerData(7, 102, "p7"),
            NumPlayers = 4
        };
        RoundTrip(sent, out var received);
        TestAssert.AreEqual(sent.GalaxyAlgo, received.GalaxyAlgo);
        TestAssert.AreEqual(sent.GoalLevel, received.GoalLevel);
        TestAssert.AreEqual(sent.GalaxySeed, received.GalaxySeed);
        TestAssert.AreEqual(sent.StarCount, received.StarCount);
        TestAssert.AreEqual(sent.ResourceMultiplier, received.ResourceMultiplier);
        TestAssert.IsFalse(received.IsPeaceMode);
        TestAssert.IsTrue(received.IsSandboxMode);
        TestAssert.IsTrue(received.IsNewPlayer);
        TestAssert.AreEqual((ushort)7, received.LocalPlayerData.PlayerId);
        TestAssert.AreEqual(102, received.LocalPlayerData.LocalPlanetId);
        TestAssert.AreEqual("p7", received.LocalPlayerData.Username);
        TestAssert.AreEqual((ushort)4, received.NumPlayers);
        CollectionAssert.AreEqual(sent.SavedThemeIds, received.SavedThemeIds);
        CollectionAssert.AreEqual(sent.CombatSettingsData, received.CombatSettingsData);
    }

    [TestMethod]
    public void TheSessionRequiresHandshakeCompletionAndWorldIdentity()
    {
        var session = new AuthoritySessionState();
        TestAssert.IsFalse(session.IsHandshakeComplete);
        TestAssert.IsFalse(session.IsActive);
        TestAssert.IsFalse(session.Context.IsHandshakeComplete);
        TestAssert.IsFalse(session.TryAdoptWorldEpoch(Epoch));

        session.ConfirmHandshake();
        TestAssert.IsTrue(session.IsHandshakeComplete);
        TestAssert.IsFalse(session.IsActive);
        TestAssert.IsTrue(session.Context.IsHandshakeComplete);

        session.BeginAuthorityWorld(Epoch, isHost: true);
        session.SetConnection(new ConnectionEpoch(4), 2);
        TestAssert.IsTrue(session.IsActive);
        TestAssert.AreEqual(Epoch, session.Context.Epoch);
        TestAssert.AreEqual((ushort)2, session.Context.ConnectedPlayerId);

        session.Reset();
        TestAssert.IsFalse(session.IsHandshakeComplete);
        TestAssert.IsFalse(session.IsActive);
        TestAssert.IsFalse(session.TryAdoptWorldEpoch(Epoch));
    }

    [TestMethod]
    public void AnInvalidWorldEpochIsRefused()
    {
        var session = new AuthoritySessionState();
        session.BeginAuthorityWorld(default, isHost: true);
        TestAssert.IsFalse(session.IsActive, "A zero epoch is not a world identity.");
    }

    [TestMethod]
    public void ResettingClearsHandshakeAndConnectionIdentity()
    {
        var session = new AuthoritySessionState();
        session.ConfirmHandshake();
        session.BeginAuthorityWorld(Epoch, isHost: false);
        session.SetConnection(new ConnectionEpoch(4), 2);

        session.Reset();
        TestAssert.IsFalse(session.IsHandshakeComplete);
        TestAssert.IsFalse(session.Context.Epoch.IsValid);
        TestAssert.IsFalse(session.Context.Connection.IsValid);
        TestAssert.AreEqual((ushort)0, session.Context.ConnectedPlayerId);
        TestAssert.IsFalse(session.TryAdoptWorldEpoch(Epoch));
        session.ConfirmHandshake();
        var nextWorld = new AuthorityEpoch(71, 72);
        TestAssert.IsTrue(session.TryAdoptWorldEpoch(nextWorld));
        TestAssert.AreEqual(nextWorld, session.Context.Epoch);
    }

    [TestMethod]
    public void ConfirmingAJoiningPeerDoesNotReplaceTheHostsWorldIdentity()
    {
        var session = new AuthoritySessionState();
        session.BeginAuthorityWorld(Epoch, isHost: true);
        session.SetConnection(new ConnectionEpoch(4), 2);
        TestAssert.IsTrue(session.IsHandshakeComplete);
        TestAssert.IsTrue(session.IsActive);
        session.ConfirmHandshake();
        TestAssert.AreEqual(Epoch, session.Context.Epoch);
        TestAssert.AreEqual(new ConnectionEpoch(4), session.Context.Connection);
        TestAssert.AreEqual((ushort)2, session.Context.ConnectedPlayerId);
        TestAssert.IsTrue(session.IsHost);
    }

    [TestMethod]
    public void AHostChecksTheClaimedIdAgainstTheConnectionItArrivedOn()
    {
        var session = new AuthoritySessionState();
        session.ConfirmHandshake();
        session.BeginAuthorityWorld(Epoch, isHost: true);
        session.SetConnection(new ConnectionEpoch(4), 1);

        var contextForPlayer2 = session.ContextForConnection(2);
        TestAssert.AreEqual((ushort)2, contextForPlayer2.ConnectedPlayerId);
        TestAssert.IsTrue(contextForPlayer2.IsHost);

        var header = new AuthorityEnvelopeHeader(AuthorityFamily.Command, Epoch,
            new ConnectionEpoch(4), 1, 1, claimedPlayerId: 2, payloadLength: 0);
        TestAssert.AreEqual(AuthorityRejectCode.None,
            AuthorityEnvelopeGate.Validate(contextForPlayer2, AuthorityDirection.ClientToServer, header).Code,
            "A command claiming the connection's own id is accepted.");

        var headerForPlayer3 = new AuthorityEnvelopeHeader(AuthorityFamily.Command, Epoch,
            new ConnectionEpoch(4), 1, 1, claimedPlayerId: 3, payloadLength: 0);
        TestAssert.AreEqual(AuthorityRejectCode.ForgedPlayer,
            AuthorityEnvelopeGate.Validate(contextForPlayer2, AuthorityDirection.ClientToServer, headerForPlayer3)
                .Code,
            "The same packet claiming another seat must be refused.");

        // A client has only its own identity, so the per-connection form is the session form there.
        var client = new AuthoritySessionState();
        client.ConfirmHandshake();
        client.BeginAuthorityWorld(Epoch, isHost: false);
        client.SetConnection(new ConnectionEpoch(4), 7);
        TestAssert.AreEqual((ushort)7, client.ContextForConnection(2).ConnectedPlayerId);
    }

    [TestMethod]
    public void TheHandshakeAndWelcomeExposeOnlyModVersionAndWorldFacts()
    {
        TestAssert.IsNull(typeof(HandshakeResponse).GetProperty("AuthorityMode"));
        TestAssert.IsNull(typeof(AuthorityWelcomePacket).GetProperty("Mode"));
        TestAssert.IsNull(typeof(AuthoritySessionState).GetProperty("Mode"));
    }

    [TestMethod]
    public void EveryFamilyDeclaresADirectionAndAPayloadCeiling()
    {
        // A family with no direction would be undecidable at the gate; the enumerations must cover
        // the same set so no family can be added to one and forgotten in the other.
        foreach (AuthorityFamily family in System.Enum.GetValues(typeof(AuthorityFamily)))
        {
            if (family == AuthorityFamily.None) continue;
            TestAssert.IsTrue(AuthorityFamilyDirection.TryGetDirection(family, out _),
                "Family " + family + " has no direction.");
        }
        TestAssert.IsFalse(AuthorityFamilyDirection.TryGetDirection(AuthorityFamily.None, out _));

        TestAssert.AreEqual(AuthorityLimits.CommandPayloadMaxBytes,
            AuthorityLimits.PayloadMaxBytes(AuthorityFamily.Command));
        TestAssert.AreEqual(AuthorityLimits.ChunkMaxBytes,
            AuthorityLimits.PayloadMaxBytes(AuthorityFamily.SnapshotChunk));
    }


    [TestMethod]
    public void SnapshotAndEnvelopeShareTheSameSubscriptionIdentity()
    {
        var snapshot = new AuthoritySnapshotBeginPacket { SubscriptionEpoch = 7 };
        AuthorityEnvelopePacket envelope = snapshot;
        TestAssert.AreEqual(7L, envelope.SubscriptionEpoch);
        envelope.SubscriptionEpoch = 8;
        TestAssert.AreEqual(8L, snapshot.SubscriptionEpoch);
        TestAssert.AreEqual(8L, snapshot.CreateOwnedCopy().SubscriptionEpoch);
    }

    [TestMethod]
    public void PrimitiveEncodingRetainsTheExistingWireBytes()
    {
        var writer = new AuthorityPayloadWriter();
        writer.WriteUShort(0xABCD);
        writer.WriteInt(int.MinValue);
        writer.WriteUInt(uint.MaxValue);
        writer.WriteLong(long.MinValue);
        writer.WriteLong(-1);
        CollectionAssert.AreEqual(new byte[]
        {
            0xCD, 0xAB, 0, 0, 0, 0x80, 0xFF, 0xFF, 0xFF, 0xFF,
            0, 0, 0, 0, 0, 0, 0, 0x80,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
        }, writer.ToArray());
    }
}
