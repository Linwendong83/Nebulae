global using NebulaeTests.Fixtures;
global using NebulaePatcher.Patches.Dynamic.Combat;
global using NebulaePatcher.Patches.Dynamic.Factory;
global using NebulaePatcher.Patches.Dynamic.Game;
global using NebulaePatcher.Patches.Dynamic.UI;
global using NebulaePatcher.Patches.Dynamic.World;
global using Microsoft.VisualStudio.TestTools.UnitTesting;
global using static NebulaeTests.TestSupport;
using HarmonyLib;
using System.Reflection;
using global::NebulaeModel.DataStructures;
using global::NebulaeModel.Networking;
using global::NebulaeModel.Networking.Serialization;
using global::NebulaeModel.Packets.Authority;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

[assembly: DoNotParallelize]

[TestClass]
public class AssemblyInitializer
{
    [AssemblyInitialize]
    public static void Init(TestContext context)
    {
        NebulaeModel.Logger.Log.Init(new DummyLogger());
        NebulaeTests.TestSupport.EnsureUsableTempPath();
    }

    private class DummyLogger : NebulaeModel.Logger.ILogger
    {
        public void LogDebug(object data) { }
        public void LogInfo(object data) { }
        public void LogWarning(object data) { }
        public void LogError(object data) { }
    }
}

namespace NebulaeTests
{
    internal static class TestSupport
    {
        /// <summary>
        /// Points TEMP/TMP at a writable directory beside the test binary when the inherited temp
        /// directory refuses writes.
        /// </summary>
        /// <remarks>
        /// Several fixtures write journals, spools and communicator state through
        /// <see cref="Path.GetTempPath"/>. Locked-down hosts (and spawned processes under a sandbox)
        /// can deny writes there, which turned every such fixture into an
        /// <c>UnauthorizedAccessException</c> instead of a test result. tools/test.ps1 already points
        /// TEMP at dist/artifacts/tests/tmp; this keeps a direct dotnet test / exe invocation equally
        /// usable instead of reporting a wall of unrelated failures.
        /// </remarks>
        internal static void EnsureUsableTempPath()
        {
            if (CanWriteTemp(Path.GetTempPath())) return;
            var fallback = Path.Combine(AppContext.BaseDirectory, "tmp");
            try
            {
                Directory.CreateDirectory(fallback);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"Neither the inherited temp directory nor '{fallback}' is writable for this test run.", error);
            }
            if (!CanWriteTemp(fallback))
                throw new InvalidOperationException($"The test fallback temp directory '{fallback}' is not writable.");
            Environment.SetEnvironmentVariable("TEMP", fallback);
            Environment.SetEnvironmentVariable("TMP", fallback);
        }

        private static bool CanWriteTemp(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return false;
            var probe = Path.Combine(directory, "nebulae-temp-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (File.Create(probe)) { }
                File.Delete(probe);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static IEnumerable<(MethodBase? Target, MethodInfo Patch)> DeclaredPatches(Type patchClass)
        {
            var processor = new Harmony("nebulae.tests.patch-inspection").CreateClassProcessor(patchClass);
            var patches = (System.Collections.IEnumerable)AccessTools.Field(typeof(PatchClassProcessor), "patchMethods").GetValue(processor);
            var dynamicTargets = AccessTools.DeclaredMethod(patchClass, "TargetMethods");
            var targets = dynamicTargets == null ? null : ((IEnumerable<MethodBase>)dynamicTargets.Invoke(null, null)).ToArray();
            var patchTools = typeof(Harmony).Assembly.GetType("HarmonyLib.PatchTools", true)!;
            var resolve = AccessTools.Method(patchTools, "GetOriginalMethod");
            foreach (var patch in patches)
            {
                var info = (HarmonyMethod)AccessTools.Field(patch.GetType(), "info").GetValue(patch);
                if (targets == null) yield return ((MethodBase?)resolve.Invoke(null, new object[] { info }), info.method);
                else foreach (var target in targets) yield return (target, info.method);
            }
        }

        internal static byte[] SerializePlayer(PlayerData player)
        {
            var writer = new NetDataWriter();
            player.Serialize(writer);
            return writer.CopyData();
        }

        internal static byte[] Compress(byte[] bytes)
        {
            using var writer = new BinaryUtils.Writer();
            writer.BinaryWriter.Write(bytes);
            return writer.CloseAndGetBytes();
        }

        // Unity's Quaternion.Euler and Quaternion.ToString cannot run in the test host.
        internal static Quaternion Yaw(float degrees)
        {
            var half = degrees * ((float)Math.PI / 180f) * 0.5f;
            return new Quaternion(0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half));
        }

        internal static void AssertSameRotation(Quaternion expected, Quaternion actual)
        {
            TestAssert.AreEqual(expected.x, actual.x, 1e-6f);
            TestAssert.AreEqual(expected.y, actual.y, 1e-6f);
            TestAssert.AreEqual(expected.z, actual.z, 1e-6f);
            TestAssert.AreEqual(expected.w, actual.w, 1e-6f);
        }

        internal static byte[] Encoded(GroundEnemyState state)
        {
            TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(state, out var data), "A defined kind must encode.");
            return data;
        }

        internal static byte[] Encoded(SpaceEnemyState state)
        {
            TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(state, out var data), "A defined kind must encode.");
            return data;
        }

        internal static byte[] Encoded(CraftState state)
        {
            TestAssert.IsTrue(CraftStateCodec.TryEncode(state, out var data), "A craft state must encode.");
            return data;
        }

        internal static byte[] Encoded(FactoryCombatState state)
        {
            TestAssert.IsTrue(FactoryCombatStateCodec.TryEncode(state, out var data));
            return data;
        }

        internal static T Uninitialized<T>() =>
            (T)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(T));

        internal static void SetBackingField(object instance, string property, object? value) =>
            SetField(instance, "<" + property + ">k__BackingField", value);

        internal static void SetField(object instance, string field, object? value) =>
            AccessTools.Field(instance.GetType(), field).SetValue(instance, value);

        internal static void SetStatic(Type type, string field, object? value) =>
            AccessTools.Field(type, field).SetValue(null, value);

        internal static T RoundTrip<T>(T sent,
            Action<NebulaeNetPacketProcessor>? configure = null)
            where T : class, new()
        {
            // Reusable handlers and their arrays must never leak between round trips.
            var processor = new NebulaeNetPacketProcessor();
            configure?.Invoke(processor);
            T received = null!;
            processor.SubscribeReusable<T>(packet => received = packet);
            processor.ReadPacket(new NetDataReader(processor.Write(sent)));
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNotNull(received);
            return received;
        }
    }
}
