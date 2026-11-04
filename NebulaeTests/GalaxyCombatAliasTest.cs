using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld.Combat;
using UnityEngine;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class GalaxyCombatAliasTest
{
    private const int HighPlanet = 210001;
    private const int HighStar = 210000;

    [TestMethod]
    public void HighAstroIdsFromCombatParticlesAndStatsAreMirroredForTheGpu()
    {
        var map = GalaxyRenderAstros.For(Galaxy(2100));
        Check.IsNotNull(map);
        var alias = (int)map!.RenderId(HighPlanet);
        Check.AreNotEqual(HighPlanet, alias);
        Check.IsLessThanOrEqualTo(GalaxyRenderAstros.ShaderMaxAstroId, alias, "The shader only accepts this range.");

        var particles = new[]
        {
            new ParticleData { id = 1, astroId = HighPlanet, pos = new Vector3(1, 2, 3) },
            new ParticleData { id = 2, astroId = 0, upos = new VectorLF3(7, 8, 9) },
            new ParticleData { id = 3, astroId = 1032768 },
            new ParticleData { id = 4, astroId = 101 }
        };
        var copy = CombatAstroAliases.CopyParticles(particles, 0, particles.Length, map);
        Check.AreEqual(alias, copy[0].astroId);
        Check.AreEqual(HighPlanet, particles[0].astroId, "The native pool must keep the real id.");
        Check.AreEqual(new Vector3(1, 2, 3), copy[0].pos);
        Check.AreEqual(0, copy[1].astroId, "World-frame particles stay world-frame.");
        Check.AreEqual(1032768, copy[2].astroId, "Hive astros keep their own classification.");
        Check.AreEqual(101, copy[3].astroId, "Low astro ids must not change.");
        Check.AreSame(copy, CombatAstroAliases.CopyParticles(particles, 0, 2, map), "One copy per source array.");

        var bursts = new[] { new GeneralShieldBurst { id = 1, astroId = HighPlanet }, new GeneralShieldBurst { id = 2, astroId = 101 } };
        var burstCopy = CombatAstroAliases.CopyShieldBursts(bursts, 0, bursts.Length, map);
        Check.AreEqual(alias, burstCopy[0].astroId);
        Check.AreEqual(101, burstCopy[1].astroId);
        Check.AreEqual(HighPlanet, bursts[0].astroId);

        var stats = new[]
        {
            new CombatStat { id = 1, astroId = HighPlanet, originAstroId = HighStar },
            new CombatStat { id = 2, astroId = 101, originAstroId = 101 },
            new CombatStat { id = 3, astroId = 0, originAstroId = 1000001 }
        };
        var statCopy = CombatAstroAliases.CopyCombatStats(stats, map);
        Check.AreEqual(alias, statCopy[0].astroId);
        Check.AreEqual((int)map.RenderId(HighStar), statCopy[0].originAstroId);
        Check.AreEqual(101, statCopy[1].astroId);
        Check.AreEqual(0, statCopy[2].astroId);
        Check.AreEqual(1000001, statCopy[2].originAstroId);
        Check.AreEqual(HighPlanet, stats[0].astroId, "The native combat stats must keep the real id.");

        Check.IsNull(GalaxyRenderAstros.For(Galaxy(2048)), "Vanilla-sized galaxies need no mirror.");
    }

    [TestMethod]
    public void ReplicatedMissileVelocityKeepsNativeUnits()
    {
        var half = (float)(Math.PI / 4);
        var relativeRot = new Quaternion(0, (float)Math.Sin(half), 0, (float)Math.Cos(half));
        var rotation = new Quaternion(-relativeRot.x, -relativeRot.y, -relativeRot.z, relativeRot.w);
        var universalVelocity = new Vector3(60, -120, 30);
        var actual = BattleEffectMotion.MissileVelocity(rotation, universalVelocity);
        var expected = Maths.QInvRotate(relativeRot, universalVelocity);
        Check.AreEqual(expected.x, actual.x, 1e-4f);
        Check.AreEqual(expected.y, actual.y, 1e-4f);
        Check.AreEqual(expected.z, actual.z, 1e-4f);
        Check.AreEqual(universalVelocity.magnitude, actual.magnitude, 1e-3f, "Rotation must not rescale the native per-second velocity.");
    }

    [TestMethod]
    public void EveryCombatInstanceUploadIsRewrittenToTheAliasingCopy()
    {
        var setRangeData = AccessTools.Method(typeof(ComputeBuffer), nameof(ComputeBuffer.SetData),
            new[] { typeof(Array), typeof(int), typeof(int), typeof(int) });
        var setData = AccessTools.Method(typeof(ComputeBuffer), nameof(ComputeBuffer.SetData), new[] { typeof(Array) });
        foreach (var method in ParticleInstanceUpload_Patch.TargetMethods())
        {
            var original = PatchProcessor.GetOriginalInstructions(method).ToList();
            // The instance upload is the ranged overload; the argument buffer still uses the
            // single-array overload and must stay untouched.
            var uploads = original.Count(command => command.Calls(setRangeData));
            Check.AreEqual(1, uploads, method.DeclaringType + "." + method.Name);
            var patched = ParticleInstanceUpload_Patch.Transpiler(original, method).ToArray();
            Check.AreEqual(0, patched.Count(command => command.Calls(setRangeData)), method.Name);
            Check.AreEqual(uploads, patched.Count(command => command.operand is MethodInfo info && info.DeclaringType == typeof(CombatAstroAliases)), method.Name);
            Check.AreEqual(1, patched.Count(command => command.Calls(setData)), "The indirect argument upload must remain.");
        }
        var update = AccessTools.Method(typeof(CombatStatRenderer), nameof(CombatStatRenderer.Update));
        var statOriginal = PatchProcessor.GetOriginalInstructions(update).ToList();
        Check.AreEqual(1, statOriginal.Count(command => command.Calls(setData)));
        var statPatched = CombatStatUpload_Patch.Transpiler(statOriginal).ToArray();
        Check.AreEqual(0, statPatched.Count(command => command.Calls(setData)));
        Check.AreEqual(1, statPatched.Count(command => command.operand is MethodInfo info && info.DeclaringType == typeof(CombatAstroAliases)));
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
