using System.Reflection;
using Mono.Cecil;
using HarmonyLib;
using NebulaeWorld.Combat;
using UnityEngine;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class GalaxyFiveOptimizationTest
{
    [TestCleanup]
    public void Cleanup() => HivePatternStorage.Reset();

    [TestMethod]
    public void ParallelRangesCoverEveryStarExactlyOnce()
    {
        foreach (var count in new[] { 0, 64, 256, 1024, 8191, 65531 })
            foreach (var grain in new[] { 32, 1024, 4096 })
                foreach (var workers in new[] { 1, 2, 4, 8, 16, 24, 32 })
                {
                    var visits = new int[count];
                    GalaxyWork.Run(count, (start, end) => { for (var i = start; i < end; i++) Interlocked.Increment(ref visits[i]); }, grain, workers);
                    Check.IsTrue(visits.All(v => v == 1));
                }
    }

    [TestMethod]
    public void EveryNativeHivePatternReaderHasAnExplicitStoragePolicy()
    {
        var targets = HivePatternAccess_Patch.TargetMethods().Concat(HivePatternCoreAccess_Patch.TargetMethods()).Select(m => m.MetadataToken).ToHashSet();
        targets.Add(AccessTools.Method(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.Export)).MetadataToken);
        targets.Add(AccessTools.PropertyGetter(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.totalAvailableBuildingCount)).MetadataToken);
        var missing = new List<string>();
        using var assembly = AssemblyDefinition.ReadAssembly(typeof(EnemyDFHiveSystem).Assembly.Location);
        foreach (var type in Types(assembly.MainModule.Types))
            foreach (var method in type.Methods)
            {
                if (!method.HasBody || !method.Body.Instructions.Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Ldfld &&
                    i.Operand is FieldReference f && f.DeclaringType.FullName == typeof(EnemyDFHiveSystem).FullName && f.Name == "pbuilders")) continue;
                if (targets.Contains(method.MetadataToken.ToInt32())) continue;
                if (type.FullName == typeof(EnemyDFHiveSystem).FullName && new[] { "get_isAlive", "get_isVisible", "get_hasAnyStructureOrUnit" }.Contains(method.Name)) continue;
                if (type.FullName == typeof(DFSCoreComponent).FullName && method.Name == nameof(DFSCoreComponent.LogicTickVirtual)) continue;
                missing.Add(type.FullName + "." + method.Name);
            }
        Check.IsEmpty(missing, string.Join(Environment.NewLine, missing));

        static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> source)
        {
            foreach (var type in source)
            {
                yield return type;
                foreach (var nested in Types(type.NestedTypes)) yield return nested;
            }
        }
    }

    [TestMethod]
    public void BuildingCountKeepsTheNativeWarmLoopAndCountsColdGeometryWithoutExpansion()
    {
        var getter = AccessTools.PropertyGetter(typeof(EnemyDFHiveSystem), nameof(EnemyDFHiveSystem.totalAvailableBuildingCount));
        Check.IsFalse(HivePatternAccess_Patch.TargetMethods().Contains(getter));
        var harmony = new Harmony("nebulae.tests.hive-building-count");
        try
        {
            harmony.CreateClassProcessor(typeof(HivePatternCount_Patch)).Patch();
            foreach (var stars in new[] { 64, 8191 })
            {
                var hive = Hive(); hive.galaxy.starCount = stars;
                for (var i = 0; i < hive.pbuilders.Length; i++)
                {
                    hive.pbuilders[i].protoId = i % 5 == 0 ? 8109 : 8101;
                    if (i % 2 == 0) hive.pbuilders[i].matterProvided = hive.pbuilders[i].matterNeeded;
                }
                var expected = hive.pbuilders.Count(b => !b.isConn && b.matterProvided >= b.matterNeeded);
                Check.AreEqual(expected, hive.totalAvailableBuildingCount);
                HivePatternStorage.Compact(hive);
                var retained = hive.pbuilders;
                Check.AreEqual(stars >= 256, HivePatternStorage.IsCold(hive));
                Check.AreEqual(expected, hive.totalAvailableBuildingCount);
                hive.pbuilders[1].matterProvided = hive.pbuilders[1].matterNeeded;
                Check.AreEqual(expected + 1, hive.totalAvailableBuildingCount);
                Check.AreSame(retained, hive.pbuilders);
            }
        }
        finally { harmony.UnpatchSelf(); }
    }

    [TestMethod]
    public void NativeTinderCoreReadersCanBeRewritten()
    {
        var harmony = new Harmony("nebulae.tests.hive-core-access");
        try
        {
            harmony.CreateClassProcessor(typeof(HivePatternCoreAccess_Patch)).Patch();
            harmony.CreateClassProcessor(typeof(HivePatternExport_Patch)).Patch();
        }
        finally { harmony.UnpatchSelf(); }
    }

    [TestMethod]
    public void ImportedHivesCanCompactInParallelBeforeAnyConsumerReadsThem()
    {
        var hives = Enumerable.Range(0, 32).Select(_ => Hive()).ToArray();
        foreach (var hive in hives) HivePatternStorage.ScheduleImport(hive);
        // An early reader joins its individual job; the sector barrier joins the rest.
        Check.HasCount(2, HivePatternStorage.ReadCore(hives[0]));
        HivePatternStorage.CompleteImports();
        Check.AreEqual(hives.Length, HivePatternStorage.DormantHives);
        Check.IsTrue(hives.All(HivePatternStorage.IsCold));
        for (var i = 0; i < hives.Length; i++)
        {
            hives[i].pbuilders[1].savedWorkProgress = i + 173;
            var full = HivePatternStorage.Read(hives[i]);
            Check.HasCount(256, full);
            Check.AreEqual(i + 173, full[1].savedWorkProgress);
            Check.AreEqual(255 * 7, full[255].savedWorkProgress);
        }
    }

    [TestMethod]
    public void NativeColdExportStaysByteIdenticalWithoutExpandingGeometry()
    {
        var hive = Hive();
        for (var i = 0; i < hive.pbuilders.Length; i++) hive.pbuilders[i].connections ??= Array.Empty<int>();
        var original = hive.pbuilders;
        HivePatternStorage.Compact(hive);
        Check.IsTrue(HivePatternStorage.CanExportCold(hive));
        hive.pbuilders[1].savedWorkProgress = 1739;
        original[1].savedWorkProgress = 1739;
        using var expected = new System.IO.MemoryStream(); using var native = new System.IO.BinaryWriter(expected);
        native.Write(original.Length);
        foreach (var builder in original) builder.Export(native);
        using var actual = new System.IO.MemoryStream(); using var writer = new System.IO.BinaryWriter(actual);
        HivePatternStorage.WriteBuilders(hive, writer);
        CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
        Check.IsTrue(HivePatternStorage.IsCold(hive));
        var restored = HivePatternStorage.Read(hive);
        for (var i = 0; i < original.Length; i++) Equal(original[i], restored[i]);
    }

    [TestMethod]
    public void CreatedHivesFreezeTheirTailBeforeScratchReuseAndKeepTheCoreWritable()
    {
        var hives = Enumerable.Range(0, 48).Select(_ => Hive()).ToArray();
        var originals = new List<GrowthPattern_DFSpace.Builder[]>();
        for (var index = 0; index < hives.Length; index++)
        {
            var hive = hives[index];
            // Exercise both native export blocks and the lossless fallback encoding.
            if (index % 2 == 0)
                for (var i = 0; i < hive.pbuilders.Length; i++) hive.pbuilders[i].connections ??= Array.Empty<int>();
            hive.pbuilders[255].workTicks = index + 9000;
            originals.Add(hive.pbuilders);
            HivePatternStorage.CompactCreated(hive);
            Check.IsTrue(HivePatternStorage.IsCold(hive));
            Check.HasCount(2, HivePatternStorage.ReadCore(hive));
            hive.pbuilders[1].instId = 4321 + index;
            originals[index][1].instId = 4321 + index;
        }
        for (var index = 0; index < hives.Length; index++)
        {
            var hive = hives[index];
            if (index % 2 == 0)
            {
                using var expected = new MemoryStream(); using var native = new BinaryWriter(expected);
                native.Write(originals[index].Length);
                foreach (var builder in originals[index]) builder.Export(native);
                using var actual = new MemoryStream(); using var writer = new BinaryWriter(actual);
                HivePatternStorage.WriteBuilders(hive, writer);
                CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
                Check.IsTrue(HivePatternStorage.IsCold(hive));
            }
            var full = HivePatternStorage.Read(hive);
            for (var i = 0; i < full.Length; i++) Equal(originals[index][i], full[i]);
        }
    }

    [TestMethod]
    public void BackgroundCompressionOwnsItsBytesAndRejectsWorkAboveItsMemoryBudget()
    {
        HiveColdCompression.Complete();
        var bytes = new byte[1024 * 1024]; new System.Random(45).NextBytes(bytes);
        var original = (byte[])bytes.Clone();
        Check.IsTrue(HiveColdCompression.TryQueue(bytes, bytes.Length, out var job));
        Array.Clear(bytes, 0, bytes.Length);
        using var input = new MemoryStream(job.GetAwaiter().GetResult());
        using var inflater = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream(); inflater.CopyTo(output);
        CollectionAssert.AreEqual(original, output.ToArray());
        // Rejection happens before copying, and lets the caller use synchronous compression.
        Check.IsFalse(HiveColdCompression.TryQueue(bytes, HiveColdCompression.MaxPendingBytes + 1, out var rejected));
        Check.IsNull(rejected);
        HiveColdCompression.Complete();
    }

    [TestMethod]
    public void DestroyingOtherGameDataDoesNotDiscardActiveColdPatterns()
    {
        var previous = GameMain.data;
        try
        {
            GameMain.data = Uninitialized<GameData>();
            var hive = Hive(); HivePatternStorage.Compact(hive);
            HivePatternLifetime_Patch.Prefix(Uninitialized<GameData>(), out var active);
            HivePatternLifetime_Patch.Postfix(active);
            Check.IsTrue(HivePatternStorage.IsCold(hive));
            Check.HasCount(256, HivePatternStorage.Read(hive));
        }
        finally { GameMain.data = previous; }
    }

    [TestMethod]
    public void ColdGeometryRetainsEveryFieldAndWritableVirtualCore()
    {
        var hive = Hive();
        var full = hive.pbuilders;
        var count = hive.totalAvailableBuildingCount;
        var alive = hive.isAlive;
        HivePatternStorage.Compact(hive);
        Check.HasCount(2, hive.pbuilders);
        Check.AreEqual(alive, hive.isAlive);
        Check.IsTrue(HivePatternStorage.TryCompletedCount(hive, out var coldCount));
        Check.AreEqual(count, coldCount);
        hive.pbuilders[1].savedWorkProgress = 173;
        Check.AreSame(hive.pbuilders, HivePatternStorage.ReadCore(hive));
        Check.AreEqual(hive.pbuilders[1].instBuilderId, HivePatternStorage.CoreInstanceId(hive, 1));
        Check.IsTrue(HivePatternStorage.IsCold(hive));
        var restored = HivePatternStorage.Read(hive);
        Check.AreEqual(173, restored[1].savedWorkProgress);
        for (var i = 2; i < full.Length; i++) Equal(full[i], restored[i]);
        HivePatternStorage.RecompactExport(hive);
        Check.HasCount(2, hive.pbuilders);
        Check.AreEqual(173, hive.pbuilders[1].savedWorkProgress);
        var again = HivePatternStorage.Read(hive);
        for (var i = 2; i < full.Length; i++) Equal(full[i], again[i]);
    }

    [TestMethod]
    public void ActiveAndPreviewHivesKeepTheirFullArrays()
    {
        foreach (var preview in new[] { false, true })
        {
            var hive = Hive();
            var original = hive.pbuilders;
            hive.realized = !preview; hive.isPreview = preview;
            HivePatternStorage.Compact(hive);
            Check.AreSame(original, hive.pbuilders);
        }
        var replica = Hive();
        HivePatternStorage.Compact(replica);
        replica.realized = true; // Host summary can set this directly on a replica.
        Check.HasCount(256, HivePatternStorage.Read(replica));
        Check.IsFalse(HivePatternStorage.IsCold(replica));
        Check.AreEqual(0, HivePatternStorage.DormantHives);
    }

    [TestMethod]
    public void EmptyFormationSharingDetachesBeforeMutationAndPreservesVacancyOrder()
    {
        var first = new EnemyFormation(); first.SetPortCount(120);
        var second = new EnemyFormation(); second.SetPortCount(120);
        HiveSharedArrays.ShareEmpty(first); HiveSharedArrays.ShareEmpty(second);
        Check.AreSame(first.units, second.units);
        var harmony = new Harmony("nebulae.tests.hive-cow");
        try
        {
            harmony.CreateClassProcessor(typeof(HiveFormationWritable_Patch)).Patch();
            harmony.CreateClassProcessor(typeof(EnemyFormation_Patch)).Patch();
            Check.AreEqual(1, first.AddUnit());
            Check.AreNotSame(first.units, second.units);
            Check.AreEqual(0, second.unitCount);
            Check.AreEqual(1, second.AddUnit());
            first.RemoveUnit(1);
            Check.AreEqual(1, first.AddUnit());
            Check.AreEqual(2, second.AddUnit());
            first.Clear();
            Check.AreEqual(0, first.unitCount);
            Check.AreEqual(2, second.unitCount);
        }
        finally { harmony.UnpatchSelf(); }
    }

    private static EnemyDFHiveSystem Hive()
    {
        var hive = new EnemyDFHiveSystem
        {
            galaxy = new GalaxyData { starCount = 8191 },
            builders = new DataPool<EnemyBuilderComponent>(),
            coreDocks = new[] { new DFDock { builderIndex = 1 } },
            pbuilders = new GrowthPattern_DFSpace.Builder[256],
            isEmpty = false
        };
        for (var i = 0; i < hive.pbuilders.Length; i++)
            hive.pbuilders[i] = new GrowthPattern_DFSpace.Builder
            {
                protoId = i % 3 == 0 ? 8109 : 8101,
                pos = new Vector3(i * .123f, -i * .245f, i * .37f),
                rot = new Quaternion(.1f, .2f, .3f, .4f),
                size = 1.234567f,
                length = 35.6789f,
                rank = i % 127,
                antSegOffset = i,
                matterProvided = i * 3,
                matterNeeded = i * 4,
                matterSpeed = i,
                workTicks = i * 5,
                instId = i + 1,
                instBuilderId = i + 2,
                savedWorkProgress = i * 7,
                connections = i % 4 == 0 ? null : new[] { 1, i }
            };
        return hive;
    }

    private static void Equal(GrowthPattern_DFSpace.Builder expected, GrowthPattern_DFSpace.Builder actual)
    {
        foreach (var field in typeof(GrowthPattern_DFSpace.Builder).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var a = field.GetValue(expected); var b = field.GetValue(actual);
            if (a is int[] aa) CollectionAssert.AreEqual(aa, (int[])b!, field.Name);
            else Check.AreEqual(a, b, field.Name);
        }
    }
}
