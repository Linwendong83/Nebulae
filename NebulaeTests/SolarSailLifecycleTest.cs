using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeNetwork.PacketProcessors.Universe;
using NebulaePatcher.Patches.Misc;
using NebulaeModel.Utils;
using NebulaeWorld;
using NebulaeWorld.Universe;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
[SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Native boundary adapters retain the original call signatures.")]
public class SolarSailLifecycleTest
{
    private Harmony harmony = null!;
    private GameMain? previousMain;
    private MultiplayerSession? previousSession;
    private GameData? previousData;

    [TestInitialize]
    public void Initialize()
    {
        previousMain = GameMain.instance; previousData = GameMain.data; previousSession = Multiplayer.Session;
        var main = Uninitialized<GameMain>();
        SetField(main, "m_CachedPtr", new IntPtr(1));
        SetStatic(typeof(GameMain), "<instance>k__BackingField", main);
        Multiplayer.Session = null;
        GameMain.data = new GameData { gameDesc = new GameDesc(), history = new GameHistoryData() };
        harmony = new Harmony("nebulae.tests.solar-sail-lifecycle");
        foreach (var name in new[] { "AddSolarSail", "RemoveSolarSail", "AbsorbSail", "OnNodeRemove", "Export", "Import", "RemoveSailsByOrbit" })
        {
            harmony.Patch(AccessTools.Method(typeof(DysonSwarm), name),
                transpiler: new HarmonyMethod(typeof(HeadlessDysonSwarm), "Transport"));
            harmony.Patch(AccessTools.Method(typeof(DysonSwarm), name),
                transpiler: new HarmonyMethod(typeof(SolarSailLifecycleTest), nameof(ManagedIO)));
        }
        Harmony.ReversePatch(AccessTools.Method(typeof(DysonSwarm), "GameTick"),
            new HarmonyMethod(typeof(SolarSailLifecycleTest), nameof(NativeTick)),
            AccessTools.Method(typeof(HeadlessDysonSwarm), "Tick"), null);
        Harmony.ReversePatch(AccessTools.Method(typeof(DysonSwarm), "Export"),
            new HarmonyMethod(typeof(SolarSailLifecycleTest), nameof(ExportLegacy)),
            AccessTools.Method(typeof(SolarSailLifecycleTest), nameof(LegacyExportBody)), null);
        harmony.Patch(AccessTools.Method(typeof(DysonSwarm), "RemoveSailsByOrbit"),
            postfix: new HarmonyMethod(typeof(HeadlessDysonSwarm), "Compact"));
        harmony.Patch(AccessTools.Method(typeof(DysonSwarm), "Export"),
            prefix: new HarmonyMethod(typeof(HeadlessDysonSwarm), "BeforeExport"));
        harmony.Patch(AccessTools.Method(typeof(DysonSwarm), "Import"),
            postfix: new HarmonyMethod(typeof(HeadlessDysonSwarm), "AfterImport"));
        harmony.Patch(AccessTools.PropertyGetter(typeof(GameMain), nameof(GameMain.sandboxToolsEnabled)),
            prefix: new HarmonyMethod(typeof(SolarSailLifecycleTest), nameof(NoSandboxTools)));
    }

    [TestCleanup]
    public void Cleanup()
    {
        harmony.UnpatchSelf(); Multiplayer.Session = previousSession;
        GameMain.data = previousData;
        SetStatic(typeof(GameMain), "<instance>k__BackingField", previousMain);
    }

    private static bool NoSandboxTools(ref bool __result) { __result = false; return false; }
    private static void NativeTick(DysonSwarm swarm, long tick) => throw new NotSupportedException();
    private static void ExportLegacy(DysonSwarm swarm, BinaryWriter writer) => throw new NotSupportedException();
    private static IEnumerable<CodeInstruction> LegacyExportBody(IEnumerable<CodeInstruction> instructions) =>
        ManagedIO(HeadlessDysonSwarm.Transport(instructions));
    private static void Resize(DysonSwarm swarm, int capacity) => HeadlessDysonSwarm.Resize(swarm, capacity);
    private static void ColorBuffer(DysonSwarm swarm) { }

    private static DysonSwarm Swarm()
    {
        var sphere = Uninitialized<DysonSphere>();
        sphere.gameData = GameMain.data;
        sphere.starData = new StarData { id = 1, index = 0, seed = 1 };
        sphere.minOrbitRadius = 1000; sphere.maxOrbitRadius = 100000; sphere.defOrbitRadius = 10000;
        SetField(sphere, "_productRegister", new int[12000]);
        SetField(sphere, "_consumeRegister", new int[12000]);
        sphere.swarm = new DysonSwarm(sphere);
        HeadlessDysonSwarm.Init(sphere.swarm); HeadlessDysonSwarm.Reset(sphere.swarm);
        return sphere.swarm;
    }

    private static DysonSwarm RoundTripSwarm(DysonSwarm source)
    {
        using var stream = new MemoryStream();
        source.Export(new BinaryWriter(stream));
        stream.Position = 0;
        var restored = Swarm();
        restored.Import(new BinaryReader(stream));
        Check.AreEqual(stream.Length, stream.Position, "The native save format must be consumed exactly.");
        DysonSphereDataProcessor.ValidateSwarm(restored);
        return restored;
    }

    [TestMethod]
    public void NewSailsSurviveNativeExportImportAndSlotReuseWithoutAnyGpuBuffer()
    {
        var swarm = Swarm();
        Check.AreEqual(0, swarm.AddSolarSail(new DysonSail { px = 123, gs = 1 }, 1, 1000));
        swarm.AddSolarSail(new DysonSail { px = 456, gs = 1 }, 1, 2000);
        var restored = RoundTripSwarm(swarm);
        Check.AreEqual(2, restored.sailCount);
        Check.AreEqual(123f, HeadlessDysonSwarm.Pool(restored)[0].px);
        Check.AreEqual(1u, restored.sailInfos[0].orbit);
        restored.RemoveSolarSail(0);
        Check.AreEqual(0f, HeadlessDysonSwarm.Pool(restored)[0].st);
        restored.AddSolarSail(new DysonSail { px = 789, gs = 1 }, 1, 3000);
        var rejoined = RoundTripSwarm(restored);
        Check.AreEqual(789f, HeadlessDysonSwarm.Pool(rejoined)[0].px);
        foreach (var name in new[] { "swarmBuffer", "swarmInfoBuffer", "nearIdBuffer", "argBuffer", "bulletBuffer", "sailOrbitColorsBuffer" })
            Check.IsNull(AccessTools.Field(typeof(DysonSwarm), name).GetValue(rejoined), name);
        HeadlessDysonSwarm.Free(rejoined);
        Check.IsNull(HeadlessDysonSwarm.Pool(rejoined));
    }

    [TestMethod]
    public void GrowthExpiryAndPeriodicCompactionPreserveTheRemainingSails()
    {
        var swarm = Swarm();
        for (var i = 0; i < 1000; i++)
            swarm.AddSolarSail(new DysonSail { px = i, gs = 1 }, 1, i < 900 ? 100 : 1000000);
        Check.AreEqual(1024, swarm.sailCapacity);
        NativeTick(swarm, 100);
        Check.AreEqual(100, swarm.sailCount);
        NativeTick(swarm, 216000);
        Check.AreEqual(512, swarm.sailCapacity);
        Check.HasCount(512, HeadlessDysonSwarm.Pool(swarm));
        Check.AreEqual(900f, HeadlessDysonSwarm.Pool(swarm)[0].px);
        var restored = RoundTripSwarm(swarm);
        Check.AreEqual(100, restored.sailCount);
        Check.AreEqual(100, restored.orbits[1].count);
        NativeTick(restored, 1000000);
        NativeTick(restored, 1000001);
        Check.AreEqual(0, restored.sailCount);
        Check.AreEqual(512, restored.sailCapacity);
        Check.AreEqual(0, restored.orbits[1].count);
    }

    [TestMethod]
    public void WrappedExpiryAndAbsorptionQueuesSurviveGrowthAndNativeSave()
    {
        var swarm = Swarm();
        for (var i = 0; i < 400; i++) swarm.AddSolarSail(new DysonSail { gs = 1 }, 1, 100 + i);
        NativeTick(swarm, 299);
        for (var i = 0; i < 500; i++) swarm.AddSolarSail(new DysonSail { gs = 1 }, 1, 1000 + i);
        var node = new DysonNode { id = 9, layerId = 2, rid = 17 };
        Check.IsTrue(swarm.AbsorbSail(node, 500));
        var restored = RoundTripSwarm(swarm);
        Check.AreEqual(swarm.sailCount, restored.sailCount);
        Check.AreEqual(17u, restored.sailInfos[restored.absorbOrder[restored.absorbCursor].index].node);
        Check.AreEqual(14900L, restored.absorbOrder[restored.absorbCursor].time);
        Check.AreEqual(9, restored.absorbOrder[restored.absorbCursor].node);
        Check.AreEqual(swarm.expiryEnding, restored.expiryEnding);
        Check.AreEqual(swarm.expiryCursor, restored.expiryCursor);
    }

    [TestMethod]
    public void ManualOrbitRemovalAndEternalSailsKeepNativeSemantics()
    {
        var swarm = Swarm();
        swarm.NewOrbit(20000, UnityEngine.Quaternion.identity);
        swarm.AddSolarSail(new DysonSail { px = 1, gs = 1 }, 1, 1000);
        swarm.AddSolarSail(new DysonSail { px = 2, gs = 1 }, 2, 2000);
        swarm.RemoveSailsByOrbit(1);
        Check.AreEqual(1, swarm.sailCount);
        Check.AreEqual(0, swarm.orbits[1].count);
        Check.AreEqual(1, swarm.orbits[2].count);
        GameMain.data.gameDesc.isSandboxMode = true;
        swarm.SetAllSailsEternal();
        var restored = RoundTripSwarm(swarm);
        Check.IsTrue(restored.eternal);
        Check.AreEqual(long.MaxValue, restored.expiryOrder[restored.expiryCursor].time);
        NativeTick(restored, 10000);
        Check.AreEqual(1, restored.sailCount);
        GameMain.data.gameDesc.isSandboxMode = false;
        NativeTick(restored, 10001);
        Check.IsFalse(restored.eternal);
        restored.RemoveSailsByOrbit(-1);
        Check.AreEqual(0, restored.sailCount);
        Check.AreEqual(0, restored.orbits[2].count);
    }

    [TestMethod]
    public void HistogramsIncludeZeroSlotAndOnlyTheActiveWrappedQueue()
    {
        var swarm = Swarm();
        swarm.AddSolarSail(new DysonSail { gs = 1 }, 1, 6000);
        var histogram = DysonSphereManager.SailLifeDistribution(swarm, 10, 0, 60, null, out var max);
        Check.AreEqual(1f, histogram.Sum()); Check.AreEqual(1f, max);
        swarm.expiryOrder[100] = new ExpiryOrder { index = 0, time = 5000 };
        histogram = DysonSphereManager.SailLifeDistribution(swarm, 10, 0, 0, new[] { 1 }, out max);
        Check.AreEqual(1f, histogram.Sum(), "Stale entries outside the ring are not sails.");
        Check.AreEqual(1f, histogram[9]);
    }

    [TestMethod]
    public void InconsistentCountsCannotBeExportedAsAnotherApparentlyValidSnapshot()
    {
        var swarm = Swarm();
        swarm.AddSolarSail(new DysonSail { gs = 1 }, 1, 1000);
        HeadlessDysonSwarm.Pool(swarm)[0] = default;
        Check.Throws<InvalidDataException>(() => RoundTripSwarm(swarm));
    }

    private static DysonSwarm ImportLegacy(DysonSwarm source)
    {
        using var stream = new MemoryStream();
        // Reproduce the old on-disk corruption through the native format, bypassing only the
        // new export guard. The production import/migration and subsequent saves stay intact.
        ExportLegacy(source, new BinaryWriter(stream));
        stream.Position = 0;
        var restored = Swarm(); restored.Import(new BinaryReader(stream));
        Check.AreEqual(stream.Length, stream.Position);
        DysonSphereDataProcessor.ValidateSwarm(restored);
        return restored;
    }

    [TestMethod]
    public void LegacyHeadlessSaveWithNoSailPayloadLoadsAndCanBeSavedAgain()
    {
        var swarm = Swarm();
        swarm.NewOrbit(20000, UnityEngine.Quaternion.identity);
        swarm.AddSolarSail(new DysonSail { px = 100, gs = 1 }, 1, 1000);
        swarm.AddSolarSail(new DysonSail { px = 200, gs = 1 }, 2, 2000);
        swarm.bulletPool[1] = new SailBullet { id = 1, state = 2, maxt = 10 };
        swarm.bulletCursor = 2;
        var bullets = swarm.bulletCursor;
        Array.Clear(HeadlessDysonSwarm.Pool(swarm), 0, swarm.sailCursor);
        var restored = ImportLegacy(swarm);
        Check.AreEqual(0, restored.sailCount);
        Check.AreEqual(0, restored.expiryEnding - restored.expiryCursor);
        Check.AreEqual(0, restored.absorbEnding - restored.absorbCursor);
        Check.AreEqual(0, restored.orbits[1].count); Check.AreEqual(0, restored.orbits[2].count);
        Check.AreEqual(20000f, restored.orbits[2].radius);
        Check.AreEqual(bullets, restored.bulletCursor);
        var rejoined = RoundTripSwarm(restored);
        Check.AreEqual(0, rejoined.sailCount);
        rejoined.AddSolarSail(new DysonSail { px = 300, gs = 1 }, 2, 3000);
        Check.AreEqual(1, RoundTripSwarm(rejoined).sailCount);
    }

    [TestMethod]
    public void LegacyMigrationPreservesIntactSailsAndWrappedLifecycleOrders()
    {
        var swarm = Swarm();
        for (var i = 0; i < 400; i++) swarm.AddSolarSail(new DysonSail { px = i, gs = 1 }, 1, 100 + i);
        NativeTick(swarm, 299);
        for (var i = 0; i < 300; i++) swarm.AddSolarSail(new DysonSail { px = 1000 + i, gs = 1 }, 1, 10000 + i);
        var lost = swarm.expiryOrder[swarm.expiryCursor].index;
        HeadlessDysonSwarm.Pool(swarm)[lost] = default;
        var restored = ImportLegacy(swarm);
        Check.AreEqual(swarm.sailCount - 1, restored.sailCount);
        Check.AreEqual(restored.sailCount, restored.orbits[1].count);
        Check.AreEqual(restored.sailCount, restored.expiryEnding - restored.expiryCursor);
        Check.AreEqual(1000f, HeadlessDysonSwarm.Pool(restored)[199].px);
        var again = RoundTripSwarm(restored);
        NativeTick(again, 20000);
        Check.AreEqual(0, again.sailCount);
        Check.AreEqual(0, again.orbits[1].count);
    }

    [TestMethod]
    public void LegacyMissingAbsorbingSailsReleaseOnlyTheirPendingNodeReservations()
    {
        var swarm = Swarm();
        swarm.AddSolarSail(new DysonSail { px = 1, gs = 1 }, 1, 1000);
        swarm.AddSolarSail(new DysonSail { px = 2, gs = 1 }, 1, 1000);
        var node = new DysonNode { id = 1, layerId = 1, rid = 1, cpOrdered = 2 };
        swarm.AbsorbSail(node, 100); swarm.AbsorbSail(node, 100);
        HeadlessDysonSwarm.Pool(swarm)[0] = default;
        var restored = ImportLegacy(swarm);
        Check.AreEqual(1, restored.sailCount);
        Check.AreEqual(1, restored.absorbEnding);
        var sphere = restored.dysonSphere;
        sphere.layersIdBased = new DysonSphereLayer[11];
        sphere.layersIdBased[1] = new DysonSphereLayer(sphere) { id = 1, nodePool = new DysonNode[] { null!, node }, nodeCursor = 2 };
        SolarSailState.FinishImport(sphere);
        Check.AreEqual(1, node.cpOrdered);
        SolarSailState.FinishImport(sphere);
        Check.AreEqual(1, node.cpOrdered, "The same reservation must not be cancelled twice.");
        Check.AreEqual(1, RoundTripSwarm(restored).sailCount);
    }

    [TestMethod]
    public void LegacyCompatibilityDoesNotAcceptOtherMalformedPayloads()
    {
        var swarm = Swarm();
        swarm.AddSolarSail(new DysonSail { px = 123, gs = 1 }, 1, 1000);
        HeadlessDysonSwarm.Pool(swarm)[0].st = 0;
        Check.Throws<InvalidDataException>(() => ImportLegacy(swarm));
    }

    [TestMethod]
    public void ReceivedSwarmSnapshotsKeepStrictValidationInsteadOfMigratingMissingSails()
    {
        var swarm = Swarm();
        swarm.AddSolarSail(new DysonSail { gs = 1 }, 1, 1000);
        HeadlessDysonSwarm.Pool(swarm)[0] = default; swarm.sailInfos[0] = default;
        var session = Multiplayer.Session = Uninitialized<MultiplayerSession>();
        session.Client = Uninitialized<global::NebulaeNetwork.Client>();
        session.DysonSpheres = new DysonSphereManager();
        using (session.DysonSpheres.IncomingDysonSwarmPacket.On())
            DysonSwarm_Patch.Import_Postfix(swarm);
        Check.AreEqual(1, swarm.sailCount);
        Check.Throws<InvalidDataException>(() => DysonSphereDataProcessor.ValidateSwarm(swarm));
    }

    [TestMethod]
    public void AbsorptionRetiresTheCpuSlotAtTheNativeCompletionTime()
    {
        var swarm = Swarm();
        swarm.dysonSphere.layersIdBased = new DysonSphereLayer[11];
        swarm.AddSolarSail(new DysonSail { gs = 1 }, 1, 1000);
        Check.IsTrue(swarm.AbsorbSail(new DysonNode { id = 1, layerId = 1, rid = 1 }, 100));
        NativeTick(swarm, 1000); Check.AreEqual(1, swarm.sailCount);
        NativeTick(swarm, 14500); Check.AreEqual(0, swarm.sailCount);
        Check.AreEqual(0f, HeadlessDysonSwarm.Pool(swarm)[0].st);
        Check.AreEqual(0u, swarm.sailInfos[0].orbit);
        Check.AreEqual(0, RoundTripSwarm(swarm).sailCount);
    }

    // Only Unity memory copy and profiling boundaries are adapted. The production patches and
    // native Export/Import bodies above run unchanged, including their slot and queue formats.
    private static IEnumerable<CodeInstruction> ManagedIO(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var original in instructions)
        {
            var code = new CodeInstruction(original);
            if (code.operand is MethodInfo method)
            {
                MethodInfo? replacement = null;
                if (method.DeclaringType == typeof(UnsafeIO))
                {
                    var name = method.Name switch
                    {
                        "WriteMassive" => nameof(WriteMassive),
                        "ReadMassive" => nameof(ReadMassive),
                        "Write" => nameof(WriteOne),
                        "Read" => nameof(ReadOne),
                        "WriteInt32Array" => nameof(WriteMassive),
                        "ReadInt32Array" => nameof(ReadMassive),
                        _ => null
                    };
                    if (name != null) replacement = AccessTools.Method(typeof(SolarSailLifecycleTest), name)
                        .MakeGenericMethod(method.IsGenericMethod ? method.GetGenericArguments() : new[] { typeof(int) });
                }
                if (method.DeclaringType == typeof(PerformanceMonitor))
                    replacement = AccessTools.Method(typeof(SolarSailLifecycleTest), nameof(Profile));
                if (method.DeclaringType == typeof(DysonSwarm) && method.Name == "SetSailCapacity")
                    replacement = AccessTools.Method(typeof(SolarSailLifecycleTest), nameof(Resize));
                if (method.DeclaringType == typeof(DysonSwarm) && method.Name == "SetOrbitColorBuffer")
                    replacement = AccessTools.Method(typeof(SolarSailLifecycleTest), nameof(ColorBuffer));
                if (replacement != null) { code.opcode = OpCodes.Call; code.operand = replacement; }
            }
            yield return code;
        }
    }

    private static void Profile(ESaveDataEntry entry) { }
    private static void WriteMassive<T>(Stream stream, T[] values, int count) where T : struct
    {
        if (count == 0) return;
        var bytes = new byte[Marshal.SizeOf(typeof(T)) * count];
        var pin = GCHandle.Alloc(values, GCHandleType.Pinned);
        try { Marshal.Copy(pin.AddrOfPinnedObject(), bytes, 0, bytes.Length); }
        finally { pin.Free(); }
        stream.Write(bytes, 0, bytes.Length);
    }
    private static void ReadMassive<T>(Stream stream, T[] values, int count) where T : struct
    {
        if (count == 0) return;
        var bytes = new byte[Marshal.SizeOf(typeof(T)) * count];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        var pin = GCHandle.Alloc(values, GCHandleType.Pinned);
        try { Marshal.Copy(bytes, 0, pin.AddrOfPinnedObject(), bytes.Length); }
        finally { pin.Free(); }
    }
    private static void WriteOne<T>(Stream stream, ref T value, int count) where T : struct => WriteMassive(stream, new[] { value }, 1);
    private static void ReadOne<T>(Stream stream, ref T value, int count) where T : struct
    {
        var array = new T[1]; ReadMassive(stream, array, 1); value = array[0];
    }
}
