using HarmonyLib;
using NebulaeModel.Packets.Factory;
using NebulaeWorld.Factory;
using NebulaeWorld;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class BuildingDeathSyncTest
{
    [TestMethod]
    public void RepeatedGroundFramesResetAllUnitStatisticsBeforeCountingAgain()
    {
        var system = Uninitialized<EnemyDFGroundSystem>();
        system.factory = Uninitialized<PlanetFactory>();
        system.factory.enemyPool = new EnemyData[1];
        system.bases = new ObjectPool<DFGBaseComponent>(); system.bases.Reset();
        var b = system.bases.Add();
        var harmony = new Harmony("nebulae.tests.assault-count-reset");
        var previous = Multiplayer.Session;
        try
        {
            Multiplayer.Session = Uninitialized<MultiplayerSession>();
            harmony.Patch(AccessTools.Method(typeof(EnemyDFGroundSystem), nameof(EnemyDFGroundSystem.GameTickLogic_Base)),
                prefix: new HarmonyMethod(typeof(EnemyDFGroundSystem_Patch), nameof(EnemyDFGroundSystem_Patch.GameTickLogic_Base_Prefix)));
            for (var tick = 0; tick < 300; tick++)
            {
                b.currentIncomingAssaultingUnitCount = 9; // A server summary also arrives between frames.
                b.currentIncomingAttackingUnitCount = 12;
                b.currentReadyUnitCount = 7;
                system.GameTickLogic_Base(EAggressiveLevel.Normal);
                TestAssert.AreEqual(0, b.currentIncomingAssaultingUnitCount);
                TestAssert.AreEqual(0, b.currentIncomingAttackingUnitCount);
                TestAssert.AreEqual(0, b.currentReadyUnitCount);
                for (var unit = 0; unit < 9; unit++) b.currentIncomingAssaultingUnitCount++;
                TestAssert.AreEqual(9, b.totalIncomingAssaultingUnitCount);
            }
        }
        finally { harmony.UnpatchSelf(); Multiplayer.Session = previous; }
    }

    [TestMethod]
    public void DestroyedBuildingTransfersPortableSettingsWithoutNativePoolIndexes()
    {
        var source = new PrebuildData
        {
            id = 10,
            protoId = 2303,
            modelIndex = 65,
            ruinId = 2,
            isDestroyed = true,
            itemRequired = 1,
            pos = new Vector3(1, 200, 3),
            rot = Quaternion.identity,
            pos2 = new Vector3(4, 200, 6),
            rot2 = Quaternion.identity,
            parameters = [7, 8, 9],
            paramCount = 3,
            content = "building settings",
            recipeId = 4,
            filterId = 5,
            warningId = 87,
            extraInfoId = 99,
            modelId = 52,
            colliderId = 42,
            hashAddress = 71,
            builderModuleId = 19,
            builderLaunched = true,
            builderValue = .5f
        };
        var bytes = DeathPrebuildCodec.Encode(source);
        var packet = RoundTrip(new BuildingDeathPacket
        {
            PlanetId = 103,
            Sequence = 12,
            ObjectId = 3,
            ProtoId = source.protoId,
            EntityGeneration = 5,
            TurretGeneration = 6,
            Prebuild = bytes,
            Connections = new int[16],
            PrebuildGeneration = 90,
            ExtraInfo = "label"
        });
        var restored = DeathPrebuildCodec.Decode(packet.Prebuild);
        TestAssert.AreEqual(source.pos2, restored.pos2);
        TestAssert.AreEqual(source.rot, restored.rot);
        TestAssert.AreEqual(source.content, restored.content);
        TestAssert.AreEqual(2, restored.ruinId);
        CollectionAssert.AreEqual(source.parameters, restored.parameters);
        TestAssert.AreEqual(0, restored.warningId + restored.extraInfoId + restored.modelId + restored.colliderId + restored.hashAddress + restored.builderModuleId);
        TestAssert.IsFalse(restored.builderLaunched);
        TestAssert.AreEqual(90L, packet.PrebuildGeneration);
        for (var size = 1; size < bytes.Length; size++)
        {
            var error = TestAssert.Throws<Exception>(() => DeathPrebuildCodec.Decode(bytes.Take(size).ToArray()));
            TestAssert.IsTrue(error is InvalidDataException or EndOfStreamException);
        }
        TestAssert.Throws<InvalidDataException>(() => DeathPrebuildCodec.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
    }

    [TestMethod]
    public void ADeathWithAnUnexpectedAllocatorFailsBeforeChangingEitherPool()
    {
        var factory = Uninitialized<PlanetFactory>();
        SetBackingField(factory, "planet", new PlanetData { id = 103 });
        factory.entityPool = new EntityData[8]; factory.entityPool[3] = new EntityData { id = 3, protoId = 2303 };
        factory.prebuildPool = new PrebuildData[8]; factory.prebuildCursor = 2;
        var packet = new BuildingDeathPacket
        {
            PlanetId = 103,
            Sequence = 1,
            ObjectId = 3,
            ProtoId = 2303,
            Connections = new int[16],
            PrebuildGeneration = 1,
            Prebuild = DeathPrebuildCodec.Encode(new PrebuildData { id = 5, protoId = 2303, modelIndex = 65, isDestroyed = true })
        };
        TestAssert.Throws<InvalidOperationException>(() => BuildingDeathReplication.Apply(factory, packet));
        TestAssert.AreEqual(3, factory.entityPool[3].id);
        TestAssert.AreEqual(2, factory.prebuildCursor);
        TestAssert.IsTrue(factory.prebuildPool.All(p => p.id == 0));
    }
}
