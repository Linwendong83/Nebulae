using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIStarmap), "_OnLateUpdate")]
internal static class StarmapDisplayFrame_Patch
{
    internal static UIStarmap Current;
    internal static Vector3 Position, Forward;
    internal static RectTransform Canvas;
    internal static Camera OverlayCamera;
    internal static readonly Plane[] Planes = new Plane[6];

    [HarmonyPrefix]
    internal static void Prefix(UIStarmap __instance)
    {
        Current = null;
        // Unity does not guarantee the relative LateUpdate order of UIRoot and
        // StarmapCamera. Project labels only after this frame's camera pose.
        if (__instance.screenCameraController != null)
            StarmapCameraPose_Patch.Refresh(__instance.screenCameraController);
        if (UIStarmap.isChangingToMilkyWay || __instance.screenCamera == null) return;
        Position = __instance.screenCamera.transform.position;
        Forward = __instance.screenCamera.transform.forward;
        GeometryUtility.CalculateFrustumPlanes(__instance.screenCamera, Planes);
        Canvas = __instance.screenCanvas.transform as RectTransform;
        OverlayCamera = UIRoot.instance.overlayCanvas.worldCamera;
        StarmapDisplay_Patch.BeginFrame(__instance);
        Current = __instance;
    }
    [HarmonyFinalizer]
    internal static void Finalizer() { Current = null; StarmapDisplay_Patch.EndFrame(); }

    internal static bool Project(Camera camera, Vector3 worldPoint, out Vector2 point, out Vector3 screen)
    {
        point = new Vector2(-3000f, -3000f);
        screen = camera.WorldToScreenPoint(worldPoint);
        if (screen.z < 0.001f) return false;
        var ray = OverlayCamera.ScreenPointToRay(screen);
        ray.origin = Canvas.InverseTransformPoint(ray.origin);
        ray.direction = Canvas.InverseTransformVector(ray.direction);
        if (!new Plane(Vector3.forward, 0f).Raycast(ray, out var enter) || enter < 0.001f) return false;
        point.x = ray.origin.x + ray.direction.x * enter;
        point.y = ray.origin.y + ray.direction.y * enter;
        return true;
    }
}

[HarmonyPatch(typeof(StarmapCamera), "LateUpdate")]
internal static class StarmapCameraPose_Patch
{
    private sealed class Frame { public int Number = -1; }
    private static readonly ConditionalWeakTable<StarmapCamera, Frame> frames = new();

    internal static void Refresh(StarmapCamera camera)
    {
        var frame = frames.GetOrCreateValue(camera);
        if (frame.Number == Time.frameCount) return;
        camera.RefreshCameraPose();
        frame.Number = Time.frameCount;
    }

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var refresh = AccessTools.Method(typeof(StarmapCamera), nameof(StarmapCamera.RefreshCameraPose));
        var count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(refresh))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(StarmapCameraPose_Patch), nameof(Refresh));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Starmap camera pose update changed.");
    }

    [HarmonyPostfix, HarmonyPatch(typeof(StarmapCamera), "OnEnable")]
    internal static void Reset(StarmapCamera __instance) => frames.Remove(__instance);
}

/// <summary>The original geometry/interaction data is maintained even outside the camera frustum.</summary>
[HarmonyPatch(typeof(UIStarmapStar), "_OnLateUpdate")]
internal static class StarmapDisplay_Patch
{
    private sealed class Entry
    {
        public Transform Root, Spot;
        public UIStarmapStarObject Owner;
        public bool Culled;
        public float Radius;
        public long ProjectionVersion = -1;
        public Vector3 ProjectedWorld, Screen;
        public Vector2 Point;
        public bool Projected;
        private bool radiusKnown;
        private float starRadius, radiusPower;
        public Entry(UIStarmapStarObject obj)
        {
            Owner = obj; Root = obj.transform; Spot = obj.spotObj.transform;
            RefreshBounds(obj);
        }
        public void RefreshBounds(UIStarmapStarObject obj)
        {
            var rootScale = Root.lossyScale;
            if (Mathf.Abs(rootScale.x) < 1e-12f || Mathf.Abs(rootScale.y) < 1e-12f || Mathf.Abs(rootScale.z) < 1e-12f)
            { Radius = float.PositiveInfinity; return; }
            Radius = 1f;
            foreach (var renderer in obj.GetComponentsInChildren<Renderer>(true))
            {
                var relative = Root.worldToLocalMatrix * renderer.localToWorldMatrix;
                var bounds = renderer.localBounds;
                // Triangle inequality bounds every rotation of each child renderer, including billboards.
                var scale = Mathf.Sqrt(relative.GetColumn(0).sqrMagnitude + relative.GetColumn(1).sqrMagnitude + relative.GetColumn(2).sqrMagnitude);
                Radius = Mathf.Max(Radius, relative.MultiplyPoint3x4(Vector3.zero).magnitude +
                    (bounds.center.magnitude + bounds.extents.magnitude) * scale);
            }
        }
        public float RadiusPower(float value)
        {
            if (!radiusKnown || starRadius != value)
            { starRadius = value; radiusPower = (float)Math.Pow(Mathf.Max(1f, value), 0.15000000596046448); radiusKnown = true; }
            return radiusPower;
        }
    }
    private sealed class MapEntries
    {
        public UIStarmapStar[] Source;
        public Entry[] Entries;
        public Matrix4x4 View, Projection, OverlayView, OverlayProjection, Canvas;
        public Rect Pixels, OverlayPixels;
        public long Version;
    }
    private static readonly ConditionalWeakTable<UIStarmap, MapEntries> maps = new();
    private static MapEntries frame;
    private static Entry activeEntry;
    internal static void BeginFrame(UIStarmap map)
    {
        frame = maps.GetOrCreateValue(map);
        if (frame.Entries == null || !ReferenceEquals(frame.Source, map.starUIs))
        { frame.Source = map.starUIs; frame.Entries = new Entry[map.starUIs?.Length ?? 0]; }
        var camera = map.screenCamera;
        var overlay = StarmapDisplayFrame_Patch.OverlayCamera;
        var view = camera.worldToCameraMatrix; var projection = camera.projectionMatrix;
        var overlayView = overlay.worldToCameraMatrix; var overlayProjection = overlay.projectionMatrix;
        var canvas = StarmapDisplayFrame_Patch.Canvas.worldToLocalMatrix;
        var pixels = camera.pixelRect; var overlayPixels = overlay.pixelRect;
        if (frame.Version == 0 || !StarmapAstroBuffer_Patch.SameBits(frame.View, view) ||
            !StarmapAstroBuffer_Patch.SameBits(frame.Projection, projection) ||
            !StarmapAstroBuffer_Patch.SameBits(frame.OverlayView, overlayView) ||
            !StarmapAstroBuffer_Patch.SameBits(frame.OverlayProjection, overlayProjection) ||
            !StarmapAstroBuffer_Patch.SameBits(frame.Canvas, canvas) ||
            !StarmapAstroBuffer_Patch.SameBits(frame.Pixels, pixels) || !StarmapAstroBuffer_Patch.SameBits(frame.OverlayPixels, overlayPixels))
        {
            frame.View = view; frame.Projection = projection; frame.OverlayView = overlayView;
            frame.OverlayProjection = overlayProjection; frame.Canvas = canvas;
            frame.Pixels = pixels; frame.OverlayPixels = overlayPixels; frame.Version++;
        }
    }
    internal static void EndFrame() { frame = null; activeEntry = null; }
    public static long CulledPresentations { get; private set; }
    public static long ReusedProjections { get; private set; }

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var project = AccessTools.Method(typeof(UIStarmap), nameof(UIStarmap.WorldPointIntoScreen));
        var screen = AccessTools.Method(typeof(Camera), nameof(Camera.WorldToScreenPoint), new[] { typeof(Vector3) });
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(project)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(StarmapDisplay_Patch), nameof(Project)); }
            else if (instruction.Calls(screen)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(StarmapDisplay_Patch), nameof(ScreenPoint)); }
            yield return instruction;
        }
    }

    internal static bool Project(UIStarmap map, Vector3 world, out Vector2 point)
    {
        var entry = activeEntry;
        if (entry == null || frame == null || !ReferenceEquals(map, StarmapDisplayFrame_Patch.Current))
            return map.WorldPointIntoScreen(world, out point);
        if (entry.ProjectionVersion == frame.Version && StarmapAstroBuffer_Patch.SameBits(entry.ProjectedWorld, world))
        { point = entry.Point; ReusedProjections++; return entry.Projected; }
        entry.Projected = StarmapDisplayFrame_Patch.Project(map.screenCamera, world, out entry.Point, out entry.Screen);
        entry.ProjectedWorld = world; entry.ProjectionVersion = frame.Version;
        point = entry.Point;
        return entry.Projected;
    }

    internal static Vector3 ScreenPoint(Camera camera, Vector3 world)
    {
        if (activeEntry != null && frame != null && camera == StarmapDisplayFrame_Patch.Current?.screenCamera &&
            activeEntry.ProjectionVersion == frame.Version && StarmapAstroBuffer_Patch.SameBits(activeEntry.ProjectedWorld, world))
            return activeEntry.Screen;
        return camera.WorldToScreenPoint(world);
    }

    [HarmonyFinalizer]
    internal static void EndEntry() => activeEntry = null;

    [HarmonyPrefix]
    internal static bool Prefix(UIStarmapStar __instance)
    {
        var ui = __instance;
        var map = ui.starmap;
        if (map == null || ui.star == null || !ReferenceEquals(StarmapDisplayFrame_Patch.Current, map)) return true;
        var index = ui.star.index;
        if (frame == null || (uint)index >= (uint)frame.Entries.Length) return true;
        var entry = frame.Entries[index];
        if (entry == null || !ReferenceEquals(entry.Owner, ui.starObject)) frame.Entries[index] = entry = new Entry(ui.starObject);
        activeEntry = entry;
        var wasCulled = entry.Culled;
        entry.Culled = false;
        // Marker rows can extend into the viewport independently of the star's renderer bounds.
        // Do not cache this decision: newly placed markers are visible on the same frame.
        foreach (var planet in ui.star.planets)
            if (planet?.factory?.digitalSystem?.markers?.count > 0) return true;
        if (!CullObject(ui.starObject, entry)) return true;
        var camera = map.screenCamera;
        var projected = Project(map, ui.starObject.vpos, out var point);
        var screen = entry.Screen;
        if (projected && Physics.Raycast(camera.ScreenPointToRay(screen),
                out var hit, 200f, 1048576, QueryTriggerInteraction.Ignore) && hit.collider.gameObject != ui.starObject.gameObject)
            projected = false;
        ui.projected = projected;
        ui.projectedCoord = new Vector2(Mathf.Round(point.x), Mathf.Round(point.y));
        if (!wasCulled)
        {
            VisualWrites.Active(ui.nameText.gameObject, false);
            ui.markerIcons._Close();
        }
        entry.Culled = true;
        return false;
    }

    private static bool CullObject(UIStarmapStarObject obj, Entry entry)
    {
        var map = obj.starmap;
        if (map == null || obj.star == null || UIStarmap.isChangingToMilkyWay) return false;
        var star = obj.star;
        // Keep focused, hovered, pinned and explicitly viewed entries on the complete native path.
        if (map.viewStar == star || map.viewStarSystem == star || map.focusStar?.star == star ||
            map.mouseHoverStar?.star == star || GameMain.history.GetStarPin(star.id) != EPin.Auto) return false;
        if (float.IsInfinity(entry.Radius)) entry.RefreshBounds(obj);
        var vpos = (Vector3)((star.uPosition - map.viewTargetUPos) * 0.00025);
        obj.vpos = vpos;
        obj.vdist = (obj.vpos - StarmapDisplayFrame_Patch.Position).magnitude;
        var radiusPower = entry.RadiusPower(star.radius);
        var ratio = obj.vdist / Mathf.Max(0.0001f, obj.vradius);
        var normalizedDistance = ratio / (160f / radiusPower);
        var scale = Mathf.Max(1f, (float)Math.Pow(normalizedDistance, 0.7));
        var size = new Vector3(scale * obj.vradius * 2f, scale * obj.vradius * 2f, scale * obj.vradius * 2f);
        var radius = Mathf.Max(0.001f, Mathf.Abs(size.x) * entry.Radius) + 2f;
        if (Intersects(StarmapDisplayFrame_Patch.Planes, vpos, radius)) return false;

        // Do not close the object, disable its collider, or alter the outer entry's projected flag.
        // The caller preserves projection and occlusion; marked and focused entries use the native path.
        obj.starObjPos = star.uPosition;
        obj.vscale = size;
        entry.Root.localPosition = vpos;
        entry.Spot.forward = StarmapDisplayFrame_Patch.Forward;
        if (entry.Root.localScale - obj.vscale != Vector3.zero) entry.Root.localScale = obj.vscale;
        CulledPresentations++;
        return true;
    }

    internal static bool Intersects(Plane[] planes, Vector3 center, float radius)
    {
        foreach (var plane in planes)
            if (plane.GetDistanceToPoint(center) < -radius) return false;
        return true;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(UIStarmapStarObject), "_OnFree")]
    internal static void Free(UIStarmapStarObject __instance)
    {
        var obj = __instance;
        if (obj.starmap != null && obj.star != null && maps.TryGetValue(obj.starmap, out var state) &&
            (uint)obj.star.index < (uint)state.Entries.Length) state.Entries[obj.star.index] = null;
    }
    [HarmonyPostfix, HarmonyPatch(typeof(UIStarmapStar), "_OnOpen")]
    internal static void Opened(UIStarmapStar __instance)
    {
        var ui = __instance;
        if (ui.starmap != null && ui.star != null && maps.TryGetValue(ui.starmap, out var state) &&
            (uint)ui.star.index < (uint)state.Entries.Length && state.Entries[ui.star.index] is { } entry) entry.Culled = false;
    }
    [HarmonyPostfix, HarmonyPatch(typeof(UIStarmap), "_OnFree")]
    internal static void FreeMap(UIStarmap __instance)
    {
        maps.Remove(__instance);
        if (ReferenceEquals(StarmapDisplayFrame_Patch.Current, __instance)) { StarmapDisplayFrame_Patch.Current = null; frame = null; }
    }
}
