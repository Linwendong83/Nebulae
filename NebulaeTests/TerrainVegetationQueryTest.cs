using HarmonyLib;
using NebulaePatcher.Patches.Transpilers;
using NebulaeWorld.Player;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class TerrainVegetationQueryTest
{
    [TestMethod]
    public void DataQueryWorksWithoutPhysicsAndDeduplicatesMultiColliderVegetation()
    {
        var pool = new VegeData[5];
        for (var id = 1; id < 5; id++) pool[id] = new VegeData
        { id = id, protoId = 1101, pos = new Vector3(id * 2, 0, 0), rot = Quaternion.identity, scl = Vector3.one };
        pool[2].id = 0;
        var collider = new ColliderData { pos = Vector3.zero, ext = Vector3.one, q = Quaternion.identity };
        var ids = new int[1];
        var count = TerrainVegetationQuery.Collect(pool, 5, Vector3.zero, 6, ref ids, _ => new[] { collider, collider });
        TestAssert.AreEqual(2, count);
        CollectionAssert.AreEqual(new[] { 1, 3 }, ids.Take(count).ToArray());
    }

    [TestMethod]
    public void BothConstructionAndFoundationUseTheDataQueryBoundary()
    {
        var query = AccessTools.Method(typeof(NearColliderLogic), nameof(NearColliderLogic.GetVegetablesInAreaNonAlloc));
        var replacement = AccessTools.Method(typeof(TerrainVegetationQuery_Transpiler), nameof(TerrainVegetationQuery_Transpiler.Collect));
        foreach (var method in TerrainVegetationQuery_Transpiler.TargetMethods())
        {
            var result = TerrainVegetationQuery_Transpiler.Transpiler(PatchProcessor.GetOriginalInstructions(method)).ToArray();
            TestAssert.AreEqual(0, result.Count(i => i.Calls(query)));
            TestAssert.AreEqual(1, result.Count(i => i.Calls(replacement)));
        }
    }
}
