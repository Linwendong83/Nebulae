using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic.World;

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
        if (UIStarmap.isChangingToMilkyWay)
        { StarmapDisplay_Patch.Invalidate(__instance); return; }
        if (__instance.screenCamera == null) return;
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
        public GameObject Mass, Black;
        public long GeometryFrame = -1;
        public bool OpticalKnown, GeometryKnown, SpotKnown, MassKnown;
        public float OpticalRatio, ScaleFactor, SpotMultiplier, MassMultiplier, LastSpot, LastMass;
        public Vector3 LastPosition, LastScale, LastForward;
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
            Mass = obj.massObj.gameObject; Black = obj.blackObj.gameObject;
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
            { starRadius = value; radiusPower = (float)Math.Pow(Mathf.Max(1f, value), 0.15000000596046448); radiusKnown = true; OpticalKnown = false; }
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
        public long GeometryFrame;
        public VectorLF3 EpochViewTarget;
        public Vector3 LastCameraPosition, LastForward;
        public bool EpochKnown;
        public bool RedoGeometry;
        public bool WroteGeometry;
    }
    private static ConditionalWeakTable<UIStarmap, MapEntries> maps = new();
    private static MapEntries frame;
    private static Entry activeEntry;
    internal static void BeginFrame(UIStarmap map)
    {
        frame = maps.GetOrCreateValue(map);
        frame.GeometryFrame++;
        if (frame.Entries == null || !ReferenceEquals(frame.Source, map.starUIs))
        { frame.Source = map.starUIs; frame.Entries = new Entry[map.starUIs?.Length ?? 0]; frame.EpochKnown = false; }
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
        // The whole pass is a pure function of the camera position and the view target; stars do not
        // move. A bit-identical pair therefore reproduces every position, scale and collider pose,
        // and the per-star work can be skipped as one decision. The view target is read once per
        // frame instead of once per star: it rotates the local target into universal space.
        var viewTarget = map.viewTargetUPos;
        var cameraPosition = StarmapDisplayFrame_Patch.Position;
        frame.RedoGeometry = !frame.EpochKnown || !PoseBits.Equal(frame.EpochViewTarget, viewTarget) ||
            !PoseBits.Equal(frame.LastCameraPosition, cameraPosition) ||
            !PoseBits.Equal(frame.LastForward, StarmapDisplayFrame_Patch.Forward);
        frame.EpochKnown = true; frame.EpochViewTarget = viewTarget;
        frame.LastCameraPosition = cameraPosition; frame.LastForward = StarmapDisplayFrame_Patch.Forward;
        frame.WroteGeometry = false;
        if (StellarPresentation.Active && frame.RedoGeometry)
        {
            // Move all collider roots before any star's label/occlusion ray. Interleaving
            // transform writes with thousands of raycasts repeatedly resynchronizes physics.
            foreach (var ui in map.starUIs)
            {
                if (ui == null || !ui.active || ui.star == null || ui.starObject == null || !ui.starObject.active) continue;
                var index = ui.star.index;
                if ((uint)index >= (uint)frame.Entries.Length) continue;
                var entry = frame.Entries[index];
                if (entry == null || !ReferenceEquals(entry.Owner, ui.starObject))
                    frame.Entries[index] = entry = new Entry(ui.starObject);
                PrepareGeometry(ui.starObject, entry);
                ApplyGeometry(ui.starObject, entry);
            }
            if (frame.WroteGeometry) Physics.SyncTransforms();
        }
        else if (StellarPresentation.Active) ReusedGeometry++;
    }
    internal static void EndFrame() { frame = null; activeEntry = null; }
    internal static void Reset() { maps = new(); EndFrame(); }
    internal static void Invalidate(UIStarmap map) => maps.Remove(map);
    public static long CulledPresentations { get; private set; }
    public static long ReusedProjections { get; private set; }
    public static long ReusedHoverScans { get; private set; }
    public static long ReusedGeometry { get; private set; }

    /// <summary>
    /// Stars whose cached screen point can still reach the hover radius, in native order. The
    /// native cursor test only ever selects a star closer than 600 squared pixels, so every
    /// other star leaves its distance and depth thresholds untouched.
    /// </summary>
    internal static UIStarmapStar[] HoverCandidates(UIStarmap map, Vector3 mouse, out int count)
    {
        count = 0;
        if (!StellarPresentation.Active || map == null) return null;
        var camera = map.screenCamera;
        if (camera == null || map.screenCanvas == null || UIRoot.instance?.overlayCanvas == null) return null;
        if (!maps.TryGetValue(map, out var state) || state.Version == 0 || state.Entries == null ||
            state.Source == null || !ReferenceEquals(state.Source, map.starUIs)) return null;
        // Cached points were produced by the last LateUpdate pose. Reuse them only while the
        // projection inputs are unchanged; otherwise the native full scan stays authoritative.
        var overlay = UIRoot.instance.overlayCanvas.worldCamera;
        if (overlay == null || !StarmapAstroBuffer_Patch.SameBits(state.View, camera.worldToCameraMatrix) ||
            !StarmapAstroBuffer_Patch.SameBits(state.Projection, camera.projectionMatrix) ||
            !StarmapAstroBuffer_Patch.SameBits(state.OverlayView, overlay.worldToCameraMatrix) ||
            !StarmapAstroBuffer_Patch.SameBits(state.OverlayProjection, overlay.projectionMatrix) ||
            !StarmapAstroBuffer_Patch.SameBits(state.Canvas, (map.screenCanvas.transform as RectTransform).worldToLocalMatrix) ||
            !StarmapAstroBuffer_Patch.SameBits(state.Pixels, camera.pixelRect) ||
            !StarmapAstroBuffer_Patch.SameBits(state.OverlayPixels, overlay.pixelRect)) return null;
        var stars = map.starUIs;
        if (stars == null) return null;
        if (screens.Length < stars.Length) screens = new Vector3[stars.Length];
        if (candidates.Length < stars.Length) candidates = new int[stars.Length];
        for (var i = 0; i < stars.Length; i++)
        {
            var ui = stars[i];
            if (ui == null) return null;
            var obj = ui.starObject;
            if (obj == null || ui.star == null) return null;
            var index = ui.star.index;
            if ((uint)index >= (uint)state.Entries.Length) return null;
            var entry = state.Entries[index];
            screens[i] = entry != null && ReferenceEquals(entry.Owner, obj) && entry.GeometryKnown &&
                entry.ProjectionVersion == state.Version && PoseBits.Equal(entry.ProjectedWorld, obj.vpos)
                ? entry.Screen
                : camera.WorldToScreenPoint(obj.transform.localPosition);
        }
        count = FilterHover(screens, stars.Length, mouse, candidates);
        ReusedHoverScans++;
        if (count == 0) return empty;
        if (selected.Length != count) selected = new UIStarmapStar[count];
        for (var i = 0; i < count; i++) selected[i] = stars[candidates[i]];
        return selected;
    }

    /// <summary>Indices of screen points the native cursor test could still select, in native order.</summary>
    internal static int FilterHover(Vector3[] points, int count, Vector3 mouse, int[] output)
    {
        var found = 0;
        for (var i = 0; i < count; i++)
        {
            var dx = points[i].x - mouse.x;
            var dy = points[i].y - mouse.y;
            if (dx * dx + dy * dy < HoverRadiusSquared) output[found++] = i;
        }
        return found;
    }

    // Native cursor test starts from 600 squared pixels and only shrinks it.
    internal const float HoverRadiusSquared = 600f;
    private static Vector3[] screens = Array.Empty<Vector3>();
    private static int[] candidates = Array.Empty<int>();
    private static UIStarmapStar[] selected = Array.Empty<UIStarmapStar>(), empty = Array.Empty<UIStarmapStar>();

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
        if (Important(map, star)) return false;
        if (float.IsInfinity(entry.Radius)) entry.RefreshBounds(obj);
        PrepareGeometry(obj, entry);
        var vpos = obj.vpos;
        var size = obj.vscale;
        var radius = Mathf.Max(0.001f, Mathf.Abs(size.x) * entry.Radius) + 2f;
        if (Intersects(StarmapDisplayFrame_Patch.Planes, vpos, radius)) return false;

        // Do not close the object, disable its collider, or alter the outer entry's projected flag.
        // The caller preserves projection and occlusion; marked and focused entries use the native path.
        ApplyGeometry(obj, entry);
        CulledPresentations++;
        return true;
    }

    private static bool Important(UIStarmap map, StarData star) => map.viewStar == star || map.viewStarSystem == star ||
        map.focusStar?.star == star || map.mouseHoverStar?.star == star || GameMain.history.GetStarPin(star.id) != EPin.Auto;

    private static void PrepareGeometry(UIStarmapStarObject obj, Entry entry)
    {
        if (StellarPresentation.Active)
        {
            // A skipped frame means the camera and the view target are bit-identical to the frame
            // that produced these values, so reusing them is exact rather than approximate.
            // Entries that never ran (new star objects, reopened rows) still compute.
            if (!frame.RedoGeometry && entry.GeometryKnown) return;
            if (entry.GeometryFrame == frame.GeometryFrame) return;
            entry.GeometryFrame = frame.GeometryFrame;
        }
        var star = obj.star;
        obj.starObjPos = star.uPosition;
        obj.vpos = (Vector3)((star.uPosition - frame.EpochViewTarget) * 0.00025);
        obj.vdist = (obj.vpos - StarmapDisplayFrame_Patch.Position).magnitude;
        var radiusPower = entry.RadiusPower(star.radius);
        var ratio = obj.vdist / Mathf.Max(0.0001f, obj.vradius);
        if (!StellarPresentation.Active || !entry.OpticalKnown || ratio <= 100f || Important(obj.starmap, star) ||
            !StellarPresentation.Close(entry.OpticalRatio, ratio))
        {
            entry.ScaleFactor = Mathf.Max(1f, (float)Math.Pow(ratio / (160f / radiusPower), 0.7));
            if (StellarPresentation.Active)
            {
                var spot = Mathf.Max(0f, (ratio - 15f) * 0.05f);
                entry.MassMultiplier = 4f * Mathf.Sqrt(Mathf.Clamp01(1f - spot));
                if (spot > 1f) { spot = Mathf.Log(spot) + 1f; spot = Mathf.Log(spot) + 1f; }
                entry.SpotMultiplier = 0.3f * Mathf.Min(2f, spot);
                entry.OpticalRatio = ratio; entry.OpticalKnown = true;
            }
        }
        var size = entry.ScaleFactor * obj.vradius * 2f;
        obj.vscale = new Vector3(size, size, size);
    }

    private static void ApplyGeometry(UIStarmapStarObject obj, Entry entry)
    {
        var forward = StarmapDisplayFrame_Patch.Forward;
        var known = StellarPresentation.Active && entry.GeometryKnown;
        if (!known || !PoseBits.Equal(entry.LastPosition, obj.vpos))
        { entry.Root.localPosition = obj.vpos; if (frame != null) frame.WroteGeometry = true; }
        if (!known || !PoseBits.Equal(entry.LastForward, forward))
        { entry.Spot.forward = forward; if (frame != null) frame.WroteGeometry = true; }
        if (!known || !PoseBits.Equal(entry.LastScale, obj.vscale))
        { entry.Root.localScale = obj.vscale; if (frame != null) frame.WroteGeometry = true; }
        entry.LastPosition = obj.vpos; entry.LastScale = obj.vscale; entry.LastForward = forward; entry.GeometryKnown = true;
    }

    // The outer star entry keeps native labels, markers, occlusion and interaction. Share its
    // geometry preparation instead of computing it a second time in the child object callback.
    internal static bool UpdateObject(UIStarmapStarObject obj)
    {
        var entry = activeEntry;
        if (!StellarPresentation.Active || frame == null || entry == null || !ReferenceEquals(entry.Owner, obj) ||
            UIStarmap.isChangingToMilkyWay) return true;
        PrepareGeometry(obj, entry);
        ApplyGeometry(obj, entry);
        VisualWrites.Queue(obj.spotMat, obj.starmap.viewStar == obj.star || obj.starmap.viewStarSystem == obj.star ? 2981 : 2979);
        if (!entry.SpotKnown || entry.LastSpot != entry.SpotMultiplier)
        { obj.spotMat.SetFloat("_Multiplier", entry.SpotMultiplier); entry.LastSpot = entry.SpotMultiplier; entry.SpotKnown = true; }
        var mass = entry.MassMultiplier > 0f;
        VisualWrites.Active(entry.Mass, mass); VisualWrites.Active(entry.Black, mass);
        if (mass && (!entry.MassKnown || entry.LastMass != entry.MassMultiplier))
        { obj.massMat.SetFloat("_Multiplier", entry.MassMultiplier); entry.LastMass = entry.MassMultiplier; entry.MassKnown = true; }
        return false;
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
            (uint)ui.star.index < (uint)state.Entries.Length && state.Entries[ui.star.index] is { } entry)
        { entry.Culled = false; entry.GeometryKnown = false; entry.GeometryFrame = -1; }
    }
    [HarmonyPostfix, HarmonyPatch(typeof(UIStarmap), "_OnFree")]
    internal static void FreeMap(UIStarmap __instance)
    {
        maps.Remove(__instance);
        if (ReferenceEquals(StarmapDisplayFrame_Patch.Current, __instance)) { StarmapDisplayFrame_Patch.Current = null; frame = null; }
    }
}

[HarmonyPatch(typeof(UIStarmapStarObject), "_OnLateUpdate")]
internal static class StarmapStarAppearance_Patch
{
    [HarmonyPrefix]
    internal static bool Prefix(UIStarmapStarObject __instance) => StarmapDisplay_Patch.UpdateObject(__instance);
}
