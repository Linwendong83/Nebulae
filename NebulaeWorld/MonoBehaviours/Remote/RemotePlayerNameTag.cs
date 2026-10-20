using NebulaeModel.Logger;
using System.Linq;
using UnityEngine;

namespace NebulaeWorld.MonoBehaviours.Remote;

/// <summary>
/// Keeps an in-world name tag above the animated remote mecha. The execution order
/// places the position update after RemotePlayerAnimation.LateUpdate.
/// </summary>
[DefaultExecutionOrder(1000)]
public sealed class RemotePlayerNameTag : MonoBehaviour
{
    private const string SHADER_ASSET_PATH = "Assets/Resources/ui/shaders/playerNameTag.shader";
    private const float OCCLUDED_ALPHA = 0.5f;

    // Tag placement sits this far above the tallest rendered part of the mecha.
    private const float HEAD_CLEARANCE = 0.15f;
    private const float FALLBACK_MODEL_TOP = 2.5f;

    // Constant world size scaled to the mecha: the text line height is this fraction
    // of the measured mecha height, independent of the viewing distance.
    private const float TAG_HEIGHT_RATIO = 0.2f;
    private const float MIN_CHARACTER_SIZE = 0.15f;
    private const float MAX_CHARACTER_SIZE = 0.5f;

    private static bool shaderChecked;
    private static Shader nameTagShader;
    private static bool fallbackWarningLogged;

    private RemotePlayerModel playerModel;
    private Transform rootTransform;
    private Color baseColor;
    private Font sourceFont;
    private Renderer[] modelRenderers;
    private float smoothedTop = float.NaN;
    // -1 sentinels force the first write through; NaN comparisons would silently skip it.
    private int boundsFrame = -3;
    private int atlasFrame;
    private float lastCharacterSize = -1f;
    private float lastAlpha = -1f;
    private float lastGhostAlpha = -1f;
    private MeshRenderer nameRenderer;
    private MeshRenderer ghostRenderer;
    private TextMesh ghostText;
    private Material nameMaterial;
    private Material ghostMaterial;

    public TextMesh NameText { get; private set; }

    internal bool Initialize(RemotePlayerModel model, Font sourceFont)
    {
        playerModel = model;
        this.sourceFont = sourceFont;
        rootTransform = model.PlayerTransform;

        var sourceMaterial = sourceFont?.material;
        var fontTexture = sourceMaterial?.mainTexture;
        if (sourceFont == null || sourceMaterial == null || fontTexture == null)
        {
            return false;
        }

        // The sail indicator can be faded out while navigation is inactive.
        // Name tags should keep their own opacity instead of inheriting that UI state.
        baseColor = Color.white;
        nameRenderer = gameObject.AddComponent<MeshRenderer>();
        NameText = gameObject.AddComponent<TextMesh>();
        ConfigureText(NameText, sourceFont, model.Username);

        if (!shaderChecked)
        {
            shaderChecked = true;
            nameTagShader = AssetLoader.NameTagAssetBundle?.LoadAsset<Shader>(SHADER_ASSET_PATH);
        }

        if (nameTagShader != null)
        {
            nameMaterial = new Material(nameTagShader) { mainTexture = fontTexture };
            nameMaterial.SetFloat("_OccludedAlpha", OCCLUDED_ALPHA);
        }
        else
        {
            // Older asset bundles can still render the new tag using the game's
            // existing through-wall font material and a depth-tested font pass.
            var depthShader = Shader.Find("Unlit/Transparent");
            if (depthShader != null)
            {
                ghostMaterial = new Material(sourceMaterial) { mainTexture = fontTexture, renderQueue = 3000 };
                var ghostObject = new GameObject("Occluded Name");
                ghostObject.transform.SetParent(transform, false);
                ghostRenderer = ghostObject.AddComponent<MeshRenderer>();
                ghostText = ghostObject.AddComponent<TextMesh>();
                ConfigureText(ghostText, sourceFont, model.Username);
                ghostRenderer.sharedMaterial = ghostMaterial;

                nameMaterial = new Material(depthShader) { mainTexture = fontTexture, renderQueue = 3001 };
            }
            else
            {
                nameMaterial = new Material(sourceMaterial) { mainTexture = fontTexture };
            }

            if (!fallbackWarningLogged)
            {
                fallbackWarningLogged = true;
                Log.Warn("Player name tag shader is missing from nebulaenametag; using fallback materials");
            }
        }

        nameRenderer.sharedMaterial = nameMaterial;
        return true;
    }

    private static void ConfigureText(TextMesh text, Font font, string username)
    {
        text.text = username;
        text.font = font;
        text.fontSize = 36;
        text.anchor = TextAnchor.LowerCenter;
        text.alignment = TextAlignment.Center;
    }

    private void LateUpdate()
    {
        if (playerModel == null || NameText == null || rootTransform == null ||
            !Multiplayer.IsActive || GameCamera.main == null || GameCamera.instance == null)
        {
            SetVisible(false);
            return;
        }

        var localData = Multiplayer.Session?.LocalPlayer?.Data;
        if (localData == null || !IsInSameWorld(localData.LocalPlanetId, localData.LocalStarId))
        {
            SetVisible(false);
            return;
        }

        transform.position = ComputeTagPosition();

        var cameraTransform = GameCamera.main.transform;
        var distance = Vector3.Distance(transform.position, cameraTransform.position);
        var planetMode = GameCamera.instance.planetMode;
        var fadeStart = planetMode ? 160f : 480f;
        var fadeEnd = planetMode ? 240f : 720f;
        // Beyond fadeEnd the visible pass fades out; the shader's occluded pass keeps
        // its fixed x-ray alpha so the tag stays readable through terrain for a while.
        var xrayEnd = fadeEnd * 3f;
        if (distance >= xrayEnd)
        {
            SetVisible(false);
            return;
        }

        SetVisible(true);
        transform.rotation = cameraTransform.rotation;

        // Dynamic font atlases can be swapped when new glyphs are baked, leaving the
        // captured texture reference stale (missing/dark glyphs), so keep it in sync.
        // The swap is rare, so poll it a few times per second instead of every frame.
        if (Time.frameCount - atlasFrame >= 30)
        {
            atlasFrame = Time.frameCount;
            var liveAtlas = sourceFont != null && sourceFont.material != null ? sourceFont.material.mainTexture : null;
            if (liveAtlas != null && nameRenderer.sharedMaterial.mainTexture != liveAtlas)
            {
                nameRenderer.sharedMaterial.mainTexture = liveAtlas;
                if (ghostMaterial != null) ghostMaterial.mainTexture = liveAtlas;
            }
        }

        // TextMesh line height is fontSize * 0.1 * characterSize meters.
        var characterSize = Mathf.Clamp(smoothedTop * TAG_HEIGHT_RATIO / (NameText.fontSize * 0.1f),
            MIN_CHARACTER_SIZE, MAX_CHARACTER_SIZE);
        var distanceAlpha = Mathf.Clamp01((fadeEnd - distance) / (fadeEnd - fadeStart));

        if (NameText.text != playerModel.Username)
        {
            NameText.text = playerModel.Username;
            if (ghostText != null) ghostText.text = playerModel.Username;
        }

        // Every write to a legacy TextMesh dirties and rebuilds its mesh, so skip the
        // writes whose value has not visibly changed — the size settles as soon as the
        // viewing distance stops moving, and the alpha only changes while fading.
        if (Mathf.Abs(characterSize - lastCharacterSize) > 1e-4f)
        {
            lastCharacterSize = characterSize;
            NameText.characterSize = characterSize;
            if (ghostText != null) ghostText.characterSize = characterSize;
        }
        var alpha = baseColor.a * distanceAlpha;
        if (Mathf.Abs(alpha - lastAlpha) > 1e-4f)
        {
            lastAlpha = alpha;
            NameText.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
        }
        if (ghostText != null)
        {
            var ghostAlpha = alpha * OCCLUDED_ALPHA;
            if (Mathf.Abs(ghostAlpha - lastGhostAlpha) > 1e-4f)
            {
                lastGhostAlpha = ghostAlpha;
                ghostText.color = new Color(baseColor.r, baseColor.g, baseColor.b, ghostAlpha);
            }
        }
    }

    private Vector3 ComputeTagPosition()
    {
        var topLocal = MeasureModelTop();
        return rootTransform.TransformPoint(0f, topLocal + HEAD_CLEARANCE, 0f);
    }

    private float MeasureModelTop()
    {
        if (modelRenderers == null && playerModel?.PlayerModelTransform != null)
        {
            modelRenderers = playerModel.PlayerModelTransform.GetComponentsInChildren<Renderer>()
                .Where(r => r is MeshRenderer || r is SkinnedMeshRenderer)
                .ToArray();
        }

        // Reading renderer.bounds is a native call per renderer and the result only
        // feeds the smoothed tag height, so refresh it a few times per second.
        if (modelRenderers != null && Time.frameCount - boundsFrame < 3)
        {
            return smoothedTop;
        }
        boundsFrame = Time.frameCount;

        var top = float.MinValue;
        if (modelRenderers != null)
        {
            foreach (var renderer in modelRenderers)
            {
                if (renderer == null)
                {
                    continue;
                }
                var bounds = renderer.bounds;
                if (bounds.extents.sqrMagnitude < 1e-6f)
                {
                    continue;
                }
                var localTop = rootTransform.InverseTransformPoint(bounds.center + Vector3.up * bounds.extents.y).y;
                if (localTop > top)
                {
                    top = localTop;
                }
            }
        }

        if (top == float.MinValue)
        {
            top = FALLBACK_MODEL_TOP;
        }

        if (float.IsNaN(smoothedTop))
        {
            smoothedTop = top;
        }
        smoothedTop = Mathf.Lerp(smoothedTop, top, 0.2f);
        return smoothedTop;
    }

    private bool IsInSameWorld(int localPlanetId, int localStarId)
    {
        var remotePlanetId = playerModel.Movement.localPlanetId;
        if (localPlanetId > 0) return remotePlanetId == localPlanetId;
        return remotePlanetId <= 0 && localStarId > 0 && playerModel.Movement.LocalStarId == localStarId;
    }

    private void SetVisible(bool visible)
    {
        // Renderer.enabled is a native call; most frames re-apply the same state.
        if (nameRenderer != null && nameRenderer.enabled != visible) nameRenderer.enabled = visible;
        if (ghostRenderer != null && ghostRenderer.enabled != visible) ghostRenderer.enabled = visible;
    }

    private void OnDestroy()
    {
        if (nameMaterial != null) Destroy(nameMaterial);
        if (ghostMaterial != null) Destroy(ghostMaterial);
    }
}
