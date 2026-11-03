using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using UnityEngine;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class GalaxyOptimizationTest
{
    [TestMethod]
    public void PurePoseCacheRequiresExactNativeArgumentsIncludingSignedZero()
    {
        var state = new PlanetPoseReuse();
        Check.IsFalse(state.TryOrbit(1, out _, out _));
        Check.IsFalse(state.TrySpin(1, out _));
        var axis = new Quaternion(.1f, .2f, .3f, .9f);
        state.Capture(1, Math.Cos(1), Math.Sin(1), 2, axis);
        Check.IsTrue(state.TryOrbit(1, out var cosine, out var sine));
        Check.AreEqual(Math.Cos(1), cosine); Check.AreEqual(Math.Sin(1), sine);
        Check.IsTrue(state.TrySpin(2, out var rotation));
        Check.IsTrue(PoseBits.Equal(axis, rotation));
        Check.IsFalse(state.TryOrbit(1.000000000000001, out _, out _));
        Check.IsFalse(state.TrySpin(2.000001f, out _));
        state.Capture(0, 1, 0, 0, Quaternion.identity);
        var minusZero = BitConverter.ToSingle(new byte[] { 0, 0, 0, 128 }, 0);
        Check.IsFalse(state.TrySpin(minusZero, out _));
        Check.IsFalse(state.TryOrbit(BitConverter.Int64BitsToDouble(long.MinValue), out _, out _));
    }

    [TestMethod]
    public void SpatialCandidatesConservativelyContainEveryNativeSphereHit()
    {
        var random = new System.Random(881);
        var galaxy = new GalaxyData { starCount = 8191, stars = new StarData[8191] };
        for (var i = 0; i < galaxy.stars.Length; i++) galaxy.stars[i] = new StarData
        {
            id = i + 1,
            uPosition = new VectorLF3((random.NextDouble() - .5) * 1e9, (random.NextDouble() - .5) * 1e9, (random.NextDouble() - .5) * 1e9)
        };
        var grid = new GalaxySpatialIndex(galaxy);
        var candidates = new List<int>();
        foreach (var rotation in new[] { Quaternion.identity, Yaw(17), Yaw(179) })
            for (var trial = 0; trial < 100; trial++)
            {
                var star = galaxy.stars[random.Next(galaxy.starCount)];
                var origin = star.uPosition + new VectorLF3(3000000, -100000, 15000);
                var localOrigin = new Vector3(19, 7, -31);
                var direction = new Vector3(0, 0, 1);
                var length = trial % 2 == 0 ? 0 : 15000;
                Check.IsTrue(grid.Query(origin + Maths.QRotateLF(rotation, (VectorLF3)localOrigin),
                    Maths.QRotateLF(rotation, (VectorLF3)direction), length, candidates));
                var set = candidates.ToHashSet();
                foreach (var item in galaxy.stars)
                {
                    Vector3 center = Maths.QInvRotateLF(rotation, item.uPosition - origin);
                    if (Phys.RayCastSphere(localOrigin, direction, length, center, 3600000))
                        Check.Contains(item.id, set, $"Missing star {item.id}, trial {trial}");
                }
                CollectionAssert.AreEqual(candidates.OrderBy(i => i).ToArray(), candidates);
            }
        Check.IsFalse(grid.Query(new VectorLF3(double.NaN, 0, 0), default, 1, candidates));
        Check.IsFalse(grid.Query(default, default, double.PositiveInfinity, candidates));
        Check.IsFalse(grid.Query(new VectorLF3(1e15, 0, 0), default, 1, candidates));
    }

    [TestMethod]
    public void CandidateSequencesRefreshAfterDeletionAndSlotReuse()
    {
        var enabled = GalaxyOptimization.Enabled;
        var oldData = GameMain.data;

        try
        {
            var original = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(SpaceColliderLogic), nameof(SpaceColliderLogic.RaycastAllColliders)));
            var patched = SpaceRayCandidates_Patch.Transpiler(original).ToArray();
            Check.AreEqual(2, patched.Count(i => i.Calls(AccessTools.Method(typeof(SpaceRayCandidates), nameof(SpaceRayCandidates.NextHive)))));
            Check.AreEqual(2, patched.Count(i => i.Calls(AccessTools.Method(typeof(SpaceRayCandidates), nameof(SpaceRayCandidates.NextStar)))));
            var game = (GameData)FormatterServices.GetUninitializedObject(typeof(GameData));
            var galaxy = new GalaxyData { starCount = 256, stars = new StarData[256] };
            for (var i = 0; i < 256; i++) galaxy.stars[i] = new StarData
            {
                id = i + 1,
                index = i,
                uPosition = new VectorLF3(i * 8000000.0, 0, 0),
                planets = Array.Empty<PlanetData>()
            };
            game.galaxy = galaxy; game.relativeRot = Quaternion.identity; GameMain.data = game;
            var model = (SectorModel)FormatterServices.GetUninitializedObject(typeof(SectorModel));
            var sector = new SpaceSector
            {
                maxHiveCount = 2050,
                dynamicAstroIdBase = 1002050,
                astroCursor = 2051,
                astros = new AstroData[4096],
                galaxyAstros = new AstroData[25700],
                dfHivesByAstro = new EnemyDFHiveSystem[2050],
                dfHives = new EnemyDFHiveSystem[256],
                enemyPool = new EnemyData[1],
                model = model
            };
            model.sector = sector; model.gameData = game;
            model.astroArr = new AstroPoseR[4096]; model.galaxyAstroArr = new AstroPoseR[25700];
            var logic = new SpaceColliderLogic { gameData = game, customCastAll = new RaycastData[512] };
            AccessTools.Field(typeof(SpaceColliderLogic), "<sector>k__BackingField").SetValue(logic, sector);
            AccessTools.Field(typeof(SpaceColliderLogic), "activeColHashes").SetValue(logic, new int[768]);
            AccessTools.Field(typeof(SpaceColliderLogic), "colChunks").SetValue(logic, new ColliderContainerLF[32768]);
            for (var i = 1; i <= 256; i++) model.galaxyAstroArr[i * 100] = new AstroPoseR
            { radius = 100, rrot = Quaternion.identity, rpos = galaxy.stars[i - 1].uPosition };
            foreach (var id in new[] { 1, 37, 2049, 1 })
            {
                Array.Clear(model.astroArr, 0, model.astroArr.Length);
                model.astroArr[id] = new AstroPoseR { radius = 100, rrot = Quaternion.identity, rpos = new Vector3(0, 0, 1000) };
                foreach (var distance in new[] { 0f, 15000f, 2000000f })
                    foreach (var physics in new[] { false, true })
                    {
                        var ray = new Ray(new Vector3(0, 0, -500), Vector3.forward);
                        GalaxyOptimization.Enabled = true;
                        SpaceRayCandidates.Observe(model);
                        SpaceRayCandidates.Prepare(logic, ray, distance);
                        var actual = new List<int>();
                        for (var i = SpaceRayCandidates.NextHive(0, logic); i < sector.maxHiveCount; i = SpaceRayCandidates.NextHive(i, logic)) actual.Add(i);
                        CollectionAssert.AreEqual(new[] { id }, actual);
                        var stars = new List<int>();
                        for (var i = SpaceRayCandidates.NextStar(0, logic); i <= galaxy.starCount; i = SpaceRayCandidates.NextStar(i, logic)) stars.Add(i);
                        Check.Contains(1, stars);
                        CollectionAssert.AreEqual(stars.OrderBy(i => i).ToArray(), stars);
                        GalaxyOptimization.Enabled = false;
                        Check.AreEqual(2, SpaceRayCandidates.NextHive(1, logic));
                        Check.AreEqual(2, SpaceRayCandidates.NextStar(1, logic));
                    }
            }
        }
        finally { GalaxyOptimization.Enabled = enabled; GameMain.data = oldData; }
    }

    [TestMethod]
    public void HoverCandidatesPreserveEveryNativeCursorOutcome()
    {
        var random = new System.Random(20261031);
        const int count = 8191;
        var points = new Vector3[count];
        for (var i = 0; i < count; i++) points[i] = new Vector3(
            (float)(random.NextDouble() * 2560 - 20), (float)(random.NextDouble() * 1600 - 20),
            (float)(random.NextDouble() * 300 - 0.05));
        points[7].z = 0.05f;   // Behind the native depth gate
        points[11].z = 0.1f;   // Exactly on the native depth gate
        var order = Enumerable.Range(0, count).ToArray();
        var buffer = new int[count];
        var probes = new List<Vector3>();
        for (var trial = 0; trial < 40; trial++)
            probes.Add(new Vector3((float)(random.NextDouble() * 2560), (float)(random.NextDouble() * 1600), 0f));
        foreach (var index in new[] { 7, 11, 123, 4096, 8190 })
        {
            var point = points[index];
            probes.Add(new Vector3(point.x, point.y, 0f));                                   // Direct hit
            probes.Add(new Vector3(point.x + 24f, point.y, 0f));                             // Inside the radius
            probes.Add(new Vector3(point.x + 24.6f, point.y, 0f));                           // Outside the radius
            probes.Add(new Vector3(point.x + 24.4949f, point.y, 0f));                        // Near 600 squared
        }
        foreach (var mouse in probes)
        {
            var expected = HoverScan(points, order, count, mouse);
            var found = StarmapDisplay_Patch.FilterHover(points, count, mouse, buffer);
            var actual = HoverScan(points, buffer, found, mouse);
            Check.AreEqual(expected.Best, actual.Best, $"Selected star differs at {mouse}");
            Check.AreEqual(expected.Distance, actual.Distance, $"Distance threshold differs at {mouse}");
            Check.AreEqual(expected.Depth, actual.Depth, $"Depth threshold differs at {mouse}");
            var ordered = new int[found];
            Array.Copy(buffer, ordered, found);
            CollectionAssert.AreEqual(ordered.OrderBy(index => index).ToArray(), ordered, "Candidates must keep native order.");
            var members = new bool[count];
            for (var i = 0; i < found; i++) members[buffer[i]] = true;
            for (var i = 0; i < count; i++)
            {
                var dx = points[i].x - mouse.x; var dy = points[i].y - mouse.y;
                Check.AreEqual(dx * dx + dy * dy < StarmapDisplay_Patch.HoverRadiusSquared, members[i],
                    $"Star {i} candidate membership differs at {mouse}");
            }
        }
    }

    /// <summary>The native cursor loop, iterating the given order exactly as shipped.</summary>
    private static (int Best, float Distance, float Depth) HoverScan(Vector3[] points, int[] order, int count, Vector3 mouse)
    {
        var position = new Vector3(mouse.x, mouse.y, 0f);
        var best = -1;
        var distance = StarmapDisplay_Patch.HoverRadiusSquared;
        var depth = float.MaxValue;
        for (var k = 0; k < count; k++)
        {
            var index = order[k];
            var screen = points[index];
            var z = screen.z;
            if (z < 0.1f) continue;
            screen.z = 0f;
            var squared = (screen - position).sqrMagnitude;
            if (squared < distance && z < depth) { distance = squared; depth = z; best = index; }
        }
        return (best, distance, depth);
    }
}

