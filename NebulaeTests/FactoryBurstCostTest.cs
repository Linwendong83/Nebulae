using System.Reflection;
using HarmonyLib;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Factory;
using NebulaeModel.Packets.Routers;
using NebulaeModel.Packets.Trash;
using NebulaePatcher.Patches.Transpilers;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

/// <summary>Transport and lifecycle costs that only appear in multiplayer bursts.</summary>
[TestClass, DoNotParallelize]
public class FactoryBurstCostTest
{
    [TestMethod]
    public void BurstFactoryAndTrashPacketsShareOneTransportEnvelope()
    {
        var writer = new NebulaeNetPacketProcessor();
        PacketUtils.RegisterNestedTypes(writer);
        // Consecutive records of one area dismantle, one build wave or a container's contents are
        // independent operations, so they may share one socket frame.
        TestAssert.IsTrue(RealtimePacketBatch.CanInclude(writer.Write(new DestructEntityRequest(101, 7, 8, 9))));
        TestAssert.IsTrue(RealtimePacketBatch.CanInclude(writer.Write(new BuildEntityRequest(101, 7, 8, 9))));
        var trash = new TrashObject(1101, 10, 0, Vector3.zero, Quaternion.identity);
        TestAssert.IsTrue(RealtimePacketBatch.CanInclude(writer.Write(
            new TrashSystemNewPlanetTrashPacket(3, trash, new TrashData { nearPlanetId = 101, lPos = Vector3.one }))));
        TestAssert.IsTrue(RealtimePacketBatch.CanInclude(writer.Write(
            new TrashSystemNewPlayerTrashPacket(1, 4, trash, new TrashData { nearStarId = 1, uVel = new VectorLF3(1, 2, 3) }))));
        // Control and snapshot traffic still travels as its own frame.
        TestAssert.IsFalse(RealtimePacketBatch.CanInclude(writer.Write(new global::NebulaeModel.Packets.GameStates.GameStateRequest())));
        // A client envelope is only applied by the host, so it may carry burst operations but no
        // packet whose processor counts on the host relaying it to other peers.
        var state = writer.Write(new global::NebulaeModel.Packets.GameStates.GameStateUpdate(1, 1, 60, 1));
        TestAssert.IsFalse(RealtimePacketBatch.CanInclude(state, hostSide: false));
        TestAssert.IsTrue(RealtimePacketBatch.CanInclude(writer.Write(new DestructEntityRequest(101, 7, 8, 9)), hostSide: false));
        var batch = RealtimePacketBatch.Encode(new[] { writer.Write(new DestructEntityRequest(101, 7, 8, 9)) }, writer);
        StarBroadcastPacket? envelope = null;
        var reader = new NebulaeNetPacketProcessor();
        PacketUtils.RegisterNestedTypes(reader);
        reader.SubscribeReusable<StarBroadcastPacket>(packet => envelope = packet);
        reader.ReadPacket(new NetDataReader(batch));
        TestAssert.IsNotNull(envelope);
        TestAssert.AreEqual(RealtimePacketBatch.UnroutableStarId, envelope!.StarId,
            "The host applies a transport batch; it must never fan the children out again.");
        TestAssert.HasCount(1, envelope.PacketObjects);
    }

    [TestMethod]
    public void RepeatedReplayPayloadIsSentOncePerCollectionVersion()
    {
        var previous = GameMain.data;
        try
        {
            var player = Uninitialized<Player>();
            var collection = new VegetableCollection();
            SetBackingField(player, "vegetableCollection", collection);
            var data = Uninitialized<GameData>();
            SetBackingField(data, "mainPlayer", player);
            GameMain.data = data;
            var manager = new NebulaeWorld.Player.VegetationManager();

            var first = manager.CaptureRequest(collection);
            TestAssert.IsGreaterThan(0, first.Bytes.Length);
            var repeat = manager.CaptureRequest(collection);
            TestAssert.IsEmpty(repeat.Bytes, "A second operation in the same tick repeats the revision.");
            TestAssert.AreEqual(first.Revision, repeat.Revision);

            manager.BeforeLocalChange(collection);
            var changed = manager.CaptureRequest(collection);
            TestAssert.IsGreaterThan(0, changed.Bytes.Length, "A changed collection is sent again.");
            TestAssert.AreNotEqual(first.Revision, changed.Revision);
        }
        finally { GameMain.data = previous; }
    }

    [TestMethod]
    public void DismantleDoesNotStopEveryPeerForAFullCollection()
    {
        var original = PatchProcessor.GetOriginalInstructions(
            AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.TakeBackItemsInEntity))).ToArray();
        var collect = AccessTools.Method(typeof(GC), "Collect", Type.EmptyTypes);
        TestAssert.IsTrue(original.Any(instruction => instruction.Calls(collect)), "native body allocates then collects");
        var transpiler = AccessTools.Method(typeof(FactoryDeathCollect_Transpiler), "TakeBackItemsInEntity_Transpiler");
        var patched = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { original })).ToArray();
        TestAssert.AreEqual(0, patched.Count(instruction => instruction.Calls(collect)));
        TestAssert.IsTrue(patched.Any(instruction => instruction.operand is MethodInfo method &&
            method.Name == "CollectOnlyInSingleplayer"));
    }
}
