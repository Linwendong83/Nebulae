using System;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel;
using NebulaeModel.Networking;
using NebulaeNetwork;
using NebulaeNetwork.ServerList;
using NebulaeWorld;
using NebulaeWorld.MonoBehaviours.Local;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NebulaePatcher.Patches.Dynamic;

/// <summary>Custom multiplayer server page rebuilt from the game's own window widgets.</summary>
internal static class MultiplayerPage
{
    private const float PanelWidth = 980f;
    private const float PanelHeight = 720f;
    private const float InnerPad = 34f;
    private const float ButtonHeight = 44f;
    private const float AutoRefreshInterval = 15f;

    private static readonly Color TitleColor = new(0.94f, 0.97f, 1f, 1f);
    private static readonly Color DividerColor = new(0.47f, 0.77f, 1f, 0.22f);
    private static readonly Color ListBackgroundColor = new(0.02f, 0.05f, 0.08f, 0.72f);
    private static readonly Color RowNormalColor = new(0.07f, 0.15f, 0.21f, 0.98f);
    private static readonly Color RowHoverColor = new(0.10f, 0.20f, 0.28f, 0.98f);
    private static readonly Color RowSelectedColor = new(0.12f, 0.31f, 0.41f, 1f);
    private static readonly Color SelectionAccentColor = new(0.58f, 0.84f, 1f, 0.95f);
    private static readonly Color NameColor = new(0.92f, 0.94f, 0.97f, 1f);
    private static readonly Color DescriptionColor = new(0.60f, 0.66f, 0.72f, 1f);
    private static readonly Color ValueColor = new(0.82f, 0.86f, 0.90f, 1f);
    private static readonly Color DimColor = new(0.62f, 0.67f, 0.72f, 1f);
    private static readonly Color FastPingColor = new(0.45f, 0.82f, 0.55f, 1f);
    private static readonly Color MediumPingColor = new(0.93f, 0.79f, 0.38f, 1f);
    private static readonly Color SlowPingColor = new(0.91f, 0.45f, 0.38f, 1f);
    private static readonly Color WarningColor = new(0.93f, 0.79f, 0.38f, 1f);
    private static readonly Color ErrorColor = new(0.88f, 0.48f, 0.42f, 1f);

    private static RectTransform root;
    private static RectTransform panel;
    private static RectTransform buttonTemplate;
    private static RectTransform inputTemplate;
    private static Text textTemplate;
    private static Font uiFont;
    private static GameObject windowButtonSource;
    private static RectTransform scrollbarSource;
    private static Sprite windowFrameSprite;
    private static Color windowFrameColor = Color.white;
    private static RectTransform listContent;
    private static RectTransform editor;
    private static RectTransform goalRow;
    private static UIComboBox goalCombo;
    private static InputField nameInput;
    private static InputField addressInput;
    private static InputField passwordInput;
    private static Button joinButton;
    private static Button editButton;
    private static Button deleteButton;
    private static Button commitButton;
    private static bool directConnect;
    private static string selectedId;
    private static string hoveredId;
    private static bool loadWindowPending;
    private static float autoRefreshTimer;
    private static readonly List<GameObject> rows = new();
    private static readonly Dictionary<string, RowWidgets> rowByServer = new();
    private static readonly Dictionary<string, ServerProbeResult> probeResults = new();
    private static readonly Dictionary<string, float> lastClickTime = new();

    private class RowWidgets
    {
        public Text Name;
        public Text Description;
        public Text Players;
        public Text Ping;
        public Image Background;
        public Image SelectionAccent;
        public Image Badge;
    }

    public static bool IsOpen => root != null && root.gameObject.activeInHierarchy;
    public static bool IsEditing => editor != null && editor.gameObject.activeSelf;

    public static void Create()
    {
        if (root != null) return;

        var overlay = GameObject.Find("Overlay Canvas")?.GetComponent<RectTransform>();
        var original = overlay?.Find("Galaxy Select") as RectTransform;
        if (overlay == null || original == null) return;

        buttonTemplate = original.Find("start-button") as RectTransform;
        var setting = original.Find("setting-group");
        inputTemplate = setting?.Find("stretch-transform/galaxy-seed") as RectTransform;
        textTemplate = buttonTemplate?.GetComponentInChildren<Text>(true);
        if (buttonTemplate == null || inputTemplate == null || textTemplate == null) return;

        root = NewRect("Nebulae - Multiplayer Menu", overlay, -1);
        Stretch(root);
        var backdrop = root.gameObject.AddComponent<Image>();
        backdrop.color = new Color(0f, 0.015f, 0.03f, 0.6f);
        backdrop.raycastTarget = true;
        var nativeBackdrop = UIRoot.instance != null && UIRoot.instance.galaxySelect != null
            ? UIRoot.instance.galaxySelect.darkBackground
            : null;
        if (nativeBackdrop != null && nativeBackdrop.sprite != null)
        {
            backdrop.sprite = nativeBackdrop.sprite;
            backdrop.type = nativeBackdrop.type;
            backdrop.fillCenter = true;
        }

        HarvestNativeWidgets(root.rect.size);

        // Texts are built from scratch on the game's own font: cloned menu/button text
        // carries stacked glow, outline and faux-bold effects that smear white labels.
        var windowText = windowButtonSource != null ? windowButtonSource.GetComponentInChildren<Text>(true) : null;
        uiFont = windowText != null ? windowText.font : textTemplate.font;

        panel = NewRect("Multiplayer panel", root, 0);
        Center(panel, PanelWidth, PanelHeight, new Vector2(0, 4));
        var panelImage = panel.gameObject.AddComponent<Image>();
        if (windowFrameSprite != null)
        {
            panelImage.sprite = windowFrameSprite;
            panelImage.type = Image.Type.Sliced;
            panelImage.color = windowFrameColor;
        }
        else
        {
            panelImage.color = new Color(0.035f, 0.09f, 0.14f, 0.97f);
            var sourceImage = original.GetComponent<Image>() ?? inputTemplate.GetComponentInChildren<Image>(true);
            if (sourceImage != null)
            {
                panelImage.sprite = sourceImage.sprite;
                panelImage.type = sourceImage.type;
            }
        }

        BuildHeader();
        BuildServerList();
        BuildActions();
        BuildEditor();
        root.gameObject.AddComponent<MultiplayerPageTicker>();
        root.gameObject.SetActive(false);
    }

    /// <summary>Picks the game's own window chrome so the page blends in with native dialogs.</summary>
    private static void HarvestNativeWidgets(Vector2 fullCanvasSize)
    {
        try
        {
            var loadWindow = UIRoot.instance != null ? UIRoot.instance.loadGameWindow : null;
            if (loadWindow == null) return;
            if (loadWindow.loadButton != null) windowButtonSource = loadWindow.loadButton.gameObject;
            scrollbarSource = loadWindow.scrollVbarRect;
            TryFindFrameImage(loadWindow.transform as RectTransform, fullCanvasSize,
                out windowFrameSprite, out windowFrameColor);
        }
        catch
        {
            // Cosmetic harvesting only; every consumer falls back to generic styling.
        }
    }

    private static bool TryFindFrameImage(RectTransform windowRoot, Vector2 fullCanvasSize, out Sprite sprite, out Color color)
    {
        sprite = null;
        color = Color.white;
        if (windowRoot == null) return false;

        var windowArea = windowRoot.rect.width * windowRoot.rect.height;
        var canvasArea = fullCanvasSize.x * fullCanvasSize.y;
        Sprite best = null;
        var bestColor = Color.white;
        var bestArea = 0f;
        foreach (var image in windowRoot.GetComponentsInChildren<Image>(true))
        {
            var candidate = image.sprite;
            if (candidate == null || (candidate.border.x <= 0f && candidate.border.y <= 0f)) continue;
            var rect = image.rectTransform.rect;
            if (rect.width < 480f || rect.height < 320f) continue;
            var area = rect.width * rect.height;
            // The dialog background is window-sized: full-screen dim overlays and small
            // inner cards must not win.
            if (windowArea > 0f && area < windowArea * 0.35f) continue;
            if (canvasArea > 0f && area > canvasArea * 0.8f) continue;
            if (area <= bestArea) continue;
            bestArea = area;
            best = candidate;
            bestColor = image.color;
        }

        if (best == null) return false;
        // The frame sprite is drawn to be tinted: rendered color = sprite x tint, so a
        // near-white harvested tint paints the whole page like paper. Whatever the game
        // state did to the source image, keep the shape and force the dark theme back.
        var luminance = bestColor.r * 0.299f + bestColor.g * 0.587f + bestColor.b * 0.114f;
        if (luminance > 0.6f) bestColor = new Color(0.13f, 0.19f, 0.26f, bestColor.a);
        sprite = best;
        color = bestColor;
        return true;
    }

    public static void Open()
    {
        Create();
        if (root == null) return;
        Multiplayer.IsInMultiplayerMenu = true;
        UIRoot.instance.CloseMainMenuUI();
        FitPanel();
        RefreshList();
        ProbeAll();
        root.SetAsLastSibling();
        root.gameObject.SetActive(true);
    }

    public static void ShowAfterDisconnect()
    {
        Create();
        if (root == null) return;
        UIRoot.instance.CloseMainMenuUI();
        FitPanel();
        RefreshList();
        ProbeAll();
        root.SetAsLastSibling();
        root.gameObject.SetActive(true);
    }

    public static void HideForConnection()
    {
        if (root != null) root.gameObject.SetActive(false);
    }

    public static void Close()
    {
        if (IsEditing)
        {
            editor.gameObject.SetActive(false);
            return;
        }

        if (root == null) return;
        root.gameObject.SetActive(false);
        Multiplayer.IsInMultiplayerMenu = false;
        UIRoot.instance.OpenMainMenuUI();
    }

    /// <summary>Called every frame by the ticker on the page root while the page is active.</summary>
    internal static void Tick(float deltaTime)
    {
        if (root == null) return;

        var generation = ServerStatusProbe.CurrentGeneration;
        while (ServerStatusProbe.TryDequeueResult(out var result))
        {
            if (result.Generation != generation) continue;
            probeResults[result.ServerId] = result;
            if (rowByServer.TryGetValue(result.ServerId, out var widgets))
            {
                ApplyRowStatus(widgets, result);
            }
        }

        if (IsOpen && !IsEditing)
        {
            autoRefreshTimer += deltaTime;
            if (autoRefreshTimer >= AutoRefreshInterval)
            {
                autoRefreshTimer = 0f;
                ProbeAll();
            }
        }
        else
        {
            autoRefreshTimer = 0f;
        }

        // The cloned combo's Localizer re-lays it out on every activation; hold it
        // left-aligned with the input column for as long as the editor shows it.
        if (PickingGoalLevel) AlignComboLeft((RectTransform)goalCombo.transform);

        // The goal picker lives under Top Windows, and the password prompt is a dialog
        // that pins itself last every frame. Both must stay above this page.
        KeepPageUnderOverlays();
    }

    private static void BuildHeader()
    {
        var header = NewRect("Header", panel, 0);
        Place(header, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0, -12), new Vector2(PanelWidth - InnerPad * 2f, 84f));

        var title = AddLocalizedText(header, "Multiplayer", 26, TextAnchor.MiddleLeft);
        title.color = TitleColor;
        Place(title.rectTransform, new Vector2(0, 1f), new Vector2(0, 1f), new Vector2(0, 1f),
            new Vector2(0, -10), new Vector2(430, 44));

        var create = AddButton("Create Game", header, 150f, StartNewGame);
        Place(create.transform as RectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
            new Vector2(-162, -10), new Vector2(150, ButtonHeight));

        var load = AddButton("Load Game", header, 150f, LoadGame);
        Place(load.transform as RectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
            new Vector2(-2, -10), new Vector2(150, ButtonHeight));

        AddDivider(header, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0, 2), 2f);
    }

    private static void BuildServerList()
    {
        var listPanel = NewRect("Saved servers", panel, 1);
        Place(listPanel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0, -108), new Vector2(PanelWidth - InnerPad * 2f, 520f));
        var background = listPanel.gameObject.AddComponent<Image>();
        background.color = ListBackgroundColor;

        var scrollbarWidth = scrollbarSource != null && scrollbarSource.rect.width > 4f
            ? scrollbarSource.rect.width
            : 14f;

        var viewport = NewRect("Server viewport", listPanel, 1);
        Stretch(viewport);
        viewport.offsetMin = new Vector2(18f, 12f);
        viewport.offsetMax = new Vector2(-(scrollbarWidth + 12f), -12f);
        var viewportBlocker = viewport.gameObject.AddComponent<Image>();
        viewportBlocker.color = Color.clear;
        viewportBlocker.raycastTarget = true;
        var deselect = viewport.gameObject.AddComponent<Button>();
        deselect.transition = Selectable.Transition.None;
        deselect.onClick = new Button.ButtonClickedEvent();
        deselect.onClick.AddListener(new UnityAction(DeselectAll));
        viewport.gameObject.AddComponent<RectMask2D>();
        var scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.scrollSensitivity = 28;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.viewport = viewport;

        if (scrollbarSource != null)
        {
            var vbar = Object.Instantiate(scrollbarSource.gameObject, listPanel, false);
            vbar.name = "Server scrollbar";
            RemoveSceneTriggers(vbar);
            vbar.SetActive(true);
            var vbarRect = vbar.GetComponent<RectTransform>();
            Place(vbarRect, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f),
                new Vector2(-4f, 0), new Vector2(scrollbarWidth, -24f));
            var vbarScrollbar = vbar.GetComponent<Scrollbar>();
            if (vbarScrollbar != null)
            {
                scroll.verticalScrollbar = vbarScrollbar;
                scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            }
        }

        listContent = NewRect("Server entries", viewport, 0);
        listContent.anchorMin = new Vector2(0, 1);
        listContent.anchorMax = new Vector2(1, 1);
        listContent.pivot = new Vector2(0.5f, 1);
        listContent.offsetMin = Vector2.zero;
        listContent.offsetMax = Vector2.zero;
        var layout = listContent.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(8, 8, 8, 8);
        layout.spacing = 6;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        listContent.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.content = listContent;
    }

    private static void BuildActions()
    {
        var actions = NewRect("Actions", panel, 2);
        Place(actions, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
            new Vector2(0, 26), new Vector2(PanelWidth - InnerPad * 2f, ButtonHeight));

        joinButton = AddButton("Join", actions, 112f, JoinSelected);
        editButton = AddButton("Edit", actions, 96f, () => OpenEditor(Selected(), false));
        deleteButton = AddButton("Delete", actions, 96f, DeleteSelected);
        var addButton = AddButton("Add Server", actions, 132f, () => OpenEditor(null, false));
        var directButton = AddButton("Direct Connect", actions, 132f, () => OpenEditor(null, true));
        var refreshButton = AddButton("Refresh", actions, 96f, ProbeAll);
        var backButton = AddButton("Back", actions, 96f, Close);

        // Server operations sit in the bottom-left corner, page commands in the bottom-right,
        // matching how the game groups primary and secondary dialog actions.
        var half = actions.rect.width / 2f;
        var gap = 10f;
        var left = -half;
        foreach (var button in new[] { joinButton, editButton, deleteButton })
        {
            var width = ((RectTransform)button.transform).sizeDelta.x;
            Center(button.transform as RectTransform, width, ButtonHeight, new Vector2(left + width / 2f, 0));
            left += width + gap;
        }

        var right = half;
        foreach (var button in new[] { backButton, refreshButton, directButton, addButton })
        {
            var width = ((RectTransform)button.transform).sizeDelta.x;
            Center(button.transform as RectTransform, width, ButtonHeight, new Vector2(right - width / 2f, 0));
            right -= width + gap;
        }
    }

    private static void BuildEditor()
    {
        editor = NewRect("Server editor", root, 20);
        Stretch(editor);
        var overlay = editor.gameObject.AddComponent<Image>();
        overlay.color = new Color(0, 0, 0, 0.6f);
        overlay.raycastTarget = true;

        var dialog = NewRect("Editor dialog", editor, 0);
        Center(dialog, 640, 480, Vector2.zero);
        var dialogImage = dialog.gameObject.AddComponent<Image>();
        if (windowFrameSprite != null)
        {
            dialogImage.sprite = windowFrameSprite;
            dialogImage.type = Image.Type.Sliced;
            dialogImage.color = windowFrameColor;
        }
        else
        {
            dialogImage.color = new Color(0.035f, 0.09f, 0.14f, 1f);
        }

        var title = AddLocalizedText(dialog, "Add Server", 22, TextAnchor.MiddleLeft);
        title.color = TitleColor;
        Place(title.rectTransform, new Vector2(0, 1f), new Vector2(0, 1f), new Vector2(0, 1f),
            new Vector2(30, -20), new Vector2(520, 40));

        AddDivider(dialog, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0, -72), 2f);

        nameInput = AddInput(dialog, "Name", 104, true);
        addressInput = AddInput(dialog, "Address", 32, true);
        passwordInput = AddInput(dialog, "Password (optional)", -40, true);
        passwordInput.contentType = InputField.ContentType.Password;
        passwordInput.UpdateLabel();
        BuildGoalRow(dialog);

        commitButton = AddButton("Save", dialog, 140f, CommitEditor);
        Place(commitButton.transform as RectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0.5f),
            new Vector2(-180, 40), new Vector2(140, ButtonHeight));
        var cancel = AddButton("Cancel", dialog, 140f, () => editor.gameObject.SetActive(false));
        Place(cancel.transform as RectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0.5f),
            new Vector2(-30, 40), new Vector2(140, ButtonHeight));
        editor.gameObject.SetActive(false);
    }

    /// <summary>The editor dialog's goal level row: the game's own combo from the load window,
    /// so a server carries the same level choice a save would. Stock items are Off/Key/Full;
    /// itemIndex + 1 is the EGoalLevel, and an empty selection means "ask on first join".
    /// Clicking it opens the game's full-screen goal picker, exactly like the load window.</summary>
    private static void BuildGoalRow(RectTransform dialog)
    {
        // Sibling order is render order in uGUI: the row must come last, or the combo's
        // dropdown opens underneath the dialog's other rows and the save/cancel buttons.
        goalRow = NewRect("Goal row", dialog, dialog.childCount);
        Center(goalRow, 580, 58, new Vector2(0, -112));
        var labelText = AddLocalizedText(goalRow, "Goal Level", 18, TextAnchor.MiddleLeft);
        Place(labelText.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
            new Vector2(8, 0), new Vector2(175, 48));

        var source = UIRoot.instance != null ? UIRoot.instance.loadGameWindow?.goalLevelComboBox : null;
        if (source == null) return;
        var comboObject = Object.Instantiate(source.gameObject, goalRow, false);
        comboObject.name = "GoalCombo";
        RemoveSceneTriggers(comboObject);
        comboObject.SetActive(true);
        goalCombo = comboObject.GetComponent<UIComboBox>();
        if (goalCombo == null) { goalCombo = null; return; }
        // The stock combo carries the load window's serialized listeners; the click still
        // reaches the load window's OnComboBoxClick, which opens the full-screen picker.
        goalCombo.onItemIndexChange = new UIComboBox.ChangeEvent();
        goalCombo.CanInput = false;
        goalCombo.InitItemIndexSelf = false;
        // The stock combo resizes itself to its text; an empty selection would collapse
        // it to a stub.
        goalCombo.autoWidth = false;
        goalCombo.UpdateItems();
        AlignComboTexts(comboObject.transform);
        AlignComboLeft((RectTransform)comboObject.transform);
    }

    /// <summary>Left-align every text of the clone to match the input fields above.</summary>
    private static void AlignComboTexts(Transform combo)
    {
        foreach (var text in combo.GetComponentsInChildren<Text>(true))
        {
            text.alignment = TextAnchor.MiddleLeft;
        }
    }

    /// <summary>The combo keeps the stock size the Localizer lays out on activation. Only its
    /// anchor is moved so the box's left edge lines up with the input fields (210px into the
    /// 580-wide row) and its vertical center sits on the row's center. Re-applied per frame
    /// because the Localizer re-fires and the cloned anchor is the box's bottom-left corner,
    /// which otherwise leaves the short box sitting on the row's bottom edge.</summary>
    private static void AlignComboLeft(RectTransform comboRect)
    {
        var row = comboRect.parent as RectTransform;
        if (row == null) return;
        comboRect.anchorMin = comboRect.anchorMax = new Vector2(0f, 0.5f);
        comboRect.pivot = new Vector2(0f, 0.5f);
        comboRect.anchoredPosition = new Vector2(210f, 0f);
    }

    /// <summary>The page is created on the Overlay Canvas and kept last so it covers the main
    /// menu. The goal picker lives inside Top Windows, which is drawn before this page, so
    /// while it is open the page steps back to just before that group. Nested canvases along
    /// the way must not stop the walk: only the ancestor that shares the page's parent counts.
    /// The password prompt's dialog group already re-pins itself last every frame.</summary>
    private static void KeepPageUnderOverlays()
    {
        if (root == null || !root.gameObject.activeInHierarchy) return;
        var parent = root.parent;
        var setting = UIRoot.instance != null ? UIRoot.instance.goalSetting : null;
        if (parent == null || setting == null || !setting.gameObject.activeInHierarchy) return;

        var top = setting.transform;
        while (top.parent != null && top.parent != parent) top = top.parent;
        if (top.parent != parent) return;
        var behind = top.GetSiblingIndex();
        if (root.GetSiblingIndex() > behind) root.SetSiblingIndex(behind);
    }

    /// <summary>True while the server editor is open with the goal row visible, so the
    /// goal picker that the cloned combo opens can be lifted onto our page and its
    /// choice routed back into the editor.</summary>
    internal static bool PickingGoalLevel =>
        IsOpen && IsEditing && goalRow != null && goalRow.gameObject.activeSelf && goalCombo != null;

    /// <summary>Highlight the editor's own level (the stock call highlights the load window's)
    /// and keep the combo's own dropdown list shut, matching the stock single-page behavior.
    /// The picker itself stays under Top Windows; the page yields the last canvas slot to it.</summary>
    internal static void BringGoalSettingToFront()
    {
        var setting = UIRoot.instance != null ? UIRoot.instance.goalSetting : null;
        if (setting == null || goalCombo == null) return;
        setting.SetOpeningGoalLevel((EGoalLevel)Mathf.Clamp(goalCombo.itemIndex + 1, 0, 3));
        goalCombo.isDroppedDown = false;
        KeepPageUnderOverlays();
    }

    /// <summary>The picker's stock handler drops the choice when no game is running, which
    /// is exactly the editor's situation; route it into the editor's combo instead.</summary>
    internal static void SetEditorGoalLevel(int level)
    {
        if (goalCombo == null) return;
        goalCombo.itemIndex = Mathf.Clamp(level - 1, -1, 2);
    }

    private static void RefreshList()
    {
        foreach (var row in rows) Object.Destroy(row);
        rows.Clear();
        rowByServer.Clear();

        var servers = ServerMemoryStore.Instance.Servers;
        if (selectedId != null && ServerMemoryStore.Instance.FindServer(selectedId) == null) selectedId = null;
        hoveredId = null;

        foreach (var server in servers)
        {
            var id = server.Id;
            var row = new GameObject("server-" + id, typeof(RectTransform)).GetComponent<RectTransform>();
            row.SetParent(listContent, false);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 54;

            var widgets = new RowWidgets();
            var background = row.gameObject.AddComponent<Image>();
            background.color = RowNormalColor;
            widgets.Background = background;

            var accent = new GameObject("Selection accent", typeof(RectTransform)).GetComponent<RectTransform>();
            accent.SetParent(row, false);
            accent.anchorMin = new Vector2(0f, 0f);
            accent.anchorMax = new Vector2(0f, 1f);
            accent.pivot = new Vector2(0f, 0.5f);
            accent.anchoredPosition = Vector2.zero;
            accent.sizeDelta = new Vector2(3f, -10f);
            var accentImage = accent.gameObject.AddComponent<Image>();
            accentImage.color = SelectionAccentColor;
            accentImage.raycastTarget = false;
            accent.gameObject.SetActive(false);
            widgets.SelectionAccent = accentImage;

            var rowButton = row.gameObject.AddComponent<Button>();
            rowButton.transition = Selectable.Transition.None;
            rowButton.onClick = new Button.ButtonClickedEvent();
            rowButton.onClick.AddListener(new UnityAction(() => OnRowClicked(id)));
            row.gameObject.AddComponent<RowHoverHandler>().ServerId = id;

            var name = CloneText(row, server.Name, TextAnchor.MiddleLeft, 18);
            name.color = NameColor;
            AnchorColumn(name, new Vector2(0f, 0.45f), new Vector2(0.78f, 1f), new Vector2(20, 0), new Vector2(-8, -5));
            name.raycastTarget = false;
            widgets.Name = name;

            var description = CloneText(row, string.Empty, TextAnchor.MiddleLeft, 14);
            description.color = DescriptionColor;
            AnchorColumn(description, new Vector2(0f, 0f), new Vector2(0.78f, 0.45f), new Vector2(20, 2), new Vector2(-8, 0));
            description.raycastTarget = false;
            widgets.Description = description;

            var players = CloneText(row, string.Empty, TextAnchor.MiddleCenter, 17);
            players.color = ValueColor;
            AnchorColumn(players, new Vector2(0.78f, 0f), new Vector2(0.855f, 1f), new Vector2(4, 0), new Vector2(-4, 0));
            players.raycastTarget = false;
            widgets.Players = players;

            // A small person glyph right after the count marks the column as the player count.
            // No game data asset is reliable in the main menu, so the glyph is drawn procedurally.
            // It tracks the number's right edge in ApplyRowStatus and only shows alongside it.
            var badge = NewRect("player badge", row, 0);
            badge.anchorMin = badge.anchorMax = new Vector2(0.78f, 0.5f);
            badge.pivot = new Vector2(0f, 0.5f);
            badge.anchoredPosition = new Vector2(0f, 0f);
            badge.sizeDelta = new Vector2(17f, 17f);
            var badgeImage = badge.gameObject.AddComponent<Image>();
            badgeImage.sprite = GetPlayerBadgeSprite();
            badgeImage.preserveAspect = true;
            badgeImage.raycastTarget = false;
            badgeImage.color = new Color(ValueColor.r, ValueColor.g, ValueColor.b, 0.9f);
            badge.gameObject.SetActive(false);
            widgets.Badge = badgeImage;

            var ping = CloneText(row, string.Empty, TextAnchor.MiddleCenter, 16);
            ping.color = DimColor;
            AnchorColumn(ping, new Vector2(0.89f, 0f), new Vector2(1f, 1f), new Vector2(4, 0), new Vector2(-14, 0));
            ping.raycastTarget = false;
            widgets.Ping = ping;

            rowByServer[id] = widgets;
            rows.Add(row.gameObject);
        }

        // Redraw from cached probe results so rebuilding the rows (selection, language
        // change, ...) does not lose the player/ping display.
        foreach (var pair in rowByServer)
        {
            UpdateRowVisual(pair.Key);
            if (probeResults.TryGetValue(pair.Key, out var stored)) ApplyRowStatus(pair.Value, stored);
            else ApplyCheckingStatus(pair.Value);
        }

        UpdateSelection();
    }

    private static void ProbeAll()
    {
        ServerStatusProbe.RefreshAll(ServerMemoryStore.Instance.Servers
            .Select(s => (s.Id, s.Address, s.Password)));
    }

    private static void OnRowClicked(string id)
    {
        if (selectedId != id)
        {
            selectedId = id;
            UpdateSelection();
        }

        var now = Time.unscaledTime;
        if (lastClickTime.TryGetValue(id, out var previous) && now - previous < 0.35f)
        {
            lastClickTime[id] = 0f;
            JoinSelected();
            return;
        }
        lastClickTime[id] = now;
    }

    private static void UpdateSelection()
    {
        foreach (var id in rowByServer.Keys) UpdateRowVisual(id);

        // Hidden rather than greyed out: with no selection the row actions have no
        // meaning and would only clutter the action bar.
        var hasSelection = Selected() != null;
        joinButton.gameObject.SetActive(hasSelection);
        editButton.gameObject.SetActive(hasSelection);
        deleteButton.gameObject.SetActive(hasSelection);
    }

    private static void DeselectAll()
    {
        if (selectedId == null) return;
        selectedId = null;
        UpdateSelection();
    }

    internal static void SetHovered(string id, bool hovered)
    {
        if (hovered)
        {
            if (hoveredId == id) return;
            var previous = hoveredId;
            hoveredId = id;
            UpdateRowVisual(id);
            if (previous != null && previous != id) UpdateRowVisual(previous);
        }
        else
        {
            if (hoveredId != id) return;
            hoveredId = null;
            UpdateRowVisual(id);
        }
    }

    private static void UpdateRowVisual(string id)
    {
        if (!rowByServer.TryGetValue(id, out var widgets) || widgets.Background == null) return;
        var selected = id == selectedId;
        widgets.Background.color = selected ? RowSelectedColor
            : id == hoveredId ? RowHoverColor
            : RowNormalColor;
        if (widgets.SelectionAccent != null) widgets.SelectionAccent.gameObject.SetActive(selected);
    }

    private static void ApplyCheckingStatus(RowWidgets widgets)
    {
        widgets.Players.text = "—";
        widgets.Players.color = DimColor;
        widgets.Ping.fontSize = 14;
        widgets.Ping.text = "Checking...".Translate();
        widgets.Ping.color = DimColor;
        widgets.Description.text = string.Empty;
        if (widgets.Badge != null) widgets.Badge.gameObject.SetActive(false);
    }

    private static void ApplyRowStatus(RowWidgets widgets, ServerProbeResult result)
    {
        if (result.State != ServerProbeState.Online)
        {
            widgets.Players.text = "—";
            widgets.Players.color = DimColor;
            widgets.Ping.fontSize = 14;
            widgets.Ping.text = StateText(result.State);
            widgets.Ping.color = StateColor(result.State);
            if (widgets.Badge != null) widgets.Badge.gameObject.SetActive(false);
            return;
        }

        widgets.Players.text = result.NumPlayers.ToString();
        widgets.Players.color = ValueColor;
        widgets.Ping.fontSize = 16;
        widgets.Ping.text = $"{result.PingMs} ms";
        widgets.Ping.color = result.PingMs <= 80 ? FastPingColor
            : result.PingMs <= 160 ? MediumPingColor
            : SlowPingColor;

        if (widgets.Badge != null)
        {
            // Centered text of unknown width: park the badge just past the number's right edge.
            // The text column carries a 4px left inset (AnchorColumn offsetMin.x), so 11 here
            // leaves a real 7px breathing gap between the number and the badge.
            var playersRect = widgets.Players.rectTransform;
            var columnWidth = playersRect.rect.width;
            var textWidth = Mathf.Min(widgets.Players.preferredWidth, columnWidth);
            var badgeRect = (RectTransform)widgets.Badge.transform;
            badgeRect.anchoredPosition = new Vector2(columnWidth / 2f + textWidth / 2f + 11f, 0f);
            widgets.Badge.gameObject.SetActive(true);
        }

        if (!NebulaeVersion.IsKnownStatusVersion(result.NebulaeVersion) ||
                 !NebulaeVersion.IsKnownStatusVersion(Config.ModVersion))
        {
            widgets.Description.text = "Unable to determine mod version".Translate();
            widgets.Description.color = WarningColor;
        }
        else if (!NebulaeVersion.IsCompatible(result.NebulaeVersion, Config.ModVersion))
        {
            widgets.Description.text = string.Format("Mod version mismatch: Server {0}".Translate(),
                NebulaeVersion.Normalize(result.NebulaeVersion));
            widgets.Description.color = ErrorColor;
        }
        else
        {
            widgets.Description.text = string.IsNullOrWhiteSpace(result.Description) ? string.Empty : result.Description;
            widgets.Description.color = DescriptionColor;
        }
    }

    private static string StateText(ServerProbeState state)
    {
        return state switch
        {
            ServerProbeState.Unreachable => "Unreachable".Translate(),
            ServerProbeState.Starting => "Starting...".Translate(),
            ServerProbeState.PasswordRequired => "Password Required".Translate(),
            ServerProbeState.WrongPassword => "Wrong Password".Translate(),
            _ => "Checking...".Translate()
        };
    }

    private static Color StateColor(ServerProbeState state)
    {
        return state switch
        {
            ServerProbeState.Unreachable => ErrorColor,
            ServerProbeState.PasswordRequired => WarningColor,
            ServerProbeState.WrongPassword => WarningColor,
            _ => DimColor
        };
    }

    internal static void ShowPasswordPrompt(string address, string password, string recordId)
    {
        ShowAfterDisconnect();
        if (root == null) return;
        // Keep the attempted address and record identity for both saved and direct connections.
        OpenEditor(new SavedServer { Id = recordId, Address = address, Password = password }, true,
            string.IsNullOrEmpty(password) ? "Password Required" : "Wrong Password");
        passwordInput.ActivateInputField();
    }

    private static void OpenEditor(SavedServer server, bool direct, string titleKey = null)
    {
        directConnect = direct;
        editor.name = server?.Id ?? "Server editor";
        var title = editor.Find("Editor dialog/Add Server text")?.GetComponent<Text>();
        NebulaeLocalizedText.Set(title, titleKey ?? (direct ? "Direct Connect" : server == null ? "Add Server" : "Edit Server"));
        nameInput.transform.parent.gameObject.SetActive(!direct);
        goalRow.gameObject.SetActive(!direct && goalCombo != null);
        nameInput.text = server?.Name ?? "";
        addressInput.text = server?.Address ?? "";
        addressInput.interactable = server == null || !direct;
        passwordInput.text = server?.Password ?? "";
        var passwordLabel = passwordInput.transform.parent.Find("Password (optional) text").GetComponent<Text>();
        NebulaeLocalizedText.Set(passwordLabel, direct && server != null ? "Password" : "Password (optional)");
        if (goalCombo != null)
        {
            goalCombo.itemIndex = Mathf.Clamp((server?.GoalLevel ?? 0) - 1, -1, 2);
        }
        // Without the name row the remaining two rows must re-center, otherwise the
        // direct-connect dialog keeps a dead gap at the top.
        var addressRow = (RectTransform)addressInput.transform.parent;
        var passwordRow = (RectTransform)passwordInput.transform.parent;
        addressRow.anchoredPosition = new Vector2(0f, direct ? 68f : 32f);
        passwordRow.anchoredPosition = new Vector2(0f, direct ? -4f : -40f);
        NebulaeLocalizedText.Set(commitButton.GetComponentInChildren<Text>(true), direct ? "Join" : "Save");
        goalRow.SetAsLastSibling();
        if (goalCombo != null) AlignComboLeft((RectTransform)goalCombo.transform);
        editor.gameObject.SetActive(true);
        editor.SetAsLastSibling();
    }

    private static void CommitEditor()
    {
        if (!ServerAddress.TryParse(addressInput.text, Config.Options.HostPort, out _))
        {
            InGamePopup.ShowWarning("Invalid Address".Translate(), "Enter a valid server address".Translate(), "OK".Translate());
            return;
        }

        var id = ServerMemoryStore.Instance.FindServer(editor.name)?.Id;
        if (directConnect)
        {
            var address = addressInput.text.Trim();
            var password = passwordInput.text;
            editor.gameObject.SetActive(false);
            UIMainMenu_Patch.JoinGame(address, password, id);
            return;
        }

        if (string.IsNullOrWhiteSpace(nameInput.text))
        {
            InGamePopup.ShowWarning("Invalid Name".Translate(), "Enter a server name".Translate(), "OK".Translate());
            return;
        }

        try
        {
            var saved = ServerMemoryStore.Instance.SaveServer(id, nameInput.text, addressInput.text, passwordInput.text);
            if (goalCombo != null && goalCombo.itemIndex >= 0)
                ServerMemoryStore.Instance.UpdateGoalLevel(saved.Id, goalCombo.itemIndex + 1);
            selectedId = saved.Id;
            editor.gameObject.SetActive(false);
            RefreshList();
            ProbeAll();
        }
        catch (Exception e)
        {
            InGamePopup.ShowWarning("Could not save server".Translate(), e.Message, "OK".Translate());
        }
    }

    private static void JoinSelected()
    {
        var server = Selected();
        if (server == null) return;
        // An empty saved password is valid; only a server authentication failure needs a prompt.
        UIMainMenu_Patch.JoinGame(server.Address, server.Password, server.Id);
    }

    private static void DeleteSelected()
    {
        var server = Selected();
        if (server == null) return;
        InGamePopup.ShowQuestion("Delete Server".Translate(),
            string.Format("Delete {0}?".Translate(), server.Name), "Cancel".Translate(), "Delete".Translate(), null, () =>
            {
                try
                {
                    ServerMemoryStore.Instance.DeleteServer(server.Id);
                    selectedId = null;
                    probeResults.Remove(server.Id);
                    RefreshList();
                    ProbeAll();
                }
                catch (Exception e)
                {
                    InGamePopup.ShowWarning("Could not save server".Translate(), e.Message, "OK".Translate());
                }
            });
    }

    private static SavedServer Selected() => ServerMemoryStore.Instance.FindServer(selectedId);

    private static void FitPanel()
    {
        if (root == null || panel == null) return;
        var width = root.rect.width;
        var height = root.rect.height;
        if (width <= 0 || height <= 0) return;
        panel.localScale = Vector3.one * Mathf.Min(1f, (width - 40f) / PanelWidth, (height - 40f) / PanelHeight);
    }

    private static void StartNewGame()
    {
        root.gameObject.SetActive(false);
        Multiplayer.HostGame(new Server(Config.Options.HostPort));
        Multiplayer.Session.IsInLobby = true;
        UIRoot.instance.galaxySelect._Open();
        UIRoot.instance.uiMainMenu._Close();
    }

    public static void ReturnFromLoadWindow()
    {
        if (!loadWindowPending) return;
        loadWindowPending = false;
        ShowAfterDisconnect();
    }

    public static void LoadSucceeded() => loadWindowPending = false;

    private static void LoadGame()
    {
        loadWindowPending = true;
        root.gameObject.SetActive(false);
        UIRoot.instance.OpenLoadGameWindow();
    }

    /// <summary>Creates a button cloned from the game's own window buttons, keeping its hover
    /// transitions and click sounds; falls back to the galaxy-select button when unavailable.</summary>
    private static Button AddButton(string label, Transform parent, float width, Action click)
    {
        var source = windowButtonSource != null ? (RectTransform)windowButtonSource.transform : buttonTemplate;
        var rect = Object.Instantiate(source, parent, false);
        rect.name = label + " button";
        RemoveSceneTriggers(rect.gameObject);
        rect.gameObject.SetActive(true);
        Center(rect, width, ButtonHeight, Vector2.zero);

        var native = rect.GetComponent<UIButton>();
        var button = native != null && native.button != null ? native.button : rect.GetComponent<Button>();
        if (button == null) button = rect.gameObject.AddComponent<Button>();
        // The stock templates serialize their in-window state (the load button starts
        // disabled until a save is picked); cloned buttons must come up clickable.
        button.enabled = true;
        button.interactable = true;
        button.onClick = new Button.ButtonClickedEvent();
        if (native != null)
        {
            // The stock receiver may carry scene-wired events; the cloned UIButton handles
            // clicks, sounds and transitions on its own.
            var receiver = rect.GetComponent<UIEventReceiver>();
            if (receiver != null) Object.Destroy(receiver);
            native.onClick += _ => click();
        }
        else
        {
            button.onClick.AddListener(new UnityAction(click));
        }

        var text = rect.GetComponentInChildren<Text>(true);
        if (text != null)
        {
            NebulaeLocalizedText.Set(text, label);
            ConfigureText(text, text.text, TextAnchor.MiddleCenter, Mathf.Max(14, text.fontSize));
        }
        return button;
    }

    /// <summary>Builds a plain text on the game's font. Cloned text objects drag along
    /// shadow/outline effects and prefab anchors, so nothing text-like is cloned here.</summary>
    private static Text CreateText(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<Text>();
        text.font = uiFont != null ? uiFont : textTemplate.font;
        text.color = Color.white;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        return text;
    }

    private static Text AddLocalizedText(Transform parent, string label, int size, TextAnchor alignment)
    {
        var text = CreateText(parent, label + " text");
        ConfigureText(text, label.Translate(), alignment, size);
        NebulaeLocalizedText.Set(text, label);
        return text;
    }

    private static Text CloneText(Transform parent, string value, TextAnchor alignment, int size)
    {
        var text = CreateText(parent, "row text");
        return ConfigureText(text, value, alignment, size);
    }

    private static Image AddDivider(Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 position, float height)
    {
        var divider = NewRect("Divider", parent, parent.childCount);
        Place(divider, anchorMin, anchorMax, new Vector2(0.5f, 1f), position, new Vector2(0f, height));
        var image = divider.gameObject.AddComponent<Image>();
        image.color = DividerColor;
        image.raycastTarget = false;
        return image;
    }

    private static Text ConfigureText(Text text, string value, TextAnchor alignment, int size)
    {
        text.text = value;
        text.alignment = alignment;
        text.fontSize = size;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.resizeTextForBestFit = true;
        text.resizeTextMinSize = Mathf.Max(10, size - 5);
        text.resizeTextMaxSize = size;
        return text;
    }

    private static void RemoveSceneTriggers(GameObject target)
    {
        foreach (var trigger in target.GetComponentsInChildren<EventTrigger>(true))
            Object.DestroyImmediate(trigger);
    }

    private static void AnchorColumn(Text text, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        var rect = text.rectTransform;
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
    }

    private static InputField AddInput(RectTransform parent, string label, float y, bool visible)
    {
        var row = NewRect(label + " row", parent, 0);
        Center(row, 580, 58, new Vector2(0, y));
        var labelText = AddLocalizedText(row, label, 18, TextAnchor.MiddleLeft);
        Place(labelText.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
            new Vector2(8, 0), new Vector2(175, 48));

        var sourceInput = inputTemplate.GetComponentInChildren<InputField>(true);
        var fieldObject = Object.Instantiate(sourceInput.gameObject, row, false);
        fieldObject.name = "InputField";
        RemoveSceneTriggers(fieldObject);
        fieldObject.SetActive(true);
        var fieldRect = fieldObject.GetComponent<RectTransform>();
        Center(fieldRect, 370, 44, new Vector2(105, 0));
        var input = fieldObject.GetComponent<InputField>();
        input.characterLimit = label == "Address" ? 255 : 120;
        input.contentType = InputField.ContentType.Standard;
        input.interactable = visible;
        input.UpdateLabel();
        return input;
    }

    private static Sprite playerBadgeSprite;

    /// <summary>Draws a little head-and-shoulders silhouette (like a "player" emoji) into a
    /// runtime texture. The game's own assets are not reliably loaded on the main menu, so
    /// the badge cannot come from LDB or the icon set.</summary>
    private static Sprite GetPlayerBadgeSprite()
    {
        if (playerBadgeSprite != null) return playerBadgeSprite;
        const int size = 48;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        var headCenter = new Vector2(size * 0.5f, size * 0.70f);
        const float headRadius = size * 0.185f;
        var bodyCenter = new Vector2(size * 0.5f, size * 0.06f);
        const float bodyRadiusX = size * 0.37f;
        const float bodyRadiusY = size * 0.36f;
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var point = new Vector2(x + 0.5f, y + 0.5f);
                var headAlpha = Mathf.Clamp01(headRadius - Vector2.Distance(point, headCenter) + 0.5f);
                var ex = (point.x - bodyCenter.x) / bodyRadiusX;
                var ey = (point.y - bodyCenter.y) / bodyRadiusY;
                var bodyAlpha = Mathf.Clamp01((1f - (ex * ex + ey * ey)) * bodyRadiusY + 0.5f);
                var alpha = Mathf.Max(headAlpha, bodyAlpha);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        playerBadgeSprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        playerBadgeSprite.name = "Nebulae player badge";
        return playerBadgeSprite;
    }

    private static RectTransform NewRect(string name, Transform parent, int order)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.SetSiblingIndex(Mathf.Clamp(order, 0, parent.childCount - 1));
        return rect;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static void Center(RectTransform rect, float width, float height, Vector2 position)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(width, height);
        rect.anchoredPosition = position;
    }

    private static void Place(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 position, Vector2 size)
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = pivot;
        rect.sizeDelta = size;
        rect.anchoredPosition = position;
    }
}

/// <summary>Drains probe results and drives the auto refresh while the page is visible.</summary>
internal class MultiplayerPageTicker : MonoBehaviour
{
    private void Update() => MultiplayerPage.Tick(Time.unscaledDeltaTime);
}

/// <summary>Highlights the hovered server row without rebuilding the list.</summary>
internal class RowHoverHandler : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public string ServerId;

    public void OnPointerEnter(PointerEventData eventData) => MultiplayerPage.SetHovered(ServerId, true);
    public void OnPointerExit(PointerEventData eventData) => MultiplayerPage.SetHovered(ServerId, false);
}
