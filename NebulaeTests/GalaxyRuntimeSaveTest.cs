using HarmonyLib;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class GalaxyRuntimeSaveTest
{
    [TestMethod]
    [DataRow(1024)]
    [DataRow(6400)]
    [DataRow(6401)]
    [DataRow(8191)]
    public void BothPlanetRecordListsRoundTripAndLeaveTheFollowingSaveSectionAligned(int count)
    {
        var galaxy = new GalaxyData { starCount = count, stars = new StarData[count] };
        for (var i = 0; i < count; i++)
        {
            var star = new StarData { galaxy = galaxy, id = i + 1, index = i, planetCount = 4, planets = new PlanetData[4] };
            galaxy.stars[i] = star;
            for (var j = 0; j < 4; j++)
                star.planets[j] = new PlanetData
                {
                    galaxy = galaxy,
                    star = star,
                    id = star.id * 100 + j + 1,
                    scanned = true,
                    birthPoint = Vector3.up,
                    veinGroups = new[]
                    {
                        default(VeinGroup),
                        new VeinGroup { type = EVeinType.Iron, pos = new Vector3(.25f, -.5f, .75f), count = 17, amount = 1234567 }
                    },
                    landPercent = .25f,
                    vegeCollectionActive = j % 2 == 0
                };
        }
        var original = Export(galaxy);
        foreach (var star in galaxy.stars)
            foreach (var planet in star.planets)
            {
                planet.scanned = false; planet.vegeCollectionActive = false; planet.landPercent = 0;
            }
        var harmony = new Harmony("nebulae.tests.galaxy-runtime-save");
        try
        {
            harmony.CreateClassProcessor(typeof(ExtendedGalaxyRuntimeData_Patch)).Patch();
            using var stream = new MemoryStream(original);
            using var reader = new BinaryReader(stream);
            galaxy.ImportRuntimeData(reader);
            TestAssert.AreEqual(0x12345678, reader.ReadInt32(), "Native statistics/player/factory data follows this sentinel.");
            TestAssert.AreEqual(stream.Length, stream.Position);
            var last = galaxy.stars[count - 1].planets;
            TestAssert.IsTrue(last.All(planet => planet.scanned && planet.landPercent == .25f));
            TestAssert.IsTrue(last[0].vegeCollectionActive);
            TestAssert.IsFalse(last[3].vegeCollectionActive);
            CollectionAssert.AreEqual(original, Export(galaxy), "The native save layout must remain unchanged.");
        }
        finally { harmony.UnpatchSelf(); }
    }

    private static byte[] Export(GalaxyData galaxy)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        galaxy.ExportRuntimeData(writer);
        writer.Write(0x12345678);
        writer.Flush();
        return stream.ToArray();
    }
}
