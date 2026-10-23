using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace NebulaeWorld.Combat;

/// <summary>Lossless cold storage for unrealized hive geometry; virtual core state stays writable.</summary>
internal static class HivePatternStorage
{
    private sealed class Cold
    {
        public GrowthPattern_DFSpace.Builder[] Stub, Full;
        public byte[] Tail;
        public int Length, Completed, NativeBytes;
    }
    private static ConditionalWeakTable<EnemyDFHiveSystem, Cold> cold = new();
    [ThreadStatic] private static MemoryStream scratch;
    [ThreadStatic] private static byte[] exportBuffer;

    private static int dormantHives, importCount;
    private static readonly object importGate = new();
    private static ConditionalWeakTable<EnemyDFHiveSystem, Task> importJobs = new();
    private static readonly List<Task> imports = new();
    private static ConcurrentExclusiveSchedulerPair importScheduler;
    public static int DormantHives => Volatile.Read(ref dormantHives);

    internal static bool IsCold(EnemyDFHiveSystem hive) => cold.TryGetValue(hive, out var state) && ReferenceEquals(hive.pbuilders, state.Stub);

    // The root and virtual cores are deliberately retained without expanding the geometry.
    internal static GrowthPattern_DFSpace.Builder[] ReadCore(EnemyDFHiveSystem hive)
    {
        WaitImport(hive);
        return hive.realized || hive.isPreview ? Read(hive) : hive.pbuilders;
    }

    internal static int CoreInstanceId(EnemyDFHiveSystem hive, int index)
    {
        WaitImport(hive);
        var builders = hive.pbuilders;
        if ((uint)index >= (uint)(builders?.Length ?? 0)) builders = Read(hive);
        return builders[index].instBuilderId;
    }

    internal static int CoreEnemyId(EnemyDFHiveSystem hive, int index)
    {
        WaitImport(hive);
        var builders = hive.pbuilders;
        if ((uint)index >= (uint)(builders?.Length ?? 0)) builders = Read(hive);
        return builders[index].instId;
    }

    internal static GrowthPattern_DFSpace.Builder[] Read(EnemyDFHiveSystem hive)
    {
        if (hive == null) return null;
        WaitImport(hive);
        if (!cold.TryGetValue(hive, out var state)) return hive.pbuilders;
        if (!ReferenceEquals(hive.pbuilders, state.Stub))
        {
            if (hive.realized || hive.isPreview) Forget(hive);
            return hive.pbuilders;
        }
        lock (state)
        {
            if (!ReferenceEquals(hive.pbuilders, state.Stub)) return hive.pbuilders;
            var full = new GrowthPattern_DFSpace.Builder[state.Length];
            Array.Copy(state.Stub, full, state.Stub.Length);
            using var input = new MemoryStream(state.Tail, false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var buffer = Buffer();
            deflate.CopyTo(buffer); buffer.Position = 0;
            using var reader = new BinaryReader(buffer, System.Text.Encoding.UTF8, true);
            if (state.NativeBytes > 0)
            {
                for (var i = state.Stub.Length; i < full.Length; i++) full[i].Import(reader);
                if (buffer.Position != state.NativeBytes) throw new InvalidDataException("Cold hive native block is misaligned.");
                for (var i = state.Stub.Length; i < full.Length; i++)
                { ReadSupplement(reader, ref full[i]); full[i].connections = HiveSharedArrays.Intern(full[i].connections); }
            }
            else for (var i = state.Stub.Length; i < full.Length; i++) full[i] = ReadBuilder(reader);
            state.Full = full; hive.pbuilders = full;
            if (hive.realized || hive.isPreview) Forget(hive);
            return full;
        }
    }

    internal static void Forget(EnemyDFHiveSystem hive)
    {
        if (!cold.TryGetValue(hive, out var state)) return;
        if (cold.Remove(hive)) { Interlocked.Decrement(ref dormantHives); }
    }

    internal static void RecompactExport(EnemyDFHiveSystem hive)
    {
        if (!hive.realized && !hive.isPreview && cold.TryGetValue(hive, out var state) && ReferenceEquals(hive.pbuilders, state.Full))
        {
            Array.Copy(state.Full, state.Stub, state.Stub.Length);
            hive.pbuilders = state.Stub; state.Full = null;
        }
    }

    internal static void Compact(EnemyDFHiveSystem hive)
    {
        if (hive.galaxy?.starCount < 256 || hive.galaxy == null || hive.realized || hive.isPreview || IsCold(hive)) return;
        var builders = hive.pbuilders;
        if (builders == null) return;
        if (hive.forms != null) foreach (var form in hive.forms) HiveSharedArrays.ShareEmpty(form);
        if (builders.Length < 16 || hive.coreDocks == null) return;
        var prefix = 2;
        foreach (var dock in hive.coreDocks) prefix = Math.Max(prefix, dock.builderIndex + 1);
        if (prefix >= builders.Length / 2) return;
        Forget(hive);
        var state = new Cold { Length = builders.Length, Stub = new GrowthPattern_DFSpace.Builder[prefix] };
        Array.Copy(builders, state.Stub, prefix);
        // Tail arrays are about to be discarded, so interning millions of them only wastes load time.
        for (var i = 0; i < prefix; i++) state.Stub[i].connections = HiveSharedArrays.Intern(state.Stub[i].connections);
        var buffer = Buffer();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, true))
        {
            var native = CanEncodeNative(builders, prefix);
            if (native)
            {
                for (var i = prefix; i < builders.Length; i++) builders[i].Export(writer);
                state.NativeBytes = (int)buffer.Position;
            }
            for (var i = prefix; i < builders.Length; i++)
            {
                if (native) WriteSupplement(writer, builders[i]);
                else WriteBuilder(writer, builders[i]);
                if (!builders[i].isConn && builders[i].matterProvided >= builders[i].matterNeeded) state.Completed++;
            }
        }
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true))
            deflate.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
        state.Tail = output.ToArray();
        cold.Add(hive, state); hive.pbuilders = state.Stub;
        Interlocked.Increment(ref dormantHives);
    }

    internal static bool TryCompletedCount(EnemyDFHiveSystem hive, out int count)
    {
        count = 0;
        WaitImport(hive);
        if (hive.realized || !cold.TryGetValue(hive, out var state) || !ReferenceEquals(hive.pbuilders, state.Stub)) return false;
        count = state.Completed;
        foreach (var builder in state.Stub) if (!builder.isConn && builder.matterProvided >= builder.matterNeeded) count++;
        return true;
    }

    internal static bool CanExportCold(EnemyDFHiveSystem hive)
    {
        WaitImport(hive);
        return cold.TryGetValue(hive, out var state) && ReferenceEquals(hive.pbuilders, state.Stub) && state.NativeBytes > 0;
    }

    internal static void WriteBuilders(EnemyDFHiveSystem hive, BinaryWriter writer)
    {
        WaitImport(hive);
        if (cold.TryGetValue(hive, out var state) && ReferenceEquals(hive.pbuilders, state.Stub) && state.NativeBytes > 0)
        {
            writer.Write(state.Length);
            foreach (var core in state.Stub) core.Export(writer);
            using var input = new MemoryStream(state.Tail, false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var chunk = exportBuffer ??= new byte[64 * 1024];
            var left = state.NativeBytes;
            while (left > 0)
            {
                var read = deflate.Read(chunk, 0, Math.Min(chunk.Length, left));
                if (read <= 0) throw new InvalidDataException("Truncated cold hive export.");
                writer.Write(chunk, 0, read); left -= read;
            }
            return;
        }
        var builders = Read(hive);
        writer.Write(builders.Length);
        for (var i = 0; i < builders.Length; i++) builders[i].Export(writer);
    }

    private static bool CanEncodeNative(GrowthPattern_DFSpace.Builder[] builders, int start)
    {
        for (var i = start; i < builders.Length; i++)
        {
            ref var b = ref builders[i];
            if (b.protoId != (short)b.protoId || b.instId < 0 || b.instId > 0x0FFFFFFF ||
                (b.instId > 0 && b.instBuilderId != (short)b.instBuilderId) || b.savedWorkProgress < 0 ||
                b.savedWorkProgress > 0x0FFFFFFF || b.connections == null || b.connections.Length > short.MaxValue) return false;
            foreach (var id in b.connections) if (id != (short)id) return false;
        }
        return true;
    }

    private static void WriteSupplement(BinaryWriter w, GrowthPattern_DFSpace.Builder b)
    {
        // Geometry/connections are already in the native block. Retain every omitted or narrowed field.
        w.Write(b.size); w.Write(b.length); w.Write(b.rank); w.Write(b.antSegOffset);
        w.Write(b.matterProvided); w.Write(b.matterSpeed); w.Write(b.workTicks);
        w.Write(b.instId); w.Write(b.instBuilderId); w.Write(b.savedWorkProgress);
    }

    private static void ReadSupplement(BinaryReader r, ref GrowthPattern_DFSpace.Builder b)
    {
        b.size = r.ReadSingle(); b.length = r.ReadSingle(); b.rank = r.ReadInt32(); b.antSegOffset = r.ReadInt32();
        b.matterProvided = r.ReadInt32(); b.matterSpeed = r.ReadInt32(); b.workTicks = r.ReadInt32();
        b.instId = r.ReadInt32(); b.instBuilderId = r.ReadInt32(); b.savedWorkProgress = r.ReadInt32();
    }

    private static void WriteBuilder(BinaryWriter w, GrowthPattern_DFSpace.Builder b)
    {
        // Native Builder.Export omits transient fields and quantizes size. Cold storage must not.
        w.Write(b.protoId); w.Write(b.pos.x); w.Write(b.pos.y); w.Write(b.pos.z);
        w.Write(b.rot.x); w.Write(b.rot.y); w.Write(b.rot.z); w.Write(b.rot.w);
        w.Write(b.size); w.Write(b.length); w.Write(b.rank); w.Write(b.antSegOffset);
        w.Write(b.matterProvided); w.Write(b.matterNeeded); w.Write(b.matterSpeed); w.Write(b.workTicks);
        w.Write(b.instId); w.Write(b.instBuilderId); w.Write(b.savedWorkProgress);
        w.Write(b.connections?.Length ?? -1);
        if (b.connections != null) foreach (var connection in b.connections) w.Write(connection);
    }

    private static GrowthPattern_DFSpace.Builder ReadBuilder(BinaryReader r)
    {
        var b = new GrowthPattern_DFSpace.Builder();
        b.protoId = r.ReadInt32(); b.pos.x = r.ReadSingle(); b.pos.y = r.ReadSingle(); b.pos.z = r.ReadSingle();
        b.rot.x = r.ReadSingle(); b.rot.y = r.ReadSingle(); b.rot.z = r.ReadSingle(); b.rot.w = r.ReadSingle();
        b.size = r.ReadSingle(); b.length = r.ReadSingle(); b.rank = r.ReadInt32(); b.antSegOffset = r.ReadInt32();
        b.matterProvided = r.ReadInt32(); b.matterNeeded = r.ReadInt32(); b.matterSpeed = r.ReadInt32(); b.workTicks = r.ReadInt32();
        b.instId = r.ReadInt32(); b.instBuilderId = r.ReadInt32(); b.savedWorkProgress = r.ReadInt32();
        var count = r.ReadInt32();
        if (count >= 0)
        {
            b.connections = new int[count];
            for (var i = 0; i < count; i++) b.connections[i] = r.ReadInt32();
            b.connections = HiveSharedArrays.Intern(b.connections);
        }
        return b;
    }

    internal static void Reset()
    {
        CompleteImports();
        cold = new(); scratch = null; exportBuffer = null; dormantHives = 0;
        HiveSharedArrays.Reset();
    }

    internal static void ScheduleImport(EnemyDFHiveSystem hive)
    {
        if (hive.galaxy?.starCount < 256 || hive.galaxy == null || hive.realized || hive.isPreview)
        { Compact(hive); return; }
        lock (importGate)
        {
            importScheduler ??= new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, Math.Min(8, Environment.ProcessorCount));
            var job = Task.Factory.StartNew(() => Compact(hive), CancellationToken.None, TaskCreationOptions.None, importScheduler.ConcurrentScheduler);
            importJobs.Remove(hive); importJobs.Add(hive, job); imports.Add(job);
            Volatile.Write(ref importCount, imports.Count);
        }
    }

    internal static void WaitImport(EnemyDFHiveSystem hive)
    {
        if (Volatile.Read(ref importCount) != 0 && importJobs.TryGetValue(hive, out var job)) job.GetAwaiter().GetResult();
    }

    internal static void CompleteImports()
    {
        lock (importGate)
        {
            try { if (imports.Count > 0) Task.WaitAll(imports.ToArray()); }
            finally
            {
                importScheduler?.Complete(); importScheduler = null;
                imports.Clear(); importJobs = new(); Volatile.Write(ref importCount, 0);
            }
        }
    }

    private static MemoryStream Buffer()
    {
        var buffer = scratch ??= new MemoryStream(128 * 1024);
        buffer.SetLength(0); buffer.Position = 0;
        return buffer;
    }
}

internal static class HiveSharedArrays
{
    private sealed class Contents : IEqualityComparer<int[]>
    {
        public bool Equals(int[] a, int[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        public int GetHashCode(int[] a) { unchecked { var hash = a.Length; foreach (var value in a) hash = hash * 31 + value; return hash; } }
    }
    private sealed class Shared { public int[] Units, Vacancies; }
    private static readonly FieldInfo vacancies = typeof(EnemyFormation).GetField("vacancies", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
    private static Dictionary<int[], int[]> arrays = new(new Contents());
    private static ConditionalWeakTable<EnemyFormation, Shared> forms = new();
    internal static int[] Intern(int[] array)
    {
        if (array == null) return null;
        lock (arrays)
        {
            if (arrays.TryGetValue(array, out var shared)) return shared;
            if (arrays.Count < 65536) arrays.Add(array, array);
            return array;
        }
    }
    internal static void ShareEmpty(EnemyFormation form)
    {
        if (form == null || form.units == null || form.unitCount != 0) return;
        var shared = forms.GetOrCreateValue(form);
        form.units = shared.Units = Intern(form.units);
        shared.Vacancies = Intern((int[])vacancies.GetValue(form));
        vacancies.SetValue(form, shared.Vacancies);
    }
    internal static void Writable(EnemyFormation form)
    {
        if (!forms.TryGetValue(form, out var shared)) return;
        if (ReferenceEquals(form.units, shared.Units)) form.units = (int[])form.units.Clone();
        if (ReferenceEquals(vacancies.GetValue(form), shared.Vacancies)) vacancies.SetValue(form, shared.Vacancies.Clone());
        forms.Remove(form);
    }
    internal static void Reset() { arrays = new(new Contents()); forms = new(); }
}
