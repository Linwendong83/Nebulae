using HarmonyLib;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class TaskContextPerformanceTest
{
    private readonly Harmony harmony = new("nebulae.tests.task-context-performance");

    [TestCleanup]
    public void Cleanup() => harmony.UnpatchSelf();

    private void Patch()
    {
        harmony.CreateClassProcessor(typeof(ScatterTaskContext_Patch)).Patch();
        harmony.CreateClassProcessor(typeof(BatchTaskContext_Patch)).Patch();
    }

    [TestMethod]
    [DataRow(384, 1)]
    [DataRow(384, 35)]
    [DataRow(49146, 2)]
    [DataRow(49146, 35)]
    public void PartitionsMatchNativeAcrossEmptyBatchesAndUnevenWorkerRanges(int capacity, int workers)
    {
        var workloads = new[] { 0, 5, 0, 7, 0, 3, 0 };
        var native = CreateScatter(capacity, workers, workloads);
        native.DetermineThreadTasks();
        Patch();
        var patched = CreateScatter(capacity, workers, workloads);
        patched.DetermineThreadTasks();

        var expected = workloads.SelectMany((count, batch) =>
            Enumerable.Range(1, count).Select(id => (batch, id))).ToArray();
        var actual = new List<(int batch, int id)>();
        for (var worker = 0; worker < workers; worker++)
        {
            var before = native.threadContexts[worker];
            var after = patched.threadContexts[worker];
            Check.AreEqual(before.ordinalBegin, after.ordinalBegin);
            Check.AreEqual(before.ordinalEnd, after.ordinalEnd);
            if (after.workCount == 0) continue;
            Check.AreEqual(before.batchBegin, after.batchBegin);
            Check.AreEqual(before.batchEnd, after.batchEnd);
            Check.AreEqual(before.idBegin, after.idBegin);
            Check.AreEqual(before.idEnd, after.idEnd);
            actual.AddRange(AssignedItems(patched, worker));
        }
        CollectionAssert.AreEqual(expected, actual.ToArray(), "Every live item must be assigned exactly once.");
        Check.AreEqual(0, patched.threadContexts[workers].workCount, "Disabled worker contexts stay reset.");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    public void EmptyWorkNeverUsesUnclearedCapacity(int batches)
    {
        Patch();
        var context = CreateScatter(49146, 35, new int[batches]);
        context.DetermineThreadTasks();
        foreach (var worker in context.threadContexts)
        {
            Check.AreEqual(0, worker.workCount);
            Check.AreEqual(0, worker.batchBegin);
            Check.AreEqual(0, worker.batchEnd);
            Check.AreEqual(0, worker.idBegin);
            Check.AreEqual(0, worker.idEnd);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ScatterResetClearsTheActiveSentinelAcrossGrowthShrinkAndZeroFactories(bool rotating)
    {
        Patch();
        var context = new ScatterTaskContext();
        context.Init(49146, 35);
        foreach (var batches in new[] { 8, 2, 0, 6 })
        {
            Fill(context.ordinals);
            context.batchOffset = 17;
            if (rotating) context.ResetFrame(23L, batches, 4);
            else context.ResetFrame(batches, 4);

            var active = Math.Max(1, batches);
            Check.AreEqual(active, context.batchCount);
            Check.AreEqual(4, context.workerThreadCount);
            Check.IsTrue(context.ordinals.Take(active + 1).All(value => value == 0));
            Check.AreEqual(123456, context.ordinals[active + 1], "Inactive capacity must not be cleared.");
            Check.AreEqual(rotating ? 23 % active : 17, context.batchOffset);
        }
    }

    [TestMethod]
    public void GrowingScatterBatchesCannotExposeThePreviousFramesTail()
    {
        Patch();
        var context = new ScatterTaskContext();
        context.Init(49146, 4);
        foreach (var workloads in new[] {
                     new[] { 0, 2, 0, 4, 0, 1 }, new[] { 0 },
                     new[] { 3, 0, 2 }, new[] { 0, 1, 0, 2, 0, 3, 0, 4 } })
        {
            context.ResetFrame(19L, workloads.Length, 4);
            for (var i = 0; i < workloads.Length; i++) context.ordinals[i + 1] = context.ordinals[i] + workloads[i];
            context.DetermineThreadTasks();
            var expected = workloads.SelectMany((count, batch) =>
                Enumerable.Range(1, count).Select(id => (batch, id))).ToArray();
            var actual = Enumerable.Range(0, 4).SelectMany(worker => AssignedItems(context, worker)).ToArray();
            CollectionAssert.AreEqual(expected, actual);
        }
    }

    [TestMethod]
    public void BatchResetAndNativeSortPreserveActiveIndexesAcrossGrowthShrinkAndZeroFactories()
    {
        Patch();
        var context = new BatchTaskContext();
        context.Init(49146, 35);
        foreach (var batches in new[] { 8, 2, 0, 6 })
        {
            Fill(context.batchValues);
            Fill(context.batchIndices);
            context.batchCursor = 99;
            context.ResetFrame(batches, 4);
            Check.AreEqual(batches, context.batchCount);
            Check.AreEqual(4, context.workerThreadCount);
            Check.AreEqual(0, context.batchCursor);
            CollectionAssert.AreEqual(new int[batches], context.batchValues.Take(batches).ToArray());
            CollectionAssert.AreEqual(Enumerable.Range(0, batches).ToArray(), context.batchIndices.Take(batches).ToArray());
            for (var i = 0; i < batches; i++) context.batchValues[i] = i + 1;
            context.SortValues();
            CollectionAssert.AreEqual(Enumerable.Range(0, batches).Reverse().ToArray(), context.batchIndices.Take(batches).ToArray());
            Check.AreEqual(123456, context.batchValues[batches]);
            Check.AreEqual(123456, context.batchIndices[batches]);
        }
    }

    [TestMethod]
    public void NativeRedispatchStillSplitsWorkWithoutLossOrDuplication()
    {
        Patch();
        var context = CreateScatter(49146, 2, new[] { 0, 0, 1000, 0 });
        context.DetermineThreadTasks();
        context.threadContexts[0].ordinalCurrent = context.threadContexts[0].ordinalEnd;
        var watching = DeepProfiler.watchEnabled;
        try
        {
            DeepProfiler.watchEnabled = false;
            Check.IsTrue(context.Redispatch(0, 16, 0));
        }
        finally { DeepProfiler.watchEnabled = watching; }

        var remaining = AssignedItems(context, 1).Concat(AssignedItems(context, 0)).ToArray();
        var expected = Enumerable.Range(501, 500).Select(id => (batch: 2, id)).ToArray();
        CollectionAssert.AreEqual(expected, remaining);
        Check.AreEqual(750, context.threadContexts[0].ordinalBegin);
        Check.AreEqual(750, context.threadContexts[1].ordinalEnd);
    }

    private static ScatterTaskContext CreateScatter(int capacity, int workers, int[] workloads)
    {
        var context = new ScatterTaskContext();
        context.Init(capacity, workers + 1);
        // Poison unused slots so a capacity-based range lookup cannot pass silently.
        Fill(context.ordinals);
        context.ResetFrame(workloads.Length, workers);
        for (var i = 0; i < workloads.Length; i++) context.ordinals[i + 1] = context.ordinals[i] + workloads[i];
        return context;
    }

    private static void Fill(int[] values)
    {
        for (var i = 0; i < values.Length; i++) values[i] = 123456;
    }

    private static IEnumerable<(int batch, int id)> AssignedItems(ScatterTaskContext context, int worker)
    {
        var range = context.threadContexts[worker];
        if (range.workCount == 0) yield break;
        for (var batch = range.batchBegin; batch <= range.batchEnd; batch++)
        {
            var begin = batch == range.batchBegin ? range.idBegin : 1;
            var end = batch == range.batchEnd ? range.idEnd : context.ordinals[batch + 1] - context.ordinals[batch] + 1;
            for (var id = begin; id < end; id++) yield return (batch, id);
        }
    }
}
