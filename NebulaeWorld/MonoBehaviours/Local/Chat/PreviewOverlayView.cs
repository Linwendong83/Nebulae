using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using NebulaeModel;
using NebulaeModel.DataStructures.Chat;
using NebulaeWorld.Chat;
using NebulaeWorld.Player;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NebulaeWorld.MonoBehaviours.Local.Chat;

/// <summary>The in-game version of Nebulae-UI-Preview. All coordinates are Canvas units.</summary>
public sealed class PreviewOverlayView : MonoBehaviour
{
    private const float MessageLifetime = 12f;
    private const float FadeTime = .35f;
    private const int MaxHistory = 200;
    private const int MaxFloating = 4;
    private const float ColumnWidth = 280f;
    private const float RowHeight = 28f;
    private static readonly Color PanelColor = new(.078f, .114f, .188f, .84f);
    private static readonly Color FloatingColor = new(.078f, .114f, .188f, .72f);
    private static readonly Color InputColor = new(0f, 0f, 0f, 75f / 255f);
    private static readonly Color LinkColor = new(105f / 255f, 177f / 255f, 1f);
    private static readonly KeyCode[] ModifierKeys =
    {
        KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl,
        KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.LeftCommand, KeyCode.RightCommand,
        KeyCode.LeftWindows, KeyCode.RightWindows
    };
    private static Font numericFont;
    private static bool numericFontSearched;
    private static XConsole nativeConsole;

    private readonly List<OverlayEntry> entries = new();
    private readonly List<GameObject> floatingObjects = new();
    private readonly List<CanvasGroup> floatingCanvasGroups = new();
    private readonly List<Outline> floatingOutlines = new();
    private RectTransform root;
    private RectTransform listPanel;
    private RectTransform chatPanel;
    private RectTransform floatingRoot;
    private RectTransform historyContent;
    private ScrollRect historyScroll;
    private InputField input;
    private Button newMessagesButton;
    private Text newMessagesLabel;
    private bool chatOpen;
    private bool listOpen;
    private int listPage;
    private float nextListRefresh;
    private Vector2 lastRootSize;

    public event Action<string> OnMessageSubmitted;
    public bool IsPointerIn => chatOpen && RectTransformUtility.RectangleContainsScreenPoint(chatPanel, Input.mousePosition);

    private sealed class OverlayEntry
    {
        public string Name;
        public string Text;
        public int Presence; // +1 joined, -1 left, 0 chat
        public float ReceivedAt;
        public RawChatMessage Source;
    }

    private void Awake()
    {
        root = (RectTransform)transform;
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.offsetMin = root.offsetMax = Vector2.zero;

        listPanel = CreatePanel("Players", root, PanelColor, false);
        listPanel.anchorMin = listPanel.anchorMax = new Vector2(.5f, 1f);
        listPanel.pivot = new Vector2(.5f, 1f);
        listPanel.gameObject.SetActive(false);

        floatingRoot = CreateRect("Recent messages", root);
        floatingRoot.anchorMin = floatingRoot.anchorMax = Vector2.zero;
        floatingRoot.pivot = Vector2.zero;

        chatPanel = CreatePanel("Chat", root, PanelColor, true);
        chatPanel.anchorMin = chatPanel.anchorMax = Vector2.zero;
        chatPanel.pivot = Vector2.zero;
        BuildChatPanel();
        chatPanel.gameObject.SetActive(false);
        RefreshLayout();

        ChatService.Instance.OnMessageAdded += OnMessageAdded;
        ChatService.Instance.OnMessageRemoved += OnMessageRemoved;
        Localization.OnLanguageChange += RefreshLanguage;
        foreach (var message in ChatService.Instance.MessageHistory) OnMessageAdded(message);
    }

    private void OnDestroy()
    {
        ChatService.Instance.OnMessageAdded -= OnMessageAdded;
        ChatService.Instance.OnMessageRemoved -= OnMessageRemoved;
        Localization.OnLanguageChange -= RefreshLanguage;
    }

    private void Update()
    {
        if (!Multiplayer.IsActive || !GameMain.isRunning || GameMain.isFullscreenPaused || !Application.isFocused)
        {
            if (listOpen) SetListOpen(false);
            if (chatOpen) HideChat();
            return;
        }

        if (root.rect.size != lastRootSize) RefreshLayout();

        if (chatOpen && IsFunctionWindowOpen()) HideChat();

        if (chatOpen)
        {
            VFInput.inputing = true;
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                HideChat();
                VFInput.UseEscape();
            }
            else if (!ChatInputState.IsComposing &&
                     (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                if (string.IsNullOrWhiteSpace(input.text)) HideChat();
                else Submit();
            }
            if (chatOpen && !input.isFocused && !IsFunctionWindowOpen()) input.ActivateInputField();
            // The same hotkey that opened the panel closes it. The input field has focus while
            // the panel is up, so the usual "no text field selected" guard cannot apply here.
            if (ShortcutDown(Config.Options.ChatHotkey)) HideChat();
        }
        else if (ShortcutDown(Config.Options.ChatHotkey) && CanOpenChat())
        {
            ShowChat();
        }

        var showList = !chatOpen && ShortcutHeld(Config.Options.PlayerListHotkey) && CanUseGameHotkeys();
        if (showList != listOpen) SetListOpen(showList);
        if (listOpen)
        {
            if (Input.GetKeyDown(KeyCode.RightArrow)) { listPage++; BuildPlayerList(); }
            if (Input.GetKeyDown(KeyCode.LeftArrow)) { listPage = Mathf.Max(0, listPage - 1); BuildPlayerList(); }
            if (Time.unscaledTime >= nextListRefresh) BuildPlayerList();
        }

        UpdateFloating();
    }

    private static bool IsTextInputSelected()
    {
        var selected = EventSystem.current?.currentSelectedGameObject;
        return selected != null &&
               (selected.GetComponent<InputField>() != null || selected.GetComponent<TMP_InputField>() != null);
    }

    private static bool CanUseGameHotkeys()
    {
        return !VFInput.inputing && !IsTextInputSelected() &&
               UIRoot.instance?.uiGame != null && UIRoot.instance.uiGame.gameObject.activeInHierarchy &&
               UIRoot.instance?.optionWindow?.active != true && !IsConsoleOpen();
    }

    private static bool IsConsoleOpen()
    {
        if (nativeConsole == null) nativeConsole = FindObjectOfType<XConsole>();
        return nativeConsole?.showConsole == true;
    }

    private static bool IsFunctionWindowOpen()
    {
        var game = UIRoot.instance?.uiGame;
        return game?.isAnyFunctionWindowActive == true ||
               game?.inventoryWindow?.active == true ||
               game?.researchResultTip?.active == true ||
               game?.screenshotTool?.active == true ||
               IsConsoleOpen() ||
               UIRoot.instance?.optionWindow?.active == true;
    }

    private bool CanOpenChat() => !chatOpen && CanUseGameHotkeys() && !IsFunctionWindowOpen();

    private static bool ShortcutHeld(KeyboardShortcut shortcut)
    {
        if (shortcut.MainKey == KeyCode.None ||
            (!Input.GetKey(shortcut.MainKey) &&
             (shortcut.MainKey != KeyCode.Return || !Input.GetKey(KeyCode.KeypadEnter)))) return false;
        // Direct loop instead of LINQ All: the lambda captured `shortcut`, so each call
        // allocated a closure plus an iterator, and Update calls this several times a frame.
        for (var i = 0; i < ModifierKeys.Length; i++)
        {
            var key = ModifierKeys[i];
            if (Input.GetKey(key) != shortcut.Modifiers.Contains(key)) return false;
        }
        return true;
    }

    private static bool ShortcutDown(KeyboardShortcut shortcut) => ShortcutHeld(shortcut) &&
        (Input.GetKeyDown(shortcut.MainKey) ||
         (shortcut.MainKey == KeyCode.Return && Input.GetKeyDown(KeyCode.KeypadEnter)));

    private void SetListOpen(bool open)
    {
        listOpen = open;
        listPanel.gameObject.SetActive(open);
        if (open) { listPage = 0; BuildPlayerList(); }
        else listPage = 0;
    }

    private void BuildPlayerList()
    {
        if (!listOpen || Multiplayer.Session?.World == null) return;
        nextListRefresh = Time.unscaledTime + .5f;
        foreach (Transform child in listPanel)
        {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }

        var players = new List<(ushort Id, string Name)>();
        var localName = Multiplayer.Session.LocalPlayer?.Data?.Username;
        if (!string.IsNullOrEmpty(localName)) players.Add((Multiplayer.Session.LocalPlayer.Id, localName));
        using (Multiplayer.Session.World.GetRemotePlayersModels(out var remote))
        {
            foreach (var player in remote.OrderBy(pair => pair.Key)) players.Add((player.Key, player.Value.Username));
        }

        var columnWidth = Mathf.Max(160f, Mathf.Min(ColumnWidth, root.rect.width - 24f));
        var rows = Mathf.Clamp(Mathf.FloorToInt((root.rect.height - 72f) / RowHeight), 1, 12);
        var columnsPerPage = Mathf.Max(1, Mathf.FloorToInt((root.rect.width - 24f) / columnWidth));
        var totalColumns = Mathf.Max(1, Mathf.CeilToInt(players.Count / (float)rows));
        var pages = Mathf.Max(1, Mathf.CeilToInt(totalColumns / (float)columnsPerPage));
        listPage = Mathf.Clamp(listPage, 0, pages - 1);
        var firstColumn = listPage * columnsPerPage;
        var visibleColumns = Mathf.Min(columnsPerPage, totalColumns - firstColumn);
        var visibleRows = Mathf.Min(rows, Mathf.Max(0, players.Count - firstColumn * rows));
        listPanel.sizeDelta = new Vector2(visibleColumns * columnWidth + 12f, visibleRows * RowHeight + 14f);
        listPanel.anchoredPosition = new Vector2(0, -24f);

        for (var col = 0; col < visibleColumns; col++)
        {
            for (var row = 0; row < rows; row++)
            {
                var index = (firstColumn + col) * rows + row;
                if (index >= players.Count) break;
                var x = 6f + col * columnWidth;
                var y = -7f - row * RowHeight;
                if (row % 2 == 1)
                {
                    var stripe = CreatePanel("stripe", listPanel, new Color(1f, 1f, 1f, .035f), false);
                    SetTopLeft(stripe, x, y, columnWidth, RowHeight);
                }
                var name = CreateText("name", listPanel, players[index].Name, 16, LinkColor);
                name.alignment = TextAnchor.MiddleLeft;
                name.horizontalOverflow = HorizontalWrapMode.Overflow;
                SetTopLeft((RectTransform)name.transform, x + 8, y, columnWidth - 76, RowHeight);
                FitSingleLine(name);
                var hasPing = PlayerLatencyTracker.TryGet(players[index].Id, out var milliseconds);
                var ping = CreateText("latency", listPanel, hasPing ? $"{milliseconds} ms" : "—", 14,
                    hasPing && milliseconds >= 180 ? new Color(1f, .73f, .4f) : new Color(1f, 1f, 1f, .7f));
                if (!numericFontSearched)
                {
                    numericFontSearched = true;
                    numericFont = Resources.FindObjectsOfTypeAll<Font>()
                        .FirstOrDefault(font => font.name.StartsWith("DIN", StringComparison.OrdinalIgnoreCase));
                }
                if (numericFont != null) ping.font = numericFont;
                ping.alignment = TextAnchor.MiddleRight;
                SetTopLeft((RectTransform)ping.transform, x + columnWidth - 66, y, 58, RowHeight);
            }
        }
    }

    private void BuildChatPanel()
    {
        var viewport = CreateRect("History viewport", chatPanel);
        viewport.anchorMin = new Vector2(0, 0);
        viewport.anchorMax = Vector2.one;
        // Bottom band: the input strip (26) + 8 gap above it.
        viewport.offsetMin = new Vector2(12, 34);
        viewport.offsetMax = new Vector2(-12, -12);
        var viewportImage = viewport.gameObject.AddComponent<Image>();
        viewportImage.color = Color.clear;
        viewportImage.raycastTarget = true;
        viewport.gameObject.AddComponent<RectMask2D>();
        historyScroll = viewport.gameObject.AddComponent<ScrollRect>();
        historyScroll.horizontal = false;
        historyScroll.vertical = true;
        historyScroll.scrollSensitivity = 20;
        historyScroll.viewport = viewport;

        historyContent = CreateRect("Messages", viewport);
        historyContent.anchorMin = new Vector2(0, 1);
        historyContent.anchorMax = Vector2.one;
        historyContent.pivot = new Vector2(.5f, 1);
        historyContent.offsetMin = Vector2.zero;
        historyContent.offsetMax = Vector2.zero;
        var layout = historyContent.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 4;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        var fitter = historyContent.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        historyScroll.content = historyContent;

        input = CreateNativeInput(chatPanel);
        var inputRect = (RectTransform)input.transform;
        inputRect.anchorMin = new Vector2(0, 0);
        inputRect.anchorMax = new Vector2(1, 0);
        // A slim strip just one notch taller than the 16pt text it holds, flush with the
        // panel's bottom edge; the history viewport only reserves the matching band above.
        inputRect.offsetMin = new Vector2(0, 0);
        inputRect.offsetMax = new Vector2(0, 30);

        var newButtonRect = CreatePanel("New messages", chatPanel, new Color(.12f, .19f, .25f, .94f), true);
        newButtonRect.anchorMin = newButtonRect.anchorMax = new Vector2(1, 0);
        newButtonRect.pivot = new Vector2(1, 0);
        newButtonRect.anchoredPosition = new Vector2(-12, 36);
        newButtonRect.sizeDelta = new Vector2(108, 28);
        newMessagesLabel = CreateText("Label", newButtonRect, string.Empty, 14, Color.white);
        newMessagesLabel.alignment = TextAnchor.MiddleCenter;
        Stretch((RectTransform)newMessagesLabel.transform);
        RefreshLanguage();
        newMessagesButton = newButtonRect.gameObject.AddComponent<Button>();
        newMessagesButton.onClick.AddListener(ScrollToBottom);
        newButtonRect.gameObject.SetActive(false);
    }

    /// <summary>
    /// Clone the stock game input field used on the settings window and the multiplayer lobby pages,
    /// so the chat box gets the native look (background, placeholder, caret and focus tint).
    /// </summary>
    private static InputField CreateNativeInput(Transform parent)
    {
        // Same template the settings window and MultiplayerPage clone: the main-menu galaxy-seed input.
        var galaxySeed = GameObject.Find("Overlay Canvas")?.transform
            .Find("Galaxy Select/setting-group/stretch-transform/galaxy-seed");
        var source = galaxySeed?.GetComponentInChildren<InputField>(true) ??
                     UIRoot.instance?.saveGameWindow?.nameInput ??
                     UIRoot.instance?.GetComponentInChildren<InputField>(true);

        RectTransform fieldRect;
        InputField input;
        if (source != null)
        {
            var fieldObject = UnityEngine.Object.Instantiate(source.gameObject, parent, false);
            fieldObject.name = "Input field";
            fieldObject.SetActive(true);
            foreach (var trigger in fieldObject.GetComponentsInChildren<EventTrigger>(true)) Destroy(trigger);
            input = fieldObject.GetComponent<InputField>();
            input.onValueChanged.RemoveAllListeners();
            input.onEndEdit.RemoveAllListeners();
            input.text = string.Empty;
            input.contentType = InputField.ContentType.Standard;
            input.characterLimit = 0;
            input.lineType = InputField.LineType.SingleLine;
            // The stock frame sprite draws a boxed border around the row; the chat overlay
            // wants a flat fill with no outline.
            foreach (var image in fieldObject.GetComponentsInChildren<Image>(true))
            {
                image.sprite = null;
                image.type = Image.Type.Simple;
                image.color = InputColor;
            }
            if (input.textComponent != null) input.textComponent.supportRichText = false;
            if (input.placeholder is Text placeholder)
            {
                placeholder.raycastTarget = false;
                NebulaeLocalizedText.Set(placeholder, "Enter message");
            }
            input.UpdateLabel();
            fieldRect = (RectTransform)fieldObject.transform;
        }
        else
        {
            fieldRect = CreatePanel("Input", parent, InputColor, true);
            var text = CreateText("Text", fieldRect, string.Empty, 20, Color.white);
            text.alignment = TextAnchor.MiddleLeft;
            Stretch((RectTransform)text.transform, 15, 2);
            input = fieldRect.gameObject.AddComponent<InputField>();
            input.textComponent = text;
            input.targetGraphic = fieldRect.GetComponent<Image>();
            input.lineType = InputField.LineType.SingleLine;
        }

        return input;
    }

    public void RefreshLayout()
    {
        if (root == null) return;
        lastRootSize = root.rect.size;
        var width = Mathf.Max(120f, Mathf.Min(460f, root.rect.width - 48f));
        var bottom = Mathf.Min(240f, root.rect.height * .3f);
        var height = Mathf.Max(80f, Mathf.Min(300f, root.rect.height - bottom - 36f));
        chatPanel.anchoredPosition = new Vector2(Mathf.Min(24f, root.rect.width * .03f), bottom);
        chatPanel.sizeDelta = new Vector2(width, height);
        floatingRoot.anchoredPosition = chatPanel.anchoredPosition;
        floatingRoot.sizeDelta = chatPanel.sizeDelta;
        if (listOpen) BuildPlayerList();
        RebuildFloating();
    }

    private void ShowChat()
    {
        if (listOpen) SetListOpen(false);
        chatOpen = true;
        chatPanel.gameObject.SetActive(true);
        floatingRoot.gameObject.SetActive(false);
        ScrollToBottom();
        input.ActivateInputField();
        VFInput.inputing = true;
    }

    private void HideChat()
    {
        chatOpen = false;
        var selectedChatInput = EventSystem.current != null &&
                                EventSystem.current.currentSelectedGameObject == input.gameObject;
        input.DeactivateInputField();
        if (selectedChatInput && EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        chatPanel.gameObject.SetActive(false);
        floatingRoot.gameObject.SetActive(true);
        newMessagesButton.gameObject.SetActive(false);
    }

    private void Submit()
    {
        var text = input.text;
        if (string.IsNullOrWhiteSpace(text)) { HideChat(); return; }
        input.text = string.Empty;
        OnMessageSubmitted?.Invoke(text);
        input.ActivateInputField();
        ScrollToBottom();
    }

    public void AddPresence(string name, bool joined)
    {
        if (string.IsNullOrEmpty(name)) return;
        AddEntry(new OverlayEntry { Name = name, Presence = joined ? 1 : -1, ReceivedAt = Time.unscaledTime });
    }

    private void OnMessageAdded(RawChatMessage message)
    {
        if (message.MessageType != ChatMessageType.PlayerMessage) return;
        AddEntry(new OverlayEntry
        {
            Name = message.UserName ?? string.Empty,
            Text = message.MessageText ?? string.Empty,
            ReceivedAt = Time.unscaledTime,
            Source = message
        });
    }

    private void OnMessageRemoved(RawChatMessage message)
    {
        var index = entries.FindIndex(e => ReferenceEquals(e.Source, message));
        if (index >= 0) { entries.RemoveAt(index); RebuildHistory(); RebuildFloating(); }
    }

    private void AddEntry(OverlayEntry entry)
    {
        var wasAtBottom = !chatOpen || historyScroll.verticalNormalizedPosition <= .02f;
        var previousScrollOffset = historyContent.anchoredPosition.y;
        entries.Add(entry);
        if (entries.Count > MaxHistory)
        {
            entries.RemoveAt(0);
            foreach (Transform child in historyContent)
            {
                if (!child.gameObject.activeSelf) continue;
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
                break;
            }
        }
        AppendHistory(entry);
        LayoutRebuilder.ForceRebuildLayoutImmediate(historyContent);
        RebuildFloating();
        if (chatOpen)
        {
            if (wasAtBottom) ScrollToBottom();
            else
            {
                Canvas.ForceUpdateCanvases();
                historyContent.anchoredPosition = new Vector2(historyContent.anchoredPosition.x,
                    Mathf.Clamp(previousScrollOffset, 0,
                        Mathf.Max(0, historyContent.rect.height - historyScroll.viewport.rect.height)));
                newMessagesButton.gameObject.SetActive(true);
            }
        }
    }

    private static string EntryText(OverlayEntry entry)
    {
        if (entry.Presence > 0) return Localization.isZHCN ? $"{entry.Name} 加入了游戏" : $"{entry.Name} joined the game";
        if (entry.Presence < 0) return Localization.isZHCN ? $"{entry.Name} 离开了游戏" : $"{entry.Name} left the game";
        return string.IsNullOrEmpty(entry.Name) ? entry.Text : $"{entry.Name}: {entry.Text}";
    }

    private void RebuildHistory()
    {
        foreach (Transform child in historyContent)
        {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }
        foreach (var entry in entries) AppendHistory(entry);
        LayoutRebuilder.ForceRebuildLayoutImmediate(historyContent);
    }

    private void AppendHistory(OverlayEntry entry)
    {
        var text = CreateText("Message", historyContent, EntryText(entry), 16,
            entry.Presence == 0 ? Color.white : new Color(.71f, .78f, .82f));
        text.alignment = TextAnchor.UpperLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        var element = text.gameObject.AddComponent<LayoutElement>();
        element.minHeight = 24;
    }

    private void RefreshLanguage()
    {
        if (newMessagesLabel != null) newMessagesLabel.text = Localization.isZHCN ? "有新消息" : "New messages";
        if (historyContent != null)
        {
            var atBottom = historyScroll == null || historyScroll.verticalNormalizedPosition <= .02f;
            var offset = historyContent.anchoredPosition.y;
            RebuildHistory();
            if (chatOpen)
            {
                if (atBottom) ScrollToBottom();
                else historyContent.anchoredPosition = new Vector2(historyContent.anchoredPosition.x, offset);
            }
        }
        if (floatingRoot != null) RebuildFloating();
    }

    private void ScrollToBottom()
    {
        Canvas.ForceUpdateCanvases();
        historyScroll.verticalNormalizedPosition = 0;
        newMessagesButton.gameObject.SetActive(false);
        if (chatOpen) input.ActivateInputField();
    }

    private void UpdateFloating()
    {
        if (chatOpen) return;
        var oldestVisible = entries.Count - MaxFloating;
        if (oldestVisible < 0) oldestVisible = 0;
        var now = Time.unscaledTime;

        // One direct pass instead of two LINQ chains plus a ToList: this runs on every
        // frame while floating messages are on screen.
        var visible = 0;
        for (var i = oldestVisible; i < entries.Count; i++)
        {
            if (now - entries[i].ReceivedAt < MessageLifetime + FadeTime) visible++;
        }
        if (visible != floatingObjects.Count) RebuildFloating();

        var index = 0;
        for (var i = oldestVisible; i < entries.Count && index < floatingObjects.Count; i++)
        {
            var age = now - entries[i].ReceivedAt;
            if (age >= MessageLifetime + FadeTime) continue;
            var target = floatingObjects[index];
            var alpha = age <= MessageLifetime ? 1f : Mathf.Clamp01(1f - (age - MessageLifetime) / FadeTime);
            var group = FloatingCanvasGroup(target, index);
            if (group != null && group.alpha != alpha) group.alpha = alpha;
            var outline = FloatingOutline(target, index);
            if (outline != null)
            {
                var solid = age <= MessageLifetime;
                if (outline.enabled != solid) outline.enabled = solid;
            }
            index++;
        }
    }

    // GetComponent/GetComponentInChildren are native lookups, and the floating objects are
    // stable between rebuilds, so cache what the fade loop touches instead of looking the
    // components up again on every frame.
    private CanvasGroup FloatingCanvasGroup(GameObject target, int index)
    {
        while (floatingCanvasGroups.Count <= index) floatingCanvasGroups.Add(null);
        return floatingCanvasGroups[index] ??= target.GetComponent<CanvasGroup>();
    }

    private Outline FloatingOutline(GameObject target, int index)
    {
        while (floatingOutlines.Count <= index) floatingOutlines.Add(null);
        return floatingOutlines[index] ??= target.GetComponentInChildren<Outline>();
    }

    private void RebuildFloating()
    {
        foreach (var go in floatingObjects)
        {
            go.SetActive(false);
            Destroy(go);
        }
        floatingObjects.Clear();
        floatingCanvasGroups.Clear();
        floatingOutlines.Clear();
        if (floatingRoot == null) return;
        var now = Time.unscaledTime;
        var visible = entries.Skip(Mathf.Max(0, entries.Count - MaxFloating))
            .Where(e => now - e.ReceivedAt < MessageLifetime + FadeTime).ToList();
        var y = 0f;
        for (var i = visible.Count - 1; i >= 0; i--)
        {
            var entry = visible[i];
            var panel = CreatePanel("Recent message", floatingRoot, FloatingColor, false);
            panel.anchorMin = panel.anchorMax = Vector2.zero;
            panel.pivot = Vector2.zero;
            var text = CreateText("Text", panel, EntryText(entry), 16,
                entry.Presence == 0 ? Color.white : new Color(.71f, .78f, .82f));
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            var outline = text.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0, 0, 0, .75f);
            outline.effectDistance = new Vector2(1, -1);
            SetTopLeft((RectTransform)text.transform, 8, -3, chatPanel.rect.width - 16, 78);
            var height = Mathf.Clamp(text.preferredHeight + 6, 28, 78);
            var width = height <= 30 ? Mathf.Min(chatPanel.rect.width, text.preferredWidth + 16) : chatPanel.rect.width;
            panel.sizeDelta = new Vector2(width, height);
            panel.anchoredPosition = new Vector2(0, y);
            Stretch((RectTransform)text.transform, 8, 3);
            panel.gameObject.AddComponent<CanvasGroup>();
            floatingObjects.Insert(0, panel.gameObject);
            y += height + 4;
        }
    }

    private static RectTransform CreateRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    private static RectTransform CreatePanel(string name, Transform parent, Color color, bool raycast)
    {
        var rect = CreateRect(name, parent);
        var image = rect.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = raycast;
        return rect;
    }

    private static Text CreateText(string name, Transform parent, string value, int size, Color color)
    {
        var rect = CreateRect(name, parent);
        var text = rect.gameObject.AddComponent<Text>();
        text.font = UIRoot.instance?.saveGameWindow?.nameInput?.textComponent?.font ?? ChatLocalization.Font;
        text.fontSize = size;
        text.color = color;
        text.text = value;
        text.supportRichText = false;
        text.raycastTarget = false;
        return text;
    }

    private static void SetTopLeft(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
        rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(width, height);
    }

    private static void FitSingleLine(Text text)
    {
        var value = text.text;
        var available = ((RectTransform)text.transform).rect.width;
        if (text.preferredWidth <= available) return;
        var left = 0;
        var right = value.Length;
        while (left < right)
        {
            var middle = (left + right + 1) / 2;
            text.text = value.Substring(0, middle) + "…";
            if (text.preferredWidth <= available) left = middle;
            else right = middle - 1;
        }
        text.text = value.Substring(0, left) + "…";
    }

    private static void Stretch(RectTransform rect, float x = 0, float y = 0)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(x, y);
        rect.offsetMax = new Vector2(-x, -y);
    }
}
