using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NebulaeWorld;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic.World;

/// <summary>CPU poses stay synchronous for SpaceColliderLogic; GPU copies wait for a render consumer.</summary>
[HarmonyPatch(typeof(SectorModel), nameof(SectorModel.UpdateAstroBuffer))]
internal static class GalaxyAstroBuffer_Patch
{
    [HarmonyPrefix]
    internal static bool Prefix(SectorModel __instance)
    {
        if (HeadlessLocalInteraction_Patch.SkipRelativePoses(__instance)) return false;
        var galaxy = AstroPreparation.Galaxy(__instance, false, __instance.gameData.relativePos, GalaxyOptimization.Enabled);
        AstroUploads.QueueGalaxy(__instance.galaxyAstroBuffer, galaxy, __instance);
        if (!Multiplayer.IsDedicated) Shader.SetGlobalBuffer("_Global_GalaxyAstroBuffer", __instance.galaxyAstroBuffer);
        var count = AstroPreparation.Sector(__instance, false, __instance.gameData.relativePos);
        AstroUploads.QueueRange(__instance.astroBuffer, __instance.astroArr, count);
        return false;
    }

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original = AccessTools.Method(typeof(ComputeBuffer), nameof(ComputeBuffer.SetData), new[] { typeof(Array) });
        var replacement = AccessTools.Method(typeof(AstroUploads), nameof(AstroUploads.Queue));
        var count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original)) { instruction.opcode = OpCodes.Call; instruction.operand = replacement; count++; }
            yield return instruction;
        }
        if (count != 2) throw new InvalidOperationException("SectorModel astro upload calls changed: " + count);
    }
}

internal static class AstroPreparation
{
    private sealed class Layout
    {
        public readonly int[] Ids, RenderIds;
        public Layout(GalaxyData galaxy)
        {
            var ids = new List<int>();
            foreach (var star in galaxy.stars)
            {
                ids.Add(star.astroId);
                foreach (var planet in star.planets) ids.Add(planet.astroId);
            }
            Ids = ids.ToArray();
            var map = GalaxyRenderAstros.For(galaxy);
            if (map == null) return;
            RenderIds = new int[Ids.Length];
            for (var i = 0; i < Ids.Length; i++) RenderIds[i] = (int)map.RenderId((uint)Ids[i]);
        }
    }
    private sealed class SectorState { public int Cursor; public byte[] Slots; }
    private static readonly ConditionalWeakTable<GalaxyData, Layout> layouts = new();
    private static readonly ConditionalWeakTable<AstroPoseR[], AstroPoseR[]> renderCopies = new();
    private static readonly ConditionalWeakTable<AstroPoseR[], SectorState> sectorStates = new();

    // Hoist quaternion conversion and its shared scalar without reassociating the
    // native double arithmetic. CPU collision poses retain the original rounding.
    internal readonly struct RelativeTransform
    {
        private readonly double x, y, z, w, scale;
        internal readonly Quaternion Inverse;
        internal RelativeTransform(Quaternion rotation)
        {
            x = rotation.x; y = rotation.y; z = rotation.z; w = rotation.w;
            scale = (double)(rotation.w * rotation.w) - 0.5;
            rotation.w = -rotation.w; Inverse = rotation;
        }
        internal VectorLF3 PositionLF(VectorLF3 value)
        {
            value.x *= 2.0; value.y *= 2.0; value.z *= 2.0;
            var dot = x * value.x + y * value.y + z * value.z;
            return new VectorLF3(
                value.x * scale - (y * value.z - z * value.y) * w + x * dot,
                value.y * scale - (z * value.x - x * value.z) * w + y * dot,
                value.z * scale - (x * value.y - y * value.x) * w + z * dot);
        }
        internal Vector3 Position(VectorLF3 value) => PositionLF(value);
    }

    internal static long GalaxyUpdates;
    internal static AstroPoseR[] Galaxy(SectorModel model, bool starmap, VectorLF3 origin, bool deferMirror = false)
    {
        var layout = layouts.GetValue(model.gameData.galaxy, galaxy => new Layout(galaxy));
        var source = model.sector.galaxyAstros;
        var output = starmap ? model.starmapGalaxyAstroArr : model.galaxyAstroArr;
        // Aliases occupy unused canonical slots. They must never enter the CPU
        // collision array, whose per-star scans stop at an empty planet slot.
        var render = layout.RenderIds == null || Multiplayer.IsDedicated ? output :
            renderCopies.GetValue(output, poses => (AstroPoseR[])poses.Clone());
        var mirrored = !ReferenceEquals(render, output);
        var transform = new RelativeTransform(model.gameData.relativeRot);
        GalaxyWork.Run(layout.Ids.Length, (start, end) =>
        {
            for (var i = start; i < end; i++)
            {
                var id = layout.Ids[i];
                ref var input = ref source[id];
                ref var result = ref output[id];
                if (starmap) { result.rpos = input.uPos - origin; result.rrot = input.uRot; }
                else { result.rpos = transform.Position(input.uPos - origin); result.rrot = transform.Inverse * input.uRot; }
                result.radius = input.uRadius;
                if (mirrored && !deferMirror)
                {
                    render[id] = result;
                    // All render IDs are unique and aliases cannot overlap real IDs.
                    if (layout.RenderIds[i] != id) render[layout.RenderIds[i]] = result;
                }
            }
        }, 1024);
        GalaxyUpdates++;
        return render;
    }

    internal static void Mirror(SectorModel model, AstroPoseR[] render)
    {
        var output = model.galaxyAstroArr;
        if (ReferenceEquals(output, render)) return;
        var layout = layouts.GetValue(model.gameData.galaxy, galaxy => new Layout(galaxy));
        GalaxyWork.Run(layout.Ids.Length, (start, end) =>
        {
            for (var i = start; i < end; i++)
            {
                var id = layout.Ids[i];
                render[id] = output[id];
                if (layout.RenderIds[i] != id) render[layout.RenderIds[i]] = output[id];
            }
        }, 4096);
    }

    internal static int Sector(SectorModel model, bool starmap, VectorLF3 origin)
    {
        var source = model.sector.astros;
        var output = starmap ? model.starmapAstroArr : model.astroArr;
        var transform = new RelativeTransform(model.gameData.relativeRot);
        var cursor = model.sector.astroCursor;
        var state = sectorStates.GetValue(output, poses =>
        {
            // Also handle an array previously populated by the native path.
            Array.Clear(poses, 0, poses.Length);
            return new SectorState { Slots = new byte[poses.Length] };
        });
        var count = Math.Max(cursor, state.Cursor);
        if (cursor < state.Cursor)
        {
            Array.Clear(output, cursor, state.Cursor - cursor);
            Array.Clear(state.Slots, cursor, state.Cursor - cursor);
        }
        output[0] = default;
        GalaxyWork.Run(cursor, (start, end) =>
        {
            for (var i = Math.Max(1, start); i < end; i++)
            {
                ref var input = ref source[i];
                ref var result = ref output[i];
                if (input.id > 0)
                {
                    if (starmap) { result.rpos = input.uPos - origin; result.rrot = input.uRot; }
                    else { result.rpos = transform.Position(input.uPos - origin); result.rrot = transform.Inverse * input.uRot; }
                    result.radius = input.uRadius;
                    state.Slots[i] = 2;
                }
                // Preserve the supported game's starmap-side write to the world array.
                else
                {
                    if (!GalaxyOptimization.Enabled || state.Slots[i] != 1) result = default;
                    if (starmap) model.astroArr[i].rrot.w = 1f;
                    else result.rrot.w = 1f;
                    state.Slots[i] = 1;
                }
            }
        }, 4096);
        state.Cursor = cursor;
        if (!starmap) SpaceRayCandidates.Observe(model);
        return count;
    }
}

// Tracks the GPU's previous live prefix, including shrinkage across coalesced ticks.
// A new buffer/source receives one full upload to initialize its unused tail.
internal sealed class AstroUploadRange
{
    private Array source;
    private int previous, latest;
    private bool initialized;
    internal int Count { get; private set; }
    internal void Stage(Array data, int count)
    {
        if (count < 0 || count > data.Length) throw new ArgumentOutOfRangeException(nameof(count));
        if (!ReferenceEquals(source, data)) { source = data; initialized = false; Count = previous = 0; }
        latest = count;
        Count = Math.Max(Count, initialized ? Math.Max(previous, count) : data.Length);
    }
    internal void Complete() { previous = latest; initialized = true; Count = 0; }
    internal void Invalidate() { initialized = false; Count = 0; }
}

internal static class AstroUploads
{
    private sealed class Pending
    {
        public ComputeBuffer Buffer;
        public Array Data;
        public bool Queued;
        public SectorModel MirrorModel;
        public readonly AstroUploadRange Range = new();
    }
    private static readonly ConditionalWeakTable<ComputeBuffer, Pending> states = new();
    private static readonly List<Pending> pending = new();
    public static long Requests { get; private set; }
    public static long Uploads { get; private set; }
    public static long HeadlessSkips { get; private set; }
    public static long UploadedElements { get; private set; }
    public static long RequestedElements { get; private set; }

    // Near-full partial writes can lose the driver's full-buffer discard path.
    // Use ranges only when they remove a meaningful part of the transfer.
    internal static int UploadCount(int count, int capacity) => count >= capacity - capacity / 4 ? capacity : count;

    internal static void Queue(ComputeBuffer buffer, Array data) => QueueRange(buffer, data, data.Length);

    internal static void QueueGalaxy(ComputeBuffer buffer, AstroPoseR[] data, SectorModel model)
    {
        Queue(buffer, data);
        if (!Multiplayer.IsDedicated && GalaxyOptimization.Enabled)
            states.GetOrCreateValue(buffer).MirrorModel = model;
    }

    internal static void QueueRange(ComputeBuffer buffer, Array data, int count)
    {
        Requests++;
        if (Multiplayer.IsDedicated) { HeadlessSkips++; return; }
        var state = states.GetOrCreateValue(buffer);
        state.Range.Stage(data, count);
        state.Buffer = buffer; state.Data = data;
        if (state.Queued) return;
        state.Queued = true; pending.Add(state);
    }

    internal static void Flush()
    {
        if (pending.Count == 0) return;
        try
        {
            foreach (var state in pending)
            {
                if (!Multiplayer.IsDedicated && state.Buffer.IsValid())
                {
                    if (state.MirrorModel != null) AstroPreparation.Mirror(state.MirrorModel, (AstroPoseR[])state.Data);
                    var count = UploadCount(state.Range.Count, state.Data.Length);
                    if (count == state.Data.Length) state.Buffer.SetData(state.Data);
                    else state.Buffer.SetData(state.Data, 0, 0, count);
                    Uploads++; UploadedElements += count; RequestedElements += state.Data.Length;
                    state.Range.Complete();
                }
            }
        }
        finally
        {
            foreach (var state in pending)
            {
                if (state.Range.Count != 0) state.Range.Invalidate();
                state.Queued = false; state.Buffer = null; state.Data = null; state.MirrorModel = null;
            }
            pending.Clear();
        }
    }

    internal static void Discard(ComputeBuffer buffer)
    {
        if (buffer == null || !states.TryGetValue(buffer, out var state)) return;
        pending.Remove(state);
        state.Queued = false; state.Buffer = null; state.Data = null; state.MirrorModel = null;
        states.Remove(buffer);
    }
}

[HarmonyPatch]
internal static class AstroUploadConsumers_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SectorModel), "Update");
        yield return AccessTools.Method(typeof(SectorModel), "LateUpdate");
        yield return AccessTools.Method(typeof(SectorModel), nameof(SectorModel.DrawInstancedBatches));
        yield return AccessTools.Method(typeof(SectorModel), nameof(SectorModel.DrawStarmapBatches));
        yield return AccessTools.Method(typeof(GameLogic), nameof(GameLogic.Draw));
        yield return AccessTools.Method(typeof(GameLogic), nameof(GameLogic.DrawPost));
    }
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix() => AstroUploads.Flush();
}

[HarmonyPatch(typeof(SectorModel), nameof(SectorModel.UpdateStarmapAstroBuffer))]
internal static class StarmapAstroBuffer_Patch
{
    private sealed class Snapshot
    {
        public long Tick = -1;
        public SpaceSector Sector;
        public AstroData[] GalaxySource, SectorSource;
        public AstroPoseR[] GalaxyOutput, SectorOutput;
        public ComputeBuffer GalaxyBuffer, SectorBuffer;
        public VectorLF3 Origin;
        public readonly List<int> Ids = new();
        public readonly List<AstroData> Galaxy = new();
        public AstroData[] Astros = Array.Empty<AstroData>();
        public int Cursor;

        public bool Matches(SectorModel model, VectorLF3 origin)
        {
            if (Tick != GameMain.gameTick || !SameBits(Origin, origin) || !ReferenceEquals(Sector, model.sector) ||
                !ReferenceEquals(GalaxySource, model.sector.galaxyAstros) || !ReferenceEquals(SectorSource, model.sector.astros) ||
                !ReferenceEquals(GalaxyOutput, model.starmapGalaxyAstroArr) || !ReferenceEquals(SectorOutput, model.starmapAstroArr) ||
                !ReferenceEquals(GalaxyBuffer, model.starmapGalaxyAstroBuffer) || !ReferenceEquals(SectorBuffer, model.starmapAstroBuffer) ||
                Cursor != model.sector.astroCursor) return false;
            var index = 0;
            foreach (var star in model.gameData.galaxy.stars)
            {
                if (!MatchesGalaxy(star.astroId, ref index)) return false;
                foreach (var planet in star.planets) if (!MatchesGalaxy(planet.astroId, ref index)) return false;
            }
            if (index != Galaxy.Count) return false;
            for (var i = 1; i < Cursor; i++) if (!SameBits(Astros[i], SectorSource[i])) return false;
            return true;
        }

        private bool MatchesGalaxy(int id, ref int index)
        {
            if (index >= Galaxy.Count || Ids[index] != id || !SameBits(Galaxy[index], GalaxySource[id])) return false;
            index++; return true;
        }

        public void Capture(SectorModel model, VectorLF3 origin)
        {
            Tick = GameMain.gameTick; Origin = origin; Sector = model.sector;
            GalaxySource = Sector.galaxyAstros; SectorSource = Sector.astros;
            GalaxyOutput = model.starmapGalaxyAstroArr; SectorOutput = model.starmapAstroArr;
            GalaxyBuffer = model.starmapGalaxyAstroBuffer; SectorBuffer = model.starmapAstroBuffer;
            Galaxy.Clear(); Ids.Clear();
            foreach (var star in model.gameData.galaxy.stars)
            {
                Ids.Add(star.astroId); Galaxy.Add(GalaxySource[star.astroId]);
                foreach (var planet in star.planets) { Ids.Add(planet.astroId); Galaxy.Add(GalaxySource[planet.astroId]); }
            }
            Cursor = Sector.astroCursor;
            if (Astros.Length < Cursor) Astros = new AstroData[Cursor];
            Array.Copy(SectorSource, Astros, Cursor);
        }
    }
    private static readonly ConditionalWeakTable<SectorModel, Snapshot> snapshots = new();
    public static long ReusedUpdates { get; private set; }
    internal static void Forget(SectorModel model) => snapshots.Remove(model);

    [HarmonyPrefix]
    internal static bool Prefix(SectorModel __instance, VectorLF3 starmapCamPos, out bool __state)
    {
        __state = false;
        if (Multiplayer.IsDedicated) return false;
        if (__instance.gameData.galaxy.starCount >= GalaxyWork.ParallelThreshold)
        {
            // Compute directly in parallel. A full snapshot taken after every tick costs more
            // than it saves in these worlds, where simulation advances on every rendered frame.
            var galaxy = AstroPreparation.Galaxy(__instance, true, starmapCamPos);
            AstroUploads.Queue(__instance.starmapGalaxyAstroBuffer, galaxy);
            Shader.SetGlobalBuffer("_Global_Starmap_GalaxyAstroBuffer", __instance.starmapGalaxyAstroBuffer);
            var count = AstroPreparation.Sector(__instance, true, starmapCamPos);
            AstroUploads.QueueRange(__instance.starmapAstroBuffer, __instance.starmapAstroArr, count);
            // Starmap culling consumes these buffers before returning to Update.
            AstroUploads.Flush();
            return false;
        }
        var snapshot = snapshots.GetOrCreateValue(__instance);
        if (snapshot.Matches(__instance, starmapCamPos)) { ReusedUpdates++; return false; }
        __state = true;
        return true;
    }

    [HarmonyPostfix]
    internal static void Postfix(SectorModel __instance, VectorLF3 starmapCamPos, bool __state)
    {
        if (__state) snapshots.GetOrCreateValue(__instance).Capture(__instance, starmapCamPos);
    }

    internal static unsafe bool SameBits<T>(T a, T b) where T : unmanaged
    {
        var left = (byte*)&a; var right = (byte*)&b;
        var i = 0;
        for (; i + 8 <= sizeof(T); i += 8) if (*(long*)(left + i) != *(long*)(right + i)) return false;
        for (; i < sizeof(T); i++) if (left[i] != right[i]) return false;
        return true;
    }
}

[HarmonyPatch]
internal static class GalaxyAstroLifetime_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SectorModel), nameof(SectorModel.Free));
        yield return AccessTools.Method(typeof(SectorModel), nameof(SectorModel.FreeGalaxyAstroBuffer));
        yield return AccessTools.Method(typeof(SectorModel), nameof(SectorModel.FreeAstroBuffer));
    }
    [HarmonyPrefix]
    internal static void Prefix(SectorModel __instance, MethodBase __originalMethod)
    {
        if (__originalMethod.Name != nameof(SectorModel.FreeAstroBuffer))
        {
            AstroUploads.Discard(__instance.galaxyAstroBuffer);
            AstroUploads.Discard(__instance.starmapGalaxyAstroBuffer);
        }
        if (__originalMethod.Name != nameof(SectorModel.FreeGalaxyAstroBuffer))
        {
            AstroUploads.Discard(__instance.astroBuffer);
            AstroUploads.Discard(__instance.starmapAstroBuffer);
        }
        StarmapAstroBuffer_Patch.Forget(__instance);
    }
}
