using NebulaeModel.Packets.Factory;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class BlueprintPreviewRangeTest
{
    [TestMethod]
    public void ActivePoolRangePreservesGraphLinksAndExactPacketBytes()
    {
        var first = new BuildPreview { item = new ItemProto { ID = 2101 }, desc = new PrefabDesc { modelIndex = 70 }, lpos = new Vector3(1, 2, 3), parameters = [7, 9], paramCount = 2 };
        var second = new BuildPreview { item = first.item, desc = first.desc, lpos = new Vector3(4, 5, 6), nearestPowerObjId = [17], parameters = [] };
        first.output = second; second.input = first;
        // Duplicate references retain IndexOf's first occurrence; inactive tail entries
        // must never change graph indexing or become part of the packet.
        var pool = new[] { first, second, first, new BuildPreview() };
        var tool = Uninitialized<BuildTool_BlueprintPaste>(); tool.bpPool = pool; tool.bpCursor = 3;
        var range = BuildTool_Common_Patch.PreviewRange(tool);
        var expected = new CreatePrebuildsRequest(101, pool.Take(3).ToList(), 2, "blueprint", 17);
        var actual = new CreatePrebuildsRequest(101, range, 2, "blueprint", 17);
        CollectionAssert.AreEqual(expected.BuildPreviewData, actual.BuildPreviewData);
        TestAssert.HasCount(3, range);
        using var payload = new global::NebulaeModel.Networking.BinaryUtils.Reader(actual.BuildPreviewData);
        var reader = payload.BinaryReader;
        TestAssert.AreEqual(3, reader.ReadInt32());
        TestAssert.AreEqual(1, reader.ReadInt32(), "The output still references the second preview.");
        TestAssert.AreEqual(-1, reader.ReadInt32());
        pool[2] = second;
        TestAssert.AreSame(second, range[2], "The active view must not allocate a second preview array.");
    }
}
