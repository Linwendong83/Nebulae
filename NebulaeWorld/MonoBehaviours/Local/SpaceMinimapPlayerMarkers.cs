#region

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#endregion

namespace NebulaeWorld.MonoBehaviours.Local;

/// <summary>
///     Draws the other players of the current star system on the space minimap in the bottom-left
///     corner of the HUD, together with their names.
///     Each remote player gets a clone of the vanilla player dot in the minimap's own 3D scene, so
///     the marker is scaled by exactly the same game object transform as the local dot. The name is
///     a UI overlay projected through the minimap camera, which keeps it readable when the texture
///     itself is not re-rendered.
/// </summary>
[DefaultExecutionOrder(2000)]
public sealed class SpaceMinimapPlayerMarkers : MonoBehaviour
{
    private const float NameGap = 5f;
    private const int NameFontSize = 12;

    private static readonly Color NameColor = new(1f, 1f, 1f, 0.95f);

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

        var camera = minimap.cam;
        var playerDot = minimap.playerDot;
        if (playerDot == null)
        {
            HideAll();
            return;
        }

        // The overlay is stretched over the same masked node as the minimap texture, so its own
        // rect is the space the markers are placed in; the texture may be laid out separately.
        var overlaySize = overlay.rect;
        if (overlaySize.width <= 0f || overlaySize.height <= 0f)
        {
            HideAll();
            return;
        }

        // The minimap keeps the local player's dot at the scene origin while in space and offsets
        // the star system instead, so remote positions are expressed relative to the player's dot.
        // That also covers interstellar space, where there is no local star (and no system
        // transform) to measure against.
        var dotOrigin = playerDot.transform.position;
        var dotScale = playerDot.transform.localScale;
        var playerUniversal = GameMain.mainPlayer.uPosition;
        visiblePlayers.Clear();

        using (world.GetRemotePlayersModels(out var remotePlayers))
        {
            foreach (var pair in remotePlayers)
            {
                var movement = pair.Value?.Movement;
                if (movement == null)
                {
                    continue;
                }

                var universal = movement.absolutePosition;
                if (universal.sqrMagnitude < 1e-8)
                {
                    continue;
                }

                var scenePoint = dotOrigin + SpaceMinimapProjection.SceneOffset(universal, playerUniversal);
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

                // The clone sits in the same parent as the local dot and inherits the scale the game
                // writes for it, so both dots shrink and grow together with the minimap zoom.
                marker.Dot.position = scenePoint;
                marker.Dot.localScale = dotScale;
                if (!marker.Dot.gameObject.activeSelf)
                {
                    marker.Dot.gameObject.SetActive(true);
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

        // The vanilla minimap only re-renders its texture when the camera moves or once a second.
        // Remote dots live in that texture, so keep it fresh while any of them is shown.
        if (visiblePlayers.Count > 0 && !camera.enabled)
        {
            camera.enabled = true;
        }
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
        // system view shown while flying in space. Deep space has no local star, but it still has
        // the space minimap, so the star is deliberately not part of this test.
        if (GameMain.localPlanet != null)
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
        if (overlay == null || font == null || minimap.playerDot == null)
        {
            return null;
        }

        var root = new GameObject("Nebulae Space Minimap Player", typeof(RectTransform));
        var rootRect = (RectTransform)root.transform;
        rootRect.SetParent(overlay, false);
        rootRect.anchorMin = rootRect.anchorMax = new Vector2(0.5f, 0.5f);
        rootRect.pivot = new Vector2(0.5f, 0.5f);
        rootRect.sizeDelta = Vector2.zero;

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

        // A clone of the vanilla player dot: same mesh, material and parent, so the game's own
        // transform scaling applies to remote players exactly as it does to the local dot.
        var dot = Object.Instantiate(minimap.playerDot.gameObject, minimap.playerDot.transform.parent, false);
        dot.name = "Nebulae Space Minimap Player Dot";
        dot.SetActive(false);

        root.SetActive(false);
        var marker = new Marker { Root = rootRect, Dot = dot.transform, Name = nameText };
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
            if (marker.Dot != null && marker.Dot.gameObject.activeSelf)
            {
                marker.Dot.gameObject.SetActive(false);
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
            if (marker.Dot != null)
            {
                Destroy(marker.Dot.gameObject);
            }
        }
    }

    private void DestroyOverlay()
    {
        foreach (var marker in markers.Values)
        {
            if (marker.Dot != null)
            {
                Destroy(marker.Dot.gameObject);
            }
        }
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

    private sealed class Marker
    {
        public Transform Dot;
        public Text Name;
        public RectTransform Root;
    }
}
