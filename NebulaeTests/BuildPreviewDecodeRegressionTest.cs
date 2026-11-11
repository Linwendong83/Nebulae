using HarmonyLib;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Factory;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class BuildPreviewDecodeRegressionTest
{
    private readonly Harmony harmony = new("nebulae.tests.build-preview-decode");
    private static ItemProtoSet items = null!;
    private static ModelProtoSet models = null!;
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void SetUp()
    {
        var item = new ItemProto { ID = 2101, ModelIndex = 70 };
        var desc = new PrefabDesc { modelIndex = 70 };
        items = Uninitialized<ItemProtoSet>(); items.dataArray = [item];
        SetField(items, "dataIndices", new Dictionary<int, int> { [2101] = 0 });
        models = Uninitialized<ModelProtoSet>(); models.modelArray = new ModelProto[71];
        models.modelArray[70] = new ModelProto { ID = 70, prefabDesc = desc };
        harmony.Patch(AccessTools.PropertyGetter(typeof(LDB), nameof(LDB.items)), prefix: new HarmonyMethod(typeof(BuildPreviewDecodeRegressionTest), nameof(GetItems)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(LDB), nameof(LDB.models)), prefix: new HarmonyMethod(typeof(BuildPreviewDecodeRegressionTest), nameof(GetModels)));
    }

    [TestCleanup] public void CleanUp() => harmony.UnpatchSelf();
    private static bool GetItems(ref ItemProtoSet __result) { __result = items; return false; }
    private static bool GetModels(ref ModelProtoSet __result) { __result = models; return false; }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2048)]
    public void ValidCompressedPreviewDecodesIncludingNativeBuildingParameters(int parameterCount)
    {
        var preview = new BuildPreview { item = items.dataArray[0], desc = models.modelArray[70].prefabDesc,
            lpos = new Vector3(1, 200, 3), lpos2 = new Vector3(1, 200, 3),
            lrot = Quaternion.identity, lrot2 = Quaternion.identity, condition = EBuildCondition.Ok,
            parameters = Enumerable.Range(0, parameterCount).ToArray(), paramCount = parameterCount, nearestPowerObjId = [4, 7] };
        var packet = new CreatePrebuildsRequest(104, [preview], 1, nameof(BuildTool_Click), 1);
        using (var reader = new BinaryUtils.Reader(packet.BuildPreviewData))
        {
            TestAssert.AreEqual(1, reader.BinaryReader.ReadInt32());
            TestContext.WriteLine($"compressedBytes={packet.BuildPreviewData.Length}, decoderLength={reader.BinaryReader.BaseStream.Length}");
        }
        TestAssert.IsTrue(packet.TryGetBuildPreviews(out var decoded), "A valid locally encoded construction request must decode on the receiving peer.");
        TestAssert.HasCount(1, decoded);
        TestAssert.AreEqual(2101, decoded[0].item.ID);
        TestAssert.AreEqual(preview.lpos, decoded[0].lpos);
        CollectionAssert.AreEqual(preview.nearestPowerObjId, decoded[0].nearestPowerObjId);
        CollectionAssert.AreEqual(preview.parameters, decoded[0].parameters);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void IncompleteOrTrailingDecodedPayloadStillCannotBeAccepted(bool trailing)
    {
        var preview = new BuildPreview { item = items.dataArray[0], desc = models.modelArray[70].prefabDesc, parameters = [] };
        var packet = new CreatePrebuildsRequest(104, [preview], 1, nameof(BuildTool_Click), 1);
        using var decoded = new System.IO.MemoryStream();
        using (var compressed = new BinaryUtils.Reader(packet.BuildPreviewData)) compressed.BinaryReader.BaseStream.CopyTo(decoded);
        var bytes = decoded.ToArray();
        using var writer = new BinaryUtils.Writer();
        writer.BinaryWriter.Write(bytes, 0, bytes.Length - (trailing ? 0 : 1));
        if (trailing) writer.BinaryWriter.Write((byte)1);
        packet.BuildPreviewData = writer.CloseAndGetBytes();
        TestAssert.IsFalse(packet.TryGetBuildPreviews(out _));
    }
}
