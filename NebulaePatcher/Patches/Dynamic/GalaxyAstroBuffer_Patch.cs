using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NebulaeWorld;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic;

/// <summary>CPU poses stay synchronous for SpaceColliderLogic; GPU copies wait for a render consumer.</summary>
[HarmonyPatch(typeof(SectorModel), nameof(SectorModel.UpdateAstroBuffer))]
internal static class GalaxyAstroBuffer_Patch
{
    [HarmonyPrefix]
    internal static bool Prefix(SectorModel __instance)
    {
        if (__instance.gameData.galaxy.starCount < GalaxyWork.ParallelThreshold) return true;
        AstroPreparation.Galaxy(__instance, false, __instance.gameData.relativePos);
        AstroUploads.Queue(__instance.galaxyAstroBuffer, __instance.galaxyAstroArr);
        if (!Multiplayer.IsDedicated) Shader.SetGlobalBuffer("_Global_GalaxyAstroBuffer", __instance.galaxyAstroBuffer);
        AstroPreparation.Sector(__instance, false, __instance.gameData.relativePos);
        AstroUploads.Queue(__instance.astroBuffer, __instance.astroArr);
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
    internal static long GalaxyUpdates;
    internal static void Galaxy(SectorModel model, bool starmap, VectorLF3 origin)
    {
        var stars = model.gameData.galaxy.stars;
        var source = model.sector.galaxyAstros;
        var output = starmap ? model.starmapGalaxyAstroArr : model.galaxyAstroArr;
        var rotation = model.gameData.relativeRot;
        var inverse = rotation; inverse.w = -inverse.w;
        GalaxyWork.Run(stars.Length, (start, end) =>
        {
            for (var i = start; i < end; i++)
            {
                Write(stars[i].astroId);
                foreach (var planet in stars[i].planets) Write(planet.astroId);
            }
            void Write(int id)
            {
                ref var input = ref source[id];
                ref var result = ref output[id];
                if (starmap) { result.rpos = input.uPos - origin; result.rrot = input.uRot; }
                else { result.rpos = Maths.QInvRotateLF(rotation, input.uPos - origin); result.rrot = inverse * input.uRot; }
                result.radius = input.uRadius;
            }
        });
        GalaxyRenderAstros.For(model.gameData.galaxy)?.Mirror(output);
        GalaxyUpdates++;
    }

    internal static void Sector(SectorModel model, bool starmap, VectorLF3 origin)
    {
        var source = model.sector.astros;
        var output = starmap ? model.starmapAstroArr : model.astroArr;
        var rotation = model.gameData.relativeRot;
        var inverse = rotation; inverse.w = -inverse.w;
        Array.Clear(output, 0, output.Length);
        GalaxyWork.Run(model.sector.astroCursor, (start, end) =>
        {
            for (var i = Math.Max(1, start); i < end; i++)
            {
                ref var input = ref source[i];
                ref var result = ref output[i];
                if (input.id > 0)
                {
                    if (starmap) { result.rpos = input.uPos - origin; result.rrot = input.uRot; }
                    else { result.rpos = Maths.QInvRotateLF(rotation, input.uPos - origin); result.rrot = inverse * input.uRot; }
                    result.radius = input.uRadius;
                }
                // Preserve the supported game's starmap-side write to the world array.
                else if (starmap) model.astroArr[i].rrot.w = 1f;
                else result.rrot.w = 1f;
            }
        });
    }
}

internal static class AstroUploads
{
    private sealed class Pending
    {
        public ComputeBuffer Buffer;
        public Array Data;
        public bool Queued;
    }
    private static readonly ConditionalWeakTable<ComputeBuffer, Pending> states = new();
    private static readonly List<Pending> pending = new();
    public static long Requests { get; private set; }
    public static long Uploads { get; private set; }
    public static long HeadlessSkips { get; private set; }

    internal static void Queue(ComputeBuffer buffer, Array data)
    {
        Requests++;
        if (Multiplayer.IsDedicated) { HeadlessSkips++; return; }
        var state = states.GetOrCreateValue(buffer);
        state.Buffer = buffer; state.Data = data;
        if (state.Queued) return;
        state.Queued = true; pending.Add(state);
    }

    internal static void Flush()
    {
        try
        {
            foreach (var state in pending)
            {
                if (!Multiplayer.IsDedicated && state.Buffer.IsValid())
                { state.Buffer.SetData(state.Data); Uploads++; }
            }
        }
        finally
        {
            foreach (var state in pending) { state.Queued = false; state.Buffer = null; state.Data = null; }
            pending.Clear();
        }
    }

    internal static void Discard(ComputeBuffer buffer)
    {
        if (buffer == null || !states.TryGetValue(buffer, out var state)) return;
        pending.Remove(state);
        state.Queued = false; state.Buffer = null; state.Data = null;
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
            AstroPreparation.Galaxy(__instance, true, starmapCamPos);
            __instance.starmapGalaxyAstroBuffer.SetData(__instance.starmapGalaxyAstroArr);
            Shader.SetGlobalBuffer("_Global_Starmap_GalaxyAstroBuffer", __instance.starmapGalaxyAstroBuffer);
            AstroPreparation.Sector(__instance, true, starmapCamPos);
            __instance.starmapAstroBuffer.SetData(__instance.starmapAstroArr);
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

[HarmonyPatch(typeof(SectorModel), nameof(SectorModel.Free))]
internal static class GalaxyAstroLifetime_Patch
{
    [HarmonyPrefix]
    internal static void Prefix(SectorModel __instance)
    {
        AstroUploads.Discard(__instance.galaxyAstroBuffer);
        AstroUploads.Discard(__instance.astroBuffer);
        StarmapAstroBuffer_Patch.Forget(__instance);
    }
}
