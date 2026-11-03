using System.Reflection;
using HarmonyLib;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class ShieldBackendTest
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static Type Mod(string name) => typeof(NebulaePatcher.NebulaePlugin).Assembly.GetType("NebulaePatcher.Patches.Misc." + name, true)!;
    private static readonly Vector3[] Source =
    [
        new Vector3(0, 1, 0),
        new Vector3(0, -1, 0),
        new Vector3(-1, 0, 0),
        new Vector3(1, 0, 0),
        new Vector3(0, 0, 1),
        new Vector3(0, 0, -1)
    ];

    private static (Vector3[] vertices, Vector3[] normals, uint[] args) Cpu(Vector4[] generators, int count)
    {
        var vertices = new Vector3[Source.Length];
        var normals = new Vector3[Source.Length];
        var args = Enumerable.Repeat(999u, 10).ToArray();
        Mod("ShieldCpuKernel").GetMethod("Compute", Static)!.Invoke(null,
            [Source, generators, count, 200f, 60.8f, 0.05f, 0.5f, vertices, normals, args]);
        return (vertices, normals, args);
    }

    [TestMethod]
    public void CpuOffGeneratorsProduceEmptyCoverageAndClearAllCounters()
    {
        var result = Cpu([new Vector4(0, 200, 0, 0)], 1);
        CollectionAssert.AreEqual(new uint[10], result.args);
        for (var i = 0; i < Source.Length; i++)
        {
            TestAssert.AreEqual(9.6f, result.vertices[i].magnitude, 0.00001f);
            TestAssert.AreEqual(1f, result.normals[i].magnitude, 0.00001f);
        }
    }

    [TestMethod]
    public void CpuRespondsToGeneratorPositionAndPowerWithoutSphericalShortcut()
    {
        var north = Cpu([new Vector4(0, 200, 0, 1)], 1);
        var south = Cpu([new Vector4(0, -200, 0, 1)], 1);
        var weak = Cpu([new Vector4(0, 200, 0, 0.5f)], 1);
        TestAssert.IsGreaterThan(0u, north.args[0]);
        TestAssert.IsLessThan((uint)Source.Length * 1000, north.args[0]);
        TestAssert.IsGreaterThan(north.vertices[1].magnitude, north.vertices[0].magnitude);
        TestAssert.AreEqual(north.args[0], south.args[0]);
        TestAssert.AreEqual(north.vertices[0].magnitude, south.vertices[1].magnitude, 0.00001f);
        TestAssert.IsLessThan(north.args[0], weak.args[0]);
    }

    [TestMethod]
    public void CpuRejectsNonfiniteInputsAndOversizedGeneratorCount()
    {
        var error = TestAssert.ThrowsExactly<TargetInvocationException>(() => Cpu([new Vector4(float.NaN, 0, 0, 1)], 1));
        TestAssert.IsInstanceOfType<ArgumentException>(error.InnerException);
        error = TestAssert.ThrowsExactly<TargetInvocationException>(() => Cpu(new Vector4[81], 81));
        TestAssert.IsInstanceOfType<ArgumentException>(error.InnerException);
    }

    [TestMethod]
    [DataRow(null, "Auto")]
    [DataRow("", "Auto")]
    [DataRow("   ", "Auto")]
    [DataRow("auto", "Auto")]
    [DataRow(" NATIVE ", "Native")]
    [DataRow("cpu", "Cpu")]
    public void BackendConfigurationAcceptsSupportedModes(string? value, string expected)
    {
        var parse = Mod("HeadlessShieldBackend").GetMethod("ParseMode", Static)!;
        TestAssert.AreEqual(expected, parse.Invoke(null, [value])!.ToString());
    }

    [TestMethod]
    [DataRow("unity")]
    [DataRow(" UNITY ")]
    [DataRow("natvie")]
    public void BackendConfigurationRejectsRemovedModeAndTypos(string value)
    {
        var parse = Mod("HeadlessShieldBackend").GetMethod("ParseMode", Static)!;
        var error = TestAssert.ThrowsExactly<TargetInvocationException>(() => parse.Invoke(null, [value]));
        TestAssert.IsInstanceOfType<ArgumentException>(error.InnerException);
        TestAssert.AreEqual("NEBULAE_SHIELD_BACKEND must be auto, native or cpu.", error.InnerException!.Message);
    }

    [TestMethod]
    public void NullRendererRestoresVerifiedMaterialParameterWithoutReplacingCoverage()
    {
        var resolve = Mod("HeadlessShieldBackend").GetMethod("ResolveBlend", Static)!;
        TestAssert.AreEqual(2.1f, resolve.Invoke(null, [0f, true, null]));
        foreach (var nullRenderer in new[] { false, true })
        {
            TestAssert.AreEqual(1.7f, resolve.Invoke(null, [1.7f, nullRenderer, null]));
            TestAssert.AreEqual(3.2f, resolve.Invoke(null, [0f, nullRenderer, "3.2"]));
            TestAssert.AreEqual(3.2f, resolve.Invoke(null, [1.7f, nullRenderer, "3.2"]));
        }
        var invalid = TestAssert.ThrowsExactly<TargetInvocationException>(() => resolve.Invoke(null, [0f, false, null]));
        TestAssert.IsInstanceOfType<InvalidOperationException>(invalid.InnerException);
    }

    [TestMethod]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("invalid")]
    public void BlendOverrideRejectsNonfiniteAndNonpositiveValues(string value)
    {
        var resolve = Mod("HeadlessShieldBackend").GetMethod("ResolveBlend", Static)!;
        var error = TestAssert.ThrowsExactly<TargetInvocationException>(() => resolve.Invoke(null, [0f, true, value]));
        TestAssert.IsInstanceOfType<ArgumentException>(error.InnerException);
    }

    [TestMethod]
    [DataRow(12, 12)]
    [DataRow(10, 4)]
    public void PhysicsBufferReplacementDoesNotAllocateUnityResources(int count, int stride)
    {
        var create = Mod("HeadlessShieldBackend").GetMethod("CreatePhysicsBuffer", Static)!;
        TestAssert.IsNull(create.Invoke(null, [count, stride, ComputeBufferType.Default]));
    }

    [TestMethod]
    public void ShieldTransportReplacementPreservesOriginalGameplayTail()
    {
        var method = AccessTools.Method(typeof(PlanetATField), "RecalculatePhysicsShape");
        var original = PatchProcessor.GetOriginalInstructions(method);
        var result = ((IEnumerable<CodeInstruction>)Mod("HeadlessShieldPipeline").GetMethod("Recalculate", Static)!
            .Invoke(null, [original])!).ToList();
        var lastReadback = original.FindLastIndex(i => i.operand is MethodInfo call && call.Name == "GetData");
        var backendCall = result.FindIndex(i => i.operand is MethodInfo call && call.DeclaringType == Mod("HeadlessShieldBackend"));
        TestAssert.IsGreaterThan(0, backendCall);
        var expectedTail = original.Skip(lastReadback + 1).ToArray();
        var actualTail = result.Skip(backendCall + 1).ToArray();
        TestAssert.HasCount(expectedTail.Length, actualTail);
        for (var i = 0; i < expectedTail.Length; i++)
        {
            TestAssert.AreEqual(expectedTail[i].opcode, actualTail[i].opcode);
            TestAssert.AreEqual(expectedTail[i].operand, actualTail[i].operand);
            CollectionAssert.AreEqual(expectedTail[i].labels, actualTail[i].labels);
            CollectionAssert.AreEqual(expectedTail[i].blocks, actualTail[i].blocks);
        }
        TestAssert.IsFalse(result.Any(i => i.operand is MethodInfo call && call.DeclaringType == typeof(ComputeShader)));
    }

    [TestMethod]
    public void PhysicsMeshCreationIsPreservedWhileOnlyTwoGpuAllocationsChange()
    {
        var original = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(PlanetATField), "CreatePhysics"));
        var result = ((IEnumerable<CodeInstruction>)Mod("HeadlessShieldPipeline").GetMethod("CreatePhysics", Static)!
            .Invoke(null, [original])!).ToList();
        TestAssert.HasCount(original.Count, result);
        var changes = 0;
        for (var i = 0; i < original.Count; i++)
        {
            CollectionAssert.AreEqual(original[i].labels, result[i].labels);
            if (Equals(original[i].operand, result[i].operand)) TestAssert.AreEqual(original[i].opcode, result[i].opcode);
            else
            {
                TestAssert.AreEqual(typeof(ComputeBuffer), ((ConstructorInfo)original[i].operand).DeclaringType);
                TestAssert.AreEqual("CreatePhysicsBuffer", ((MethodInfo)result[i].operand).Name);
                changes++;
            }
        }
        TestAssert.AreEqual(2, changes);
    }
}
