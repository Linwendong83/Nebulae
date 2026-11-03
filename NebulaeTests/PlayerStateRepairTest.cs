using HarmonyLib;
using NebulaeModel.Contracts;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Combat.Mecha;
using NebulaeModel.Packets.Factory;
using NebulaeWorld;
using NebulaeWorld.Combat;
using NebulaeWorld.Factory;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class PlayerStateRepairTest
{
    [TestMethod]
    public void OnlyAcceptedMatchingReceiptsReleaseInventoryWork()
    {
        var life = new PlayerLifeManager();
        var ticket = life.RequestCheckpoint();
        var pending = new PlayerLifePacket { Life = new PlayerLifeData { Revision = 100 } };
        SetField(life, "pending", pending); SetField(life, "pendingTicket", ticket); SetField(life, "requestedTicket", 0L);
        life.Acknowledge(PlayerLifePacket.CreateReceipt(2, 99, acceptedRevision: 100));
        TestAssert.IsTrue(life.BlocksInventory);
        life.Acknowledge(PlayerLifePacket.CreateReceipt(2, 100, acceptedRevision: 99));
        TestAssert.IsTrue(life.BlocksInventory);
        life.Acknowledge(PlayerLifePacket.CreateReceipt(2, 100, acceptedRevision: 100));
        TestAssert.AreEqual(ticket, life.AcceptedTicket);
        TestAssert.IsFalse(life.BlocksInventory);
    }

    [TestMethod]
    public void ARejectedSnapshotRetainsTheCheckpointBarrier()
    {
        var life = new PlayerLifeManager();
        var ticket = life.RequestCheckpoint();
        SetField(life, "pending", new PlayerLifePacket { Life = new PlayerLifeData { Revision = 100 } });
        SetField(life, "pendingTicket", ticket); SetField(life, "requestedTicket", 0L);
        life.Acknowledge(PlayerLifePacket.CreateReceipt(2, 100, CheckpointReceiptStatus.Retry, 90, CheckpointRejectReason.Busy));
        TestAssert.IsTrue(life.BlocksInventory);
        TestAssert.AreEqual(0L, life.AcceptedTicket);
        TestAssert.AreEqual(ticket, life.RequestCheckpoint(), "Retries must preserve the waiting command's ticket.");
    }

    [TestMethod]
    public void NewerCheckpointDemandIsNotClearedByAnEarlierReceipt()
    {
        var life = new PlayerLifeManager();
        var first = life.RequestCheckpoint();
        SetField(life, "pending", new PlayerLifePacket { Life = new PlayerLifeData { Revision = 100 } });
        SetField(life, "pendingTicket", first); SetField(life, "requestedTicket", 0L);
        var next = life.RequestCheckpoint();
        life.Acknowledge(PlayerLifePacket.CreateReceipt(2, 100, acceptedRevision: 100));
        TestAssert.IsGreaterThan(life.AcceptedTicket, next);
        TestAssert.IsTrue(life.BlocksInventory);
    }

    [TestMethod]
    public void RetryingAnOldSnapshotStillCoversTheNewerWaitingInventoryRequest()
    {
        var life = new PlayerLifeManager();
        var first = life.RequestCheckpoint();
        SetField(life, "pending", new PlayerLifePacket { Life = new PlayerLifeData { Revision = 100 } });
        SetField(life, "pendingTicket", first); SetField(life, "requestedTicket", 0L);
        var next = life.RequestCheckpoint();
        life.Acknowledge(PlayerLifePacket.CreateReceipt(2, 100, CheckpointReceiptStatus.Retry, 90, CheckpointRejectReason.Busy));
        TestAssert.AreEqual(next, life.RequestCheckpoint());
        TestAssert.AreEqual(0L, life.AcceptedTicket);
    }

    [TestMethod]
    public void UpdatingCombatResearchKeepsLiveDroneStateAndPersonalRechargeSettings()
    {
        var drones = new ConstructionModuleComponent { droneCount = 10, droneAliveCount = 9, droneIdleCount = 3 };
        var destination = new Mecha { constructionModule = drones, energyShieldRechargeSpeed = 7, energyShieldEnergy = 123 };
        var source = new Mecha { energyShieldCapacity = 100000, reactorPowerGen = 240000, hpMaxUpgrade = 500 };
        CombatAuthorityManager.RefreshCombatLimits(destination, source);
        TestAssert.AreEqual(100000L, destination.energyShieldCapacity);
        TestAssert.AreEqual(240000d, destination.reactorPowerGen);
        TestAssert.AreEqual(123L, destination.energyShieldEnergy);
        TestAssert.AreEqual(7f, destination.energyShieldRechargeSpeed);
        TestAssert.AreEqual(3, drones.droneIdleCount);
        TestAssert.AreEqual(9, drones.droneAliveCount);
    }

    [TestMethod]
    public void ManualBuildIntentCarriesInstanceIdentityAndRejectsMalformedMaterialCounts()
    {
        var original = new ManualBuildIntent(101, 7, 42, 1, 4);
        TestAssert.IsTrue(ManualBuildIntent.TryDecode(original.Encode(), out var decoded));
        TestAssert.AreEqual(42L, decoded.Generation);
        TestAssert.IsFalse(ManualBuildIntent.TryDecode(new ManualBuildIntent(101, 7, 0, 1, 0).Encode(), out _));
        TestAssert.IsFalse(ManualBuildIntent.TryDecode(new ManualBuildIntent(101, 7, 42, 1, 11).Encode(), out _));
        TestAssert.IsFalse(ManualBuildIntent.TryDecode(original.Encode().Take(23).ToArray(), out _));
    }

    [TestMethod]
    public void PrebuildSlotReuseAndFactorySnapshotKeepIndependentInstanceGenerations()
    {
        var previous = Multiplayer.Session;
        try
        {
            var session = Uninitialized<MultiplayerSession>(); session.Server = Uninitialized<global::NebulaeNetwork.Server>();
            Multiplayer.Session = session;
            var factory = Uninitialized<PlanetFactory>(); SetBackingField(factory, "planet", new PlanetData { id = 101 });
            factory.prebuildCursor = 2; factory.prebuildPool = new PrebuildData[2]; factory.prebuildPool[1].id = 1;
            var identities = new PrebuildInstances();
            var first = identities.Get(factory, 1); identities.Remove(101, 1);
            var second = identities.Get(factory, 1);
            TestAssert.IsGreaterThan(first, second);
            var claims = new byte[] { 0, 0, 0, 0 };
            var snapshot = identities.Export(factory, claims); var replica = new PrebuildInstances();
            CollectionAssert.AreEqual(claims, replica.Import(factory, snapshot));
            TestAssert.AreEqual(second, replica.Get(factory, 1));
        }
        finally { Multiplayer.Session = previous; }
    }

    [TestMethod]
    public void RepairQueuesDropOutOfRangeAndRecycledStatReferences()
    {
        var factory = Uninitialized<PlanetFactory>(); SetBackingField(factory, "planet", new PlanetData { id = 101 });
        factory.entityCursor = 2; factory.entityPool = new EntityData[2];
        factory.constructionSystem = Uninitialized<ConstructionSystem>();
        factory.constructionSystem.constructStats = new DataPool<ConstructStat> { cursor = 2, buffer = new ConstructStat[2] };
        factory.skillSystem = Uninitialized<SkillSystem>();
        factory.skillSystem.combatStats = new DataPool<CombatStat> { cursor = 2, buffer = new CombatStat[2] };
        factory.entityPool[1] = new EntityData { id = 1, constructStatId = 1, combatStatId = 1 };
        factory.constructionSystem.constructStats.buffer[1] = new ConstructStat { id = 1, entityId = 1 };
        factory.skillSystem.combatStats.buffer[1] = new CombatStat { id = 1, objectId = 1, originAstroId = 101, objectType = (int)EObjectType.Entity };
        var module = new ConstructionModuleComponent { entityId = 1, repairTargets = new ConstructTargetExtra[4] };
        module.repairTargets[0].objectId = 999; module.repairTargets[1].objectId = 1;
        ConstructionRepairTargets.Prune(module, factory);
        TestAssert.AreEqual(1, module.repairTargets[0].objectId);
        factory.entityPool[1].constructStatId = 0;
        ConstructionRepairTargets.Prune(module, factory);
        TestAssert.AreEqual(0, module.repairTargets[0].objectId);
        TestAssert.IsGreaterThan(0, module.repairTargetTotalCount);
        AccessTools.Method(typeof(ConstructionModuleComponent), "PreLaunchDrone").Invoke(module, [factory, null]);
    }

    [TestMethod]
    public void BlueprintIdentitiesLeaveInBoundedBatchesInsteadOfOnePacketPerPrebuild()
    {
        var previous = Multiplayer.Session;
        try
        {
            var network = new StarRecordingNetwork();
            var session = Uninitialized<MultiplayerSession>();
            session.Server = Uninitialized<global::NebulaeNetwork.Server>();
            session.Network = network;
            Multiplayer.Session = session;
            var factory = Uninitialized<PlanetFactory>();
            SetBackingField(factory, "planet", new PlanetData { id = 101, star = new StarData { id = 3 } });
            factory.prebuildCursor = 1201; factory.prebuildPool = new PrebuildData[1201];
            for (var id = 1; id <= 1200; id++) factory.prebuildPool[id].id = id;
            var identities = new PrebuildInstances();
            for (var id = 1; id <= 1200; id++) identities.Created(factory, id);
            TestAssert.HasCount(0, network.StarPackets, "Identities wait for the frame boundary instead of one frame per prebuild.");
            identities.Flush();
            TestAssert.HasCount(3, network.StarPackets, "1200 identities are three bounded batches.");
            var sent = network.StarPackets.Cast<PrebuildInstanceBatchPacket>().ToArray();
            TestAssert.AreEqual(1200, sent.Sum(packet => packet.PrebuildIds.Length));
            TestAssert.IsTrue(sent.All(packet => packet.PlanetId == 101 && packet.PrebuildIds.Length <= PrebuildInstances.BatchSize));
            var replica = new PrebuildInstances();
            foreach (var packet in sent) replica.Apply(packet);
            for (var id = 1; id <= 1200; id++)
                TestAssert.AreEqual(identities.Get(factory, id), replica.Get(factory, id));
            // Replaying a batch must not move a slot backwards.
            foreach (var packet in sent) replica.Apply(packet);
            TestAssert.AreEqual(identities.Get(factory, 1200), replica.Get(factory, 1200));
        }
        finally { Multiplayer.Session = previous; }
    }

    private sealed class StarRecordingNetwork : INetworkProvider
    {
        public List<object> StarPackets { get; } = new();
        public INetPacketProcessor PacketProcessor => throw new NotSupportedException();
        public byte[] WriteOnce<T>(T packet) where T : class, new() => throw new NotSupportedException();
        public void SendPacket<T>(T packet) where T : class, new() => throw new NotSupportedException();
        public void SendToMatching<T>(T packet, Predicate<INebulaePlayer> condition) where T : class, new() => throw new NotSupportedException();
        public void SendPacketToLocalStar<T>(T packet) where T : class, new() => throw new NotSupportedException();
        public void SendPacketToLocalPlanet<T>(T packet) where T : class, new() => throw new NotSupportedException();
        public void SendPacketToPlanet<T>(T packet, int planetId) where T : class, new() => throw new NotSupportedException();
        public void SendPacketToStar<T>(T packet, int starId) where T : class, new() => StarPackets.Add(packet);
        public void SendPacketExclude<T>(T packet, INebulaeConnection exclude) where T : class, new() => throw new NotSupportedException();
        public void SendPacketToStarExclude<T>(T packet, int starId, INebulaeConnection exclude) where T : class, new() => throw new NotSupportedException();
        public void Dispose() { }
    }

    [TestMethod]
    public void ParallelFactoryCompletionsKeepStableDistinctInstanceIdentities()
    {
        var previous = Multiplayer.Session;
        try
        {
            var session = Uninitialized<MultiplayerSession>(); session.Server = Uninitialized<global::NebulaeNetwork.Server>();
            Multiplayer.Session = session;
            var factories = Enumerable.Range(1, 8).Select(i =>
            {
                var factory = Uninitialized<PlanetFactory>(); SetBackingField(factory, "planet", new PlanetData { id = 100 + i });
                factory.prebuildCursor = 2; factory.prebuildPool = new PrebuildData[2]; factory.prebuildPool[1].id = 1;
                return factory;
            }).ToArray();
            var instances = new PrebuildInstances();
            var results = new long[8000];
            System.Threading.Tasks.Parallel.For(0, results.Length, i => results[i] = instances.Get(factories[i % 8], 1));
            TestAssert.HasCount(8, results.Distinct().ToArray());
            for (var i = 0; i < results.Length; i++) TestAssert.AreEqual(results[i % 8], results[i]);
        }
        finally { Multiplayer.Session = previous; }
    }
}
