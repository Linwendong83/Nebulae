using System.Runtime.Serialization;
using NebulaeWorld;
using UnityEngine;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class GalaxyAstroPreparationTest
{
    [TestMethod]
    public void WeightedPosePartitionsNeverSplitAStarOrOmitAPlanet()
    {
        var stars = Enumerable.Range(0, 8191).Select(i => new StarData { planetCount = i % 13 }).ToArray();
        var plan = new GalaxyPosePlan(stars, stars.Length);
        Check.AreEqual(stars.Sum(s => s.planetCount), plan.Total);
        foreach (var workers in new[] { 1, 2, 4, 8, 16, 24, 32 })
        {
            var visits = new int[stars.Length];
            GalaxyWork.Run(plan.Total, (start, end) =>
            {
                for (var i = plan.StarAt(start); i < plan.StarAt(end); i++) Interlocked.Increment(ref visits[i]);
            }, 32, workers);
            for (var i = 0; i < stars.Length; i++)
                if (stars[i].planetCount > 0) Check.AreEqual(1, visits[i], $"Star {i}, workers {workers}");
        }
    }

    [TestMethod]
    public void SharedRotationRetainsNativeFloatBits()
    {
        var random = new System.Random(731);
        foreach (var rotation in new[] { Quaternion.identity, new Quaternion(.1f, -.2f, .3f, -.9f),
                     new Quaternion(-0f, 0f, -0f, -1f) })
        {
            var transform = new AstroPreparation.RelativeTransform(rotation);
            for (var i = 0; i < 1000; i++)
            {
                var scale = i % 2 == 0 ? 1e12 : 1e-6;
                var pos = new VectorLF3((random.NextDouble() - .5) * scale,
                    (random.NextDouble() - .5) * scale, (random.NextDouble() - .5) * scale);
                Vector3 expected = Maths.QInvRotateLF(rotation, pos);
                Check.IsTrue(StarmapAstroBuffer_Patch.SameBits(expected, transform.Position(pos)), $"Position {i}");
            }
        }
    }

    [TestMethod]
    [DataRow(64)]
    [DataRow(256)]
    [DataRow(2100)]
    public void ParallelGalaxyPreparationKeepsCanonicalHolesAndAllRenderAliases(int count)
    {
        var previous = Multiplayer.IsDedicated;
        try
        {
            Multiplayer.IsDedicated = false;
            var model = Model(count, 32);
            var origin = new VectorLF3(1e10, -2e10, 3e10);
            var rotation = model.gameData.relativeRot;
            var inverse = rotation; inverse.w = -inverse.w;
            var map = GalaxyRenderAstros.For(model.gameData.galaxy);
            foreach (var starmap in new[] { false, true })
            {
                var canonical = starmap ? model.starmapGalaxyAstroArr : model.galaxyAstroArr;
                var render = AstroPreparation.Galaxy(model, starmap, origin);
                if (map == null) Check.AreSame(canonical, render);
                else Check.AreNotSame(canonical, render);
                foreach (var star in model.gameData.galaxy.stars)
                    foreach (var id in new[] { star.astroId }.Concat(star.planets.Select(p => p.astroId)))
                    {
                        var input = model.sector.galaxyAstros[id];
                        var expected = new AstroPoseR
                        {
                            rpos = starmap ? (Vector3)(input.uPos - origin) : (Vector3)Maths.QInvRotateLF(rotation, input.uPos - origin),
                            rrot = starmap ? input.uRot : inverse * input.uRot,
                            radius = input.uRadius
                        };
                        Check.IsTrue(StarmapAstroBuffer_Patch.SameBits(expected, canonical[id]), $"Canonical {id}");
                        Check.IsTrue(StarmapAstroBuffer_Patch.SameBits(expected, render[id]), $"GPU canonical {id}");
                        if (map != null)
                        {
                            Check.AreEqual(expected, render[map.RenderId((uint)id)], $"GPU alias {id}");
                            if (map.RenderId((uint)id) != id)
                                Check.AreEqual(default(AstroPoseR), canonical[map.RenderId((uint)id)], "CPU empty slots must stay empty");
                        }
                    }
                var changedId = count * 100 + 1;
                model.sector.galaxyAstros[changedId].uPos.x += 100;
                Check.AreSame(render, AstroPreparation.Galaxy(model, starmap, origin));
                Check.AreEqual(canonical[changedId], render[map?.RenderId((uint)changedId) ?? (uint)changedId]);
            }
            Multiplayer.IsDedicated = true;
            Check.AreSame(model.galaxyAstroArr, AstroPreparation.Galaxy(model, false, origin));
        }
        finally { Multiplayer.IsDedicated = previous; }
    }

    [TestMethod]
    public void SectorDeletionShrinkReuseAndStarmapMatchNativeOutputs()
    {
        var model = Model(1, 32768);
        model.sector.astroCursor = 19001;
        for (var i = 1; i < model.sector.astroCursor; i++)
            model.sector.astros[i] = new AstroData
            {
                id = i % 3 == 0 ? 0 : 1000000 + i,
                uPos = new VectorLF3(i * 100.0, i * -20.0, i),
                uRot = Quaternion.identity,
                uRadius = i % 70
            };
        foreach (var starmap in new[] { false, true })
        {
            AssertSector(model, starmap);
            model.sector.astros[1].id = 0;
            model.sector.astroCursor = 80;
            AssertSector(model, starmap);
            model.sector.astros[1].id = 1000001;
            model.sector.astroCursor = 19001;
            AssertSector(model, starmap);
        }
    }

    private static void AssertSector(SectorModel model, bool starmap)
    {
        var origin = new VectorLF3(-10000, 2000, 300);
        var rotation = model.gameData.relativeRot;
        var inverse = rotation; inverse.w = -inverse.w;
        var output = starmap ? model.starmapAstroArr : model.astroArr;
        var expected = new AstroPoseR[output.Length];
        for (var i = 1; i < model.sector.astroCursor; i++)
        {
            var input = model.sector.astros[i];
            if (input.id > 0) expected[i] = new AstroPoseR
            {
                rpos = starmap ? (Vector3)(input.uPos - origin) : (Vector3)Maths.QInvRotateLF(rotation, input.uPos - origin),
                rrot = starmap ? input.uRot : inverse * input.uRot,
                radius = input.uRadius
            };
            else if (!starmap) expected[i].rrot.w = 1f;
        }
        AstroPreparation.Sector(model, starmap, origin);
        CollectionAssert.AreEqual(expected, output);
        if (starmap) Check.AreEqual(1f, model.astroArr[3].rrot.w);
    }

    [TestMethod]
    public void DeferredRenderAliasesCopyOnlyTheLatestCanonicalPoses()
    {
        var previous = Multiplayer.IsDedicated;
        try
        {
            Multiplayer.IsDedicated = false;
            var model = Model(2100, 32);
            var render = AstroPreparation.Galaxy(model, false, default);
            var id = 210001;
            var old = render[id];
            model.sector.galaxyAstros[id].uPos.x += 100;
            Check.AreSame(render, AstroPreparation.Galaxy(model, false, default, true));
            Check.AreEqual(old, render[id]);
            model.sector.galaxyAstros[id].uPos.x += 200;
            AstroPreparation.Galaxy(model, false, default, true);
            AstroPreparation.Mirror(model, render);
            Check.AreEqual(model.galaxyAstroArr[id], render[id]);
            Check.AreEqual(render[id], render[GalaxyRenderAstros.For(model.gameData.galaxy).RenderId((uint)id)]);
        }
        finally { Multiplayer.IsDedicated = previous; }
    }

    [TestMethod]
    public void UploadRangesInitializeTailsAndClearShrinkingCoalescedPrefixes()
    {
        Check.AreEqual(1000, AstroUploads.UploadCount(999, 1000));
        Check.AreEqual(1000, AstroUploads.UploadCount(750, 1000));
        Check.AreEqual(500, AstroUploads.UploadCount(500, 1000));
        var state = new AstroUploadRange();
        var data = new AstroPoseR[1000];
        state.Stage(data, 600); Check.AreEqual(1000, state.Count); state.Complete();
        state.Stage(data, 200); Check.AreEqual(600, state.Count);
        state.Stage(data, 100); Check.AreEqual(600, state.Count); state.Complete();
        state.Stage(data, 50); Check.AreEqual(100, state.Count); state.Complete();
        state.Stage(data, 900); state.Stage(data, 10); Check.AreEqual(900, state.Count); state.Complete();
        state.Stage(data, 10); Check.AreEqual(10, state.Count); state.Complete();
        state.Stage(new AstroPoseR[1500], 1); Check.AreEqual(1500, state.Count); state.Complete();
        state.Invalidate(); state.Stage(data, 1); Check.AreEqual(1000, state.Count);
        Check.Throws<ArgumentOutOfRangeException>(() => state.Stage(data, 1001));
    }

    private static SectorModel Model(int stars, int capacity)
    {
        var galaxy = new GalaxyData
        {
            starCount = stars,
            stars = Enumerable.Range(1, stars).Select(id => new StarData
            {
                id = id,
                index = id - 1,
                planetCount = 2,
                planets = new[] { new PlanetData { id = id * 100 + 1 }, new PlanetData { id = id * 100 + 2 } }
            }).ToArray()
        };
        var source = new AstroData[(stars + 1) * 100];
        for (var i = 0; i < source.Length; i++) source[i] = new AstroData
        {
            id = i,
            uPos = new VectorLF3(i * 1000.5, -i * 70.1, i * 19.3),
            uRot = new Quaternion(.2f, .3f, .1f, .9f),
            uRadius = 200
        };
        var model = (SectorModel)FormatterServices.GetUninitializedObject(typeof(SectorModel));
        model.gameData = (GameData)FormatterServices.GetUninitializedObject(typeof(GameData));
        model.gameData.galaxy = galaxy;
        model.gameData.relativeRot = new Quaternion(.1f, -.2f, .3f, -.9f);
        model.sector = new SpaceSector { galaxyAstros = source, astros = new AstroData[capacity] };
        model.galaxyAstroArr = new AstroPoseR[source.Length]; model.starmapGalaxyAstroArr = new AstroPoseR[source.Length];
        model.astroArr = new AstroPoseR[capacity]; model.starmapAstroArr = new AstroPoseR[capacity];
        return model;
    }
}
