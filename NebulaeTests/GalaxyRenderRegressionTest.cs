using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class GalaxyRenderRegressionTest
{
    [TestMethod]
    [DataRow(2048)]
    [DataRow(2049)]
    [DataRow(2100)]
    [DataRow(8191)]
    public void EveryGalaxyAstroHasAUniqueShaderReadablePose(int count)
    {
        var galaxy = Galaxy(count);
        var map = new GalaxyRenderAstros(galaxy);
        var poses = new AstroPoseR[(count + 1) * 100];
        var ids = galaxy.stars.SelectMany(s => new[] { s.astroId }.Concat(s.planets.Select(p => p.astroId))).ToArray();
        var renderIds = new HashSet<uint>();
        foreach (var id in ids)
        {
            var alias = map.RenderId((uint)id);
            Check.IsTrue(alias > 0 && alias <= GalaxyRenderAstros.ShaderMaxAstroId);
            Check.IsTrue(renderIds.Add(alias), "GPU alias collision: " + id);
            if (id <= GalaxyRenderAstros.ShaderMaxAstroId) Check.AreEqual((uint)id, alias);
        }
        // Different world/starmap origins and later moving planet poses must all
        // reach the very same GPU slot, without overwriting a real low-ID astro.
        foreach (var origin in new[] { -100000f, 0f, 123456f })
        {
            foreach (var id in ids) poses[id] = new AstroPoseR
            {
                rpos = new Vector3(id - origin, id * 2, -id),
                rrot = new Quaternion(.1f, .2f, .3f, .4f),
                radius = id % 100 + 1
            };
            map.Mirror(poses);
            foreach (var id in ids) Check.AreEqual(poses[id], poses[map.RenderId((uint)id)], "Astro " + id);
        }
        foreach (var id in new uint[] { 0, 1000001, 1016384, 1032768, 1065528 })
            Check.AreEqual(id, map.RenderId(id), "Detached/sector astros must retain their render frame.");
    }

    [TestMethod]
    public void FullAndPartialUploadsKeepCanonicalIdsAndSurvivePoolReuse()
    {
        var map = new GalaxyRenderAstros(Galaxy(2100));
        var source = new[]
        {
            new SPACEOBJECT(),
            new SPACEOBJECT { objId = 1, astroId = 204901, posx = 17, rotw = 1 },
            new SPACEOBJECT { objId = 2, astroId = 1000001, posy = 29, rotw = 1 },
            new SPACEOBJECT { objId = 3, astroId = 101, posz = 31, rotw = 1 }
        };
        var original = source.ToArray();
        var copy = map.CopyForUpload(source, 0, source.Length);
        CollectionAssert.AreEqual(original, source);
        Check.AreNotSame(source, copy);
        Check.AreEqual(map.RenderId(204901), copy[1].astroId);
        Check.AreEqual(17f, copy[1].posx);
        Check.AreEqual(source[2], copy[2]); Check.AreEqual(source[3], copy[3]);
        source[1] = new SPACEOBJECT();
        Check.AreEqual(default(SPACEOBJECT), map.CopyForUpload(source, 1, 1)[1]);
        source[1] = new SPACEOBJECT { objId = 8, astroId = 210006, posx = 42 };
        copy = map.CopyForUpload(source, 1, 1);
        Check.AreEqual(map.RenderId(210006), copy[1].astroId);
        Check.AreEqual(42f, copy[1].posx);
        Check.AreEqual(210006u, source[1].astroId);
        Array.Resize(ref source, 8);
        copy = map.CopyForUpload(source, 0, source.Length);
        Check.AreEqual(map.RenderId(210006), copy[1].astroId);
        Check.AreEqual(default(SPACEOBJECT), copy[7]);
    }

    [TestMethod]
    public void NewGalaxyBuildsItsOwnAliasesAndSmallWorldsUseNativeUploads()
    {
        Check.IsNull(GalaxyRenderAstros.For(Galaxy(2048)));
        var first = Galaxy(2100); var second = Galaxy(2100);
        second.stars[0].planets = Array.Empty<PlanetData>();
        var a = GalaxyRenderAstros.For(first); var b = GalaxyRenderAstros.For(second);
        Check.AreSame(a, GalaxyRenderAstros.For(first));
        Check.AreNotSame(a, b);
        Check.AreNotEqual(a.RenderId(204900), b.RenderId(204900));
    }

    [TestMethod]
    public void RealUploadAndCameraBodiesAreRewrittenAtTheirNativeCalls()
    {
        var nativeUploads = typeof(ComputeBuffer).GetMethods().Where(m => m.Name == nameof(ComputeBuffer.SetData)).ToArray();
        foreach (var method in GalaxyRenderUpload_Patch.TargetMethods())
        {
            var original = PatchProcessor.GetOriginalInstructions(method).ToList();
            var count = original.Count(i => nativeUploads.Any(i.Calls));
            Check.IsGreaterThan(0, count, method.Name);
            var patched = GalaxyRenderUpload_Patch.Transpiler(original).ToArray();
            Check.AreEqual(0, patched.Count(i => nativeUploads.Any(i.Calls)), method.Name);
            Check.AreEqual(count, patched.Count(i => i.operand is MethodInfo m && m.DeclaringType == typeof(GalaxyRenderAstros)), method.Name);
        }
        var camera = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(StarmapCamera), "LateUpdate"));
        var result = StarmapCameraPose_Patch.Transpiler(camera).ToArray();
        Check.AreEqual(0, result.Count(i => i.Calls(AccessTools.Method(typeof(StarmapCamera), nameof(StarmapCamera.RefreshCameraPose)))));
        Check.AreEqual(1, result.Count(i => i.Calls(AccessTools.Method(typeof(StarmapCameraPose_Patch), nameof(StarmapCameraPose_Patch.Refresh)))));
        var prefix = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(StarmapDisplayFrame_Patch), "Prefix")).ToList();
        var refresh = prefix.FindIndex(i => i.Calls(AccessTools.Method(typeof(StarmapCameraPose_Patch), nameof(StarmapCameraPose_Patch.Refresh))));
        var project = prefix.FindIndex(i => i.Calls(AccessTools.Method(typeof(StarmapDisplay_Patch), nameof(StarmapDisplay_Patch.BeginFrame))));
        Check.IsTrue(refresh >= 0 && project > refresh, "Camera must settle before the projection cache reads its matrices.");
    }

    private static GalaxyData Galaxy(int count) => new()
    {
        starCount = count,
        stars = Enumerable.Range(1, count).Select(id => new StarData
        {
            id = id,
            index = id - 1,
            planetCount = 6,
            planets = Enumerable.Range(1, 6).Select(p => new PlanetData { id = id * 100 + p }).ToArray()
        }).ToArray()
    };
}
