#region

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#endregion

namespace NebulaeWorld.MonoBehaviours.Local;

/// <summary>
///     Draws the other players of the current star system on the space minimap in the bottom-left
///     corner of the HUD, together with their names.
///     The vanilla minimap is a camera rendered 3D scene, so a marker is placed by projecting the
///     same universal position through that camera. Drawing the marker as a UI overlay on top of the
///     minimap texture keeps it in sync even on frames where the texture itself is not re-rendered.
/// </summary>
[DefaultExecutionOrder(2000)]
public sealed class SpaceMinimapPlayerMarkers : MonoBehaviour
{
    // UIMinimap3DControl.scaleRatio: minimap scene units per universal unit.
    private const double SceneScale = 2.5e-6;

    private const float DotSize = 7f;
    private const float NameGap = 5f;
    private const int NameFontSize = 12;

    private static readonly Color DotColor = new(1f, 1f, 1f, 0.95f);
    private static readonly Color NameColor = new(1f, 1f, 1f, 0.95f);
    private static Sprite dotSprite;

    private readonly Dictionary<ushort, Marker> markers = new();
    private readonly HashSet<ushort> visiblePlayers = new();
    private readonly List<ushort> removedPlayers = new();

    private UIPlanetGlobe globe;
    private UIMinimap3DControl minimap;
    private RawImage minimapImage;
    private RectTransform overlay;
    private Font font;

    private void LateUpdate()
    {
        if (!TryResolveView(out var world))
        {
            HideAll();
            return;
        }

        var star = GameMain.localStar;
        var camera = minimap.cam;
        var system = minimap.planetarySystemTrans;
        // The overlay is stretched over the same masked node as the minimap texture, so its own
        // rect is the space the markers are placed in; the texture may be laid out separately.
        var overlaySize = overlay.rect;
        visiblePlayers.Clear();

        using (world.GetRemotePlayersModels(out var remotePlayers))
        {
            foreach (var pair in remotePlayers)
            {
                var movement = pair.Value?.Movement;
                // Players in another star system have no body in the local frame; the starmap
                // already shows them, and this minimap only covers the current system.
                if (movement == null || movement.LocalStarId != star.id)
                {
                    continue;
                }

                var universal = movement.absolutePosition;
                if (universal.sqrMagnitude < 1e-8)
                {
                    continue;
                }

                // Same mapping the minimap uses for the planets: the system transform is placed
                // relative to the local player and children are laid out at 2.5e-6 units per metre.
                var scenePoint = system.TransformPoint((Vector3)((universal - star.uPosition) * SceneScale));
                var viewport = camera.WorldToViewportPoint(scenePoint);
                if (viewport.z <= 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
                {
                    continue;
                }

                var marker = GetMarker(pair.Key);
                if (marker == null)
                {
                    continue;
                }

                marker.Root.localPosition = new Vector3(
                    (viewport.x - 0.5f) * overlaySize.width,
                    (viewport.y - 0.5f) * overlaySize.height, 0f);
                var username = pair.Value.Username ?? string.Empty;
                if (marker.Name.text != username)
                {
                    marker.Name.text = username;
                }
                if (!marker.Root.gameObject.activeSelf)
                {
                    marker.Root.gameObject.SetActive(true);
                }
                visiblePlayers.Add(pair.Key);
            }
        }

        RemoveHiddenMarkers();
    }

    private void OnDestroy()
    {
        DestroyOverlay();
    }

    private bool TryResolveView(out SimulatedWorld world)
    {
        world = null;
        if (!Multiplayer.IsActive || !GameMain.isRunning || GameMain.mainPlayer == null)
        {
            return false;
        }

        var session = Multiplayer.Session;
        if (session?.World == null)
        {
            return false;
        }

        // The planet minimap keeps its own remote player dots. This overlay is only for the star
        // system view shown while flying in space.
        if (GameMain.localPlanet != null || GameMain.localStar == null)
        {
            return false;
        }

        var uiGame = UIRoot.instance?.uiGame;
        var planetGlobe = uiGame?.planetGlobe;
        if (planetGlobe == null)
        {
            return false;
        }

        var image = planetGlobe.spaceMinimap;
        var control = planetGlobe.minimapControl;
        if (image == null || !image.enabled || !image.gameObject.activeInHierarchy)
        {
            return false;
        }
        if (control == null || control.cam == null || control.planetarySystemTrans == null ||
            control.spaceGlobeObject == null || !control.spaceGlobeObject.activeInHierarchy)
        {
            return false;
        }

        if (overlay == null || font == null || globe != planetGlobe || minimap != control ||
            minimapImage != image)
        {
            CreateOverlay(planetGlobe, control, image);
        }
        if (overlay == null)
        {
            return false;
        }

        world = session.World;
        return true;
    }

    private void CreateOverlay(UIPlanetGlobe planetGlobe, UIMinimap3DControl control, RawImage image)
    {
        DestroyOverlay();
        font = ResolveFont();
        if (font == null)
        {
            return;
        }

        globe = planetGlobe;
        minimap = control;
        minimapImage = image;

        // The minimap texture lives inside a masked node, so the markers are parented there to
        // inherit the circular crop instead of spilling over the frame of the panel.
        var parent = image.transform.parent;
        if (parent == null)
        {
            globe = null;
            minimap = null;
            minimapImage = null;
            return;
        }

        var root = new GameObject("Nebulae Space Minimap Markers", typeof(RectTransform));
        overlay = (RectTransform)root.transform;
        overlay.SetParent(parent, false);
        overlay.anchorMin = Vector2.zero;
        overlay.anchorMax = Vector2.one;
        overlay.offsetMin = Vector2.zero;
        overlay.offsetMax = Vector2.zero;
        overlay.pivot = new Vector2(0.5f, 0.5f);
        overlay.localScale = Vector3.one;
        overlay.SetAsLastSibling();
    }

    private static Font ResolveFont()
    {
        var uiGame = UIRoot.instance?.uiGame;
        var targetText = uiGame?.sailIndicator?.targetText;
        if (targetText != null && targetText.font != null)
        {
            return targetText.font;
        }
        var nameInput = uiGame?.planetGlobe?.nameInput;
        return nameInput != null && nameInput.textComponent != null ? nameInput.textComponent.font : null;
    }

    private Marker GetMarker(ushort playerId)
    {
        if (markers.TryGetValue(playerId, out var existing))
        {
            return existing;
        }
        if (overlay == null || font == null)
        {
            return null;
        }

        var root = new GameObject("Nebulae Space Minimap Player", typeof(RectTransform));
        var rootRect = (RectTransform)root.transform;
        rootRect.SetParent(overlay, false);
        rootRect.anchorMin = rootRect.anchorMax = new Vector2(0.5f, 0.5f);
        rootRect.pivot = new Vector2(0.5f, 0.5f);
        rootRect.sizeDelta = Vector2.zero;

        var dot = new GameObject("Dot", typeof(RectTransform), typeof(Image));
        var dotRect = (RectTransform)dot.transform;
        dotRect.SetParent(rootRect, false);
        dotRect.anchorMin = dotRect.anchorMax = new Vector2(0.5f, 0.5f);
        dotRect.sizeDelta = new Vector2(DotSize, DotSize);
        var dotImage = dot.GetComponent<Image>();
        dotImage.sprite = DotSprite;
        dotImage.color = DotColor;
        dotImage.raycastTarget = false;

        var name = new GameObject("Name", typeof(RectTransform), typeof(Text));
        var nameRect = (RectTransform)name.transform;
        nameRect.SetParent(rootRect, false);
        nameRect.anchorMin = nameRect.anchorMax = new Vector2(0.5f, 0f);
        nameRect.pivot = new Vector2(0.5f, 0f);
        nameRect.sizeDelta = new Vector2(200f, NameFontSize + 4f);
        nameRect.anchoredPosition = new Vector2(0f, NameGap);
        var nameText = name.GetComponent<Text>();
        nameText.font = font;
        nameText.fontSize = NameFontSize;
        // User names are displayed verbatim: rich text tags in them must not restyle the label.
        nameText.supportRichText = false;
        nameText.alignment = TextAnchor.MiddleCenter;
        nameText.horizontalOverflow = HorizontalWrapMode.Overflow;
        nameText.verticalOverflow = VerticalWrapMode.Overflow;
        nameText.raycastTarget = false;
        nameText.color = NameColor;
        var outline = name.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        outline.effectDistance = new Vector2(1f, -1f);

        root.SetActive(false);
        var marker = new Marker { Root = rootRect, Name = nameText };
        markers.Add(playerId, marker);
        return marker;
    }

    private void HideAll()
    {
        foreach (var marker in markers.Values)
        {
            if (marker.Root != null && marker.Root.gameObject.activeSelf)
            {
                marker.Root.gameObject.SetActive(false);
            }
        }
    }

    private void RemoveHiddenMarkers()
    {
        removedPlayers.Clear();
        foreach (var pair in markers)
        {
            if (!visiblePlayers.Contains(pair.Key))
            {
                removedPlayers.Add(pair.Key);
            }
        }
        foreach (var playerId in removedPlayers)
        {
            var marker = markers[playerId];
            markers.Remove(playerId);
            if (marker.Root != null)
            {
                Destroy(marker.Root.gameObject);
            }
        }
    }

    private void DestroyOverlay()
    {
        markers.Clear();
        visiblePlayers.Clear();
        removedPlayers.Clear();
        if (overlay != null)
        {
            Destroy(overlay.gameObject);
        }
        overlay = null;
        globe = null;
        minimap = null;
        minimapImage = null;
        font = null;
    }

    private static Sprite DotSprite
    {
        get
        {
            if (dotSprite != null)
            {
                return dotSprite;
            }

            const int size = 32;
            const float radius = size * 0.5f;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Nebulae Space Minimap Dot",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x + 0.5f - radius;
                    var dy = y + 0.5f - radius;
                    var alpha = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            dotSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
            dotSprite.name = "Nebulae Space Minimap Dot";
            return dotSprite;
        }
    }

    private sealed class Marker
    {
        public Text Name;
        public RectTransform Root;
    }
}
