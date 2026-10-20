#region

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using NebulaeModel;
using NebulaeModel.Attributes;
using NebulaeModel.Logger;
using NebulaeWorld.MonoBehaviours.Local;
using NGPT;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIOptionWindow))]
internal class UIOptionWindow_Patch
{
    // Templates
    private static RectTransform checkboxTemplate;
    private static RectTransform comboBoxTemplate;
    private static RectTransform sliderTemplate;
    private static RectTransform inputTemplate;
    private static RectTransform labelTemplate;
    private static RectTransform multiplayerContent;
    private static int multiplayerTabIndex;
    private static Dictionary<string, Action> tempToUICallbacks;
    private static readonly List<Action> languageCallbacks = [];
    private static MultiplayerOptions tempMultiplayerOptions = new();
    private static RectTransform contentContainer;

    private const float TopPadding = 15f;
    private const float RowHeight = 40f;
    private const float LabelX = 30f;
    private const float LabelWidth = 260f;
    private const float ControlX = 295f;
    private const float ControlWidth = 200f;
    private const float ControlHeight = 30f;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIOptionWindow._OnCreate))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnCreate_Postfix(UIOptionWindow __instance)
    {
        tempToUICallbacks = new();
        languageCallbacks.Clear();
        Localization.OnLanguageChange -= RefreshTranslations;
        Localization.OnLanguageChange += RefreshTranslations;
        tempMultiplayerOptions = new();

        // Add multiplayer tab button
        var tabButtons = __instance.tabButtons;
        multiplayerTabIndex = tabButtons.Length;
        var lastTab = tabButtons[tabButtons.Length - 1].GetComponent<RectTransform>();
        var beforeLastTab = tabButtons[tabButtons.Length - 2].GetComponent<RectTransform>();
        var tabOffset = lastTab.anchoredPosition.x - beforeLastTab.anchoredPosition.x;
        var multiplayerTab = Object.Instantiate(lastTab, lastTab.parent, true);
        multiplayerTab.name = "tab-button-multiplayer";
        var anchoredPosition = lastTab.anchoredPosition;
        multiplayerTab.anchoredPosition = new Vector2(anchoredPosition.x + tabOffset, anchoredPosition.y);
        var newTabButtons = tabButtons.AddToArray(multiplayerTab.GetComponent<UIButton>());
        __instance.tabButtons = newTabButtons;

        // Update multiplayer tab text
        var tabText = multiplayerTab.GetComponentInChildren<Text>();
        tabText.GetComponent<Localizer>().enabled = false;
        NebulaeLocalizedText.Set(tabText, "Multiplayer");
        var tabTexts = __instance.tabTexts;
        var newTabTexts = tabTexts.AddToArray(tabText);
        __instance.tabTexts = newTabTexts;

        // Add multiplayer tab content
        // Instantiate from "Miscellaneous" tab which has no scroll elements (yet)
        var tabTweeners = __instance.tabTweeners;
        var contentRectTransform = tabTweeners[4].GetComponent<RectTransform>();
        multiplayerContent = Object.Instantiate(contentRectTransform, contentRectTransform.parent, true);
        multiplayerContent.name = "multiplayer-content";

        // Add revert button
        var newContents = tabTweeners.AddToArray(multiplayerContent.GetComponent<Tweener>());
        __instance.tabTweeners = newContents;
        var revertButtons = __instance.revertButtons;
        var revertButton = multiplayerContent.Find("revert-button").GetComponent<RectTransform>();
        var newRevertButtons = revertButtons.AddToArray(revertButton.GetComponent<UIButton>());
        __instance.revertButtons = newRevertButtons;

        // The cloned tab still contains the game's combo boxes. Destroy them before the tab is shown,
        // otherwise SetTabIndex activates a UIComboBox whose dropdown was not duplicated and Awake throws.
        var staleTabChildren = new List<GameObject>();
        foreach (RectTransform child in multiplayerContent)
        {
            if (child != revertButton)
            {
                staleTabChildren.Add(child.gameObject);
            }
        }
        foreach (var staleChild in staleTabChildren)
        {
            Object.DestroyImmediate(staleChild);
        }

        // Add ScrollView
        var sourceList = tabTweeners[2]?.transform.Find("list")
                      ?? tabTweeners[0]?.transform.Find("list")
                      ?? tabTweeners[3]?.transform.Find("list");

        if (sourceList == null)
        {
            Log.Error("Failed to find list in tabTweeners!");
            return;
        }

        var sourceListRect = sourceList.GetComponent<RectTransform>();
        var list = Object.Instantiate(sourceListRect, multiplayerContent, false);
        list.name = "list";
        list.anchorMin = sourceListRect.anchorMin;
        list.anchorMax = sourceListRect.anchorMax;
        list.pivot = sourceListRect.pivot;
        list.anchoredPosition = sourceListRect.anchoredPosition;
        list.sizeDelta = sourceListRect.sizeDelta;
        list.offsetMin = new Vector2(sourceListRect.offsetMin.x, Mathf.Max(sourceListRect.offsetMin.y, 60f));
        list.offsetMax = new Vector2(sourceListRect.offsetMax.x, 0f);

        var listContent = list.Find("scroll-view/viewport/content").GetComponent<RectTransform>();
        var staleRows = new List<GameObject>();
        foreach (RectTransform child in listContent)
        {
            staleRows.Add(child.gameObject);
        }
        foreach (var stale in staleRows)
        {
            Object.DestroyImmediate(stale);
        }
        var leftoverCombos = list.GetComponentsInChildren<UIComboBox>(true);
        for (var i = 0; i < leftoverCombos.Length; i++)
        {
            var combo = leftoverCombos[i];
            if (combo == null || combo.gameObject == list.gameObject) continue;
            Object.DestroyImmediate(combo.gameObject);
        }
        contentContainer = listContent;
        contentContainer.anchorMin = new Vector2(0, 1);
        contentContainer.anchorMax = new Vector2(1, 1);
        contentContainer.pivot = new Vector2(0, 1);
        contentContainer.anchoredPosition = Vector2.zero;

        // Find control templates - get actual controls, not their parent container
        // The game now uses separate labels/comps containers, so we need to get individual controls
        var videoListContent = tabTweeners[0].transform.Find("list/scroll-view/viewport/content");
        var labelsContainer = videoListContent?.Find("labels");
        var compsContainer = videoListContent?.Find("comps");

        if (labelsContainer == null || compsContainer == null)
        {
            Log.Error("Failed to find labels/comps containers in Video tab!");
            return;
        }

        // Find first checkbox, combobox, slider from comps
        checkboxTemplate = null;
        comboBoxTemplate = null;
        sliderTemplate = null;

        foreach (Transform child in compsContainer)
        {
            if (checkboxTemplate == null && child.name == "CheckBox")
                checkboxTemplate = child.GetComponent<RectTransform>();
            else if (comboBoxTemplate == null && child.name == "ComboBox")
                comboBoxTemplate = child.GetComponent<RectTransform>();
            else if (sliderTemplate == null && child.name == "Slider")
                sliderTemplate = child.GetComponent<RectTransform>();
        }

        // Get a label template
        labelTemplate = labelsContainer.GetChild(0)?.GetComponent<RectTransform>();

        if (checkboxTemplate == null || comboBoxTemplate == null || sliderTemplate == null || labelTemplate == null)
        {
            Log.Error("Failed to find UI templates for multiplayer options!");
            return;
        }

        inputTemplate = CreateInputTemplate(multiplayerContent);
        if (inputTemplate == null)
        {
            Log.Error("Failed to create an input field template for multiplayer options!");
            return;
        }

        var visibleRows = 0;
        try
        {
            visibleRows = AddMultiplayerOptionsProperties();
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to add multiplayer options: {ex}");
        }

        contentContainer.sizeDelta = new Vector2(contentContainer.sizeDelta.x, TopPadding + RowHeight * visibleRows + 20f);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIOptionWindow._OnDestroy))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnDestroy_Postfix()
    {
        tempToUICallbacks?.Clear();
        Localization.OnLanguageChange -= RefreshTranslations;
        languageCallbacks.Clear();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIOptionWindow._OnUpdate))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnUpdate_Prefix()
    {
        // VFInput.escape is latched for the whole key-down frame and closes this window.
        // Eat it while a shortcut is being captured, including the frame after capture ends.
        if (VFInput.escape && KeyBinder.ConsumeEscape())
        {
            VFInput.UseEscape();
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIOptionWindow._OnOpen))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static void _OnOpen_Prefix()
    {
        tempMultiplayerOptions = (MultiplayerOptions)Config.Options.Clone();
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIOptionWindow.ApplyOptions))]
    public static void ApplyOptions()
    {
        Config.Options = tempMultiplayerOptions;
        Config.SaveOptions();
        Config.OnConfigApplied?.Invoke();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UIOptionWindow.OnRevertButtonClick))]
    public static void OnRevertButtonClick_Prefix(int idx)
    {
        if (idx == multiplayerTabIndex)
        {
            tempMultiplayerOptions = new MultiplayerOptions();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIOptionWindow.TempOptionToUI))]
    public static void TempOptionToUI_Postfix()
    {
        var properties = AccessTools.GetDeclaredProperties(typeof(MultiplayerOptions));
        foreach (var prop in properties)
        {
            if (tempToUICallbacks.TryGetValue(prop.Name, out var callback))
            {
                callback();
            }
        }
    }

    private static void RefreshTranslations()
    {
        foreach (var callback in languageCallbacks) callback();
    }

    private static int AddMultiplayerOptionsProperties()
    {
        var properties = AccessTools.GetDeclaredProperties(typeof(MultiplayerOptions));
        var container = contentContainer;
        var row = 0;

        foreach (var prop in properties)
        {
            try
            {
                var displayAttr = prop.GetCustomAttribute<DisplayNameAttribute>();
                var descriptionAttr = prop.GetCustomAttribute<DescriptionAttribute>();
                if (displayAttr == null)
                {
                    continue;
                }

                if (prop.PropertyType == typeof(bool))
                {
                    CreateBooleanControl(displayAttr, descriptionAttr, prop, row, container);
                }
                else if (prop.PropertyType == typeof(int) || prop.PropertyType == typeof(float) ||
                         prop.PropertyType == typeof(ushort))
                {
                    CreateNumberControl(displayAttr, descriptionAttr, prop, row, container);
                }
                else if (prop.PropertyType == typeof(string))
                {
                    CreateStringControl(displayAttr, descriptionAttr, prop, row, container);
                }
                else if (prop.PropertyType.IsEnum)
                {
                    CreateEnumControl(displayAttr, descriptionAttr, prop, row, container);
                }
                else if (prop.PropertyType == typeof(KeyboardShortcut))
                {
                    CreateHotkeyControl(displayAttr, descriptionAttr, prop, row, container);
                }
                else
                {
                    Log.Warn($"MultiplayerOption property \"{prop.Name}\" of type \"{prop.PropertyType}\" not supported.");
                    continue;
                }
                row++;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to create control for property '{prop.Name}': {ex}");
            }
        }

        return row;
    }

    private static RectTransform CreateRowBase(Transform container, int row, string labelText, DescriptionAttribute descriptionAttr)
    {
        var rowGo = new GameObject("row", typeof(RectTransform));
        var rowRect = rowGo.GetComponent<RectTransform>();
        rowRect.SetParent(container, false);
        rowRect.anchorMin = new Vector2(0, 1);
        rowRect.anchorMax = new Vector2(1, 1);
        rowRect.pivot = new Vector2(0, 1);
        rowRect.sizeDelta = new Vector2(0, RowHeight);
        rowRect.anchoredPosition = new Vector2(0, -(TopPadding + row * RowHeight));

        if (descriptionAttr != null)
        {
            var tooltip = rowGo.AddComponent<Tooltip>();
            tooltip.Title = labelText;
            tooltip.Text = descriptionAttr.Description;
        }

        // Add label
        var label = Object.Instantiate(labelTemplate, rowRect, false);
        label.name = "label";
        label.anchorMin = new Vector2(0, 0.5f);
        label.anchorMax = new Vector2(0, 0.5f);
        label.pivot = new Vector2(0, 0.5f);
        label.anchoredPosition = new Vector2(LabelX, 0);
        label.sizeDelta = new Vector2(LabelWidth, ControlHeight);

        var labelLocalizer = label.GetComponentInChildren<Localizer>();
        if (labelLocalizer != null) labelLocalizer.enabled = false;
        var labelTextComp = label.GetComponentInChildren<Text>();
        if (labelTextComp != null)
        {
            labelTextComp.alignment = TextAnchor.MiddleLeft;
            NebulaeLocalizedText.Set(labelTextComp, labelText);
        }

        return rowRect;
    }

    private static RectTransform CreateInputTemplate(Transform parent)
    {
        InputField sourceInput = null;

        // Try galaxy-seed from main menu first
        var overlay = GameObject.Find("Overlay Canvas")?.GetComponent<RectTransform>();
        if (overlay != null)
        {
            var galaxySelect = overlay.Find("Galaxy Select");
            var setting = galaxySelect?.Find("setting-group");
            var galaxySeed = setting?.Find("stretch-transform/galaxy-seed");
            sourceInput = galaxySeed?.GetComponentInChildren<InputField>(true);
        }

        // Fallback: search UIRoot for any InputField
        if (sourceInput == null && UIRoot.instance != null)
        {
            sourceInput = UIRoot.instance.GetComponentInChildren<InputField>(true);
        }

        if (sourceInput == null)
        {
            Log.Error("Failed to find any InputField template in the game UI!");
            return null;
        }

        var clone = Object.Instantiate(sourceInput.gameObject, parent, false);
        clone.name = "inputTemplate";
        RemoveSceneTriggers(clone);

        var rect = clone.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(ControlWidth, ControlHeight);

        var input = clone.GetComponent<InputField>();
        input.text = string.Empty;
        input.characterLimit = 0;
        input.lineType = InputField.LineType.SingleLine;

        clone.SetActive(false);
        return rect;
    }

    private static void RemoveSceneTriggers(GameObject target)
    {
        foreach (var trigger in target.GetComponentsInChildren<EventTrigger>(true))
        {
            Object.DestroyImmediate(trigger);
        }
    }

    private static void PositionControl(RectTransform controlRect, float width = ControlWidth, float height = ControlHeight)
    {
        controlRect.anchorMin = new Vector2(0, 0.5f);
        controlRect.anchorMax = new Vector2(0, 0.5f);
        controlRect.pivot = new Vector2(0, 0.5f);
        controlRect.anchoredPosition = new Vector2(ControlX, 0);
        controlRect.sizeDelta = new Vector2(width, height);
    }

    private static void CreateBooleanControl(DisplayNameAttribute control, DescriptionAttribute descriptionAttr,
        PropertyInfo prop, int rowIdx, Transform container)
    {
        var row = CreateRowBase(container, rowIdx, control.DisplayName, descriptionAttr);
        row.name = prop.Name;

        var controlObj = Object.Instantiate(checkboxTemplate, row, false);
        controlObj.name = "checkbox";
        PositionControl(controlObj, 30f, 30f);

        foreach (var text in controlObj.GetComponentsInChildren<Text>(true))
        {
            text.text = string.Empty;
        }

        var toggle = controlObj.GetComponentInChildren<UIToggle>();
        if (toggle != null)
        {
            toggle.toggle.onValueChanged.RemoveAllListeners();
            toggle.toggle.onValueChanged.AddListener(value =>
            {
                prop.SetValue(tempMultiplayerOptions, value, null);
            });

            tempToUICallbacks[prop.Name] = () =>
            {
                toggle.isOn = (bool)prop.GetValue(tempMultiplayerOptions, null);
            };

            toggle.isOn = (bool)prop.GetValue(tempMultiplayerOptions, null);
        }
    }

    private static void CreateNumberControl(DisplayNameAttribute control, DescriptionAttribute descriptionAttr,
        PropertyInfo prop, int rowIdx, Transform container)
    {
        var row = CreateRowBase(container, rowIdx, control.DisplayName, descriptionAttr);
        row.name = prop.Name;

        var rangeAttr = prop.GetCustomAttribute<UIRangeAttribute>();
        var isSlider = rangeAttr is { Slider: true };
        var isFloatingPoint = prop.PropertyType == typeof(float) || prop.PropertyType == typeof(double);

        if (isSlider)
        {
            var sliderObj = Object.Instantiate(sliderTemplate, row, false);
            sliderObj.name = "slider";
            var rect = sliderObj.GetComponent<RectTransform>();
            PositionControl(rect, ControlWidth, ControlHeight);

            var slider = sliderObj.GetComponentInChildren<Slider>();
            slider.minValue = rangeAttr.Min;
            slider.maxValue = rangeAttr.Max;
            slider.wholeNumbers = !isFloatingPoint;
            var sliderThumbText = slider.GetComponentInChildren<Text>();
            slider.onValueChanged.RemoveAllListeners();
            slider.onValueChanged.AddListener(value =>
            {
                prop.SetValue(tempMultiplayerOptions, Convert.ChangeType(value, prop.PropertyType), null);
                if (sliderThumbText != null)
                {
                    sliderThumbText.text = value.ToString(isFloatingPoint ? "0.00" : "0");
                }
            });

            tempToUICallbacks[prop.Name] = () =>
            {
                var val = Convert.ToSingle(prop.GetValue(tempMultiplayerOptions, null));
                slider.value = val;
                if (sliderThumbText != null)
                {
                    sliderThumbText.text = val.ToString(isFloatingPoint ? "0.00" : "0");
                }
            };

            var initialVal = Convert.ToSingle(prop.GetValue(tempMultiplayerOptions, null));
            slider.value = initialVal;
            if (sliderThumbText != null)
            {
                sliderThumbText.text = initialVal.ToString(isFloatingPoint ? "0.00" : "0");
            }
        }
        else
        {
            var inputFieldObj = Object.Instantiate(inputTemplate.gameObject, row, false);
            inputFieldObj.name = "number-input";
            inputFieldObj.SetActive(true);
            var rect = inputFieldObj.GetComponent<RectTransform>();
            PositionControl(rect, ControlWidth, ControlHeight);

            var input = inputFieldObj.GetComponent<InputField>();
            input.contentType = isFloatingPoint ? InputField.ContentType.DecimalNumber : InputField.ContentType.IntegerNumber;
            if (prop.PropertyType == typeof(ushort))
            {
                input.characterLimit = 5;
            }

            input.onValueChanged.RemoveAllListeners();
            input.onValueChanged.AddListener(str =>
            {
                try
                {
                    var converter = TypeDescriptor.GetConverter(prop.PropertyType);
                    var value = (IComparable)converter.ConvertFromString(str);

                    if (rangeAttr != null)
                    {
                        var min = (IComparable)Convert.ChangeType(rangeAttr.Min, prop.PropertyType);
                        var max = (IComparable)Convert.ChangeType(rangeAttr.Max, prop.PropertyType);
                        if (value.CompareTo(min) < 0) value = min;
                        if (value.CompareTo(max) > 0) value = max;
                    }

                    prop.SetValue(tempMultiplayerOptions, value, null);
                }
                catch
                {
                    // Ignore invalid input while typing
                }
            });

            input.onEndEdit.RemoveAllListeners();
            input.onEndEdit.AddListener(_ =>
            {
                input.text = prop.GetValue(tempMultiplayerOptions, null)?.ToString() ?? string.Empty;
            });

            tempToUICallbacks[prop.Name] = () =>
            {
                input.text = prop.GetValue(tempMultiplayerOptions, null)?.ToString() ?? string.Empty;
            };

            input.text = prop.GetValue(tempMultiplayerOptions, null)?.ToString() ?? string.Empty;
        }
    }

    private static void CreateStringControl(DisplayNameAttribute control, DescriptionAttribute descriptionAttr,
        PropertyInfo prop, int rowIdx, Transform container)
    {
        var row = CreateRowBase(container, rowIdx, control.DisplayName, descriptionAttr);
        row.name = prop.Name;

        var contentTypeAttr = prop.GetCustomAttribute<UIContentTypeAttribute>();

        var inputFieldObj = Object.Instantiate(inputTemplate.gameObject, row, false);
        inputFieldObj.name = "input";
        inputFieldObj.SetActive(true);
        var rect = inputFieldObj.GetComponent<RectTransform>();
        PositionControl(rect, ControlWidth, ControlHeight);

        var input = inputFieldObj.GetComponent<InputField>();
        if (contentTypeAttr != null)
        {
            input.contentType = contentTypeAttr.ContentType;
            if (contentTypeAttr.ContentType == InputField.ContentType.Password)
            {
                input.inputType = InputField.InputType.Password;
                input.asteriskChar = '*';
                if (input.placeholder is Text ph)
                {
                    ph.text = "Password...".Translate();
                }
            }
        }

        input.onValueChanged.RemoveAllListeners();
        input.onValueChanged.AddListener(value =>
        {
            prop.SetValue(tempMultiplayerOptions, value, null);
        });

        tempToUICallbacks[prop.Name] = () =>
        {
            input.text = prop.GetValue(tempMultiplayerOptions, null) as string ?? string.Empty;
        };

        input.text = prop.GetValue(tempMultiplayerOptions, null) as string ?? string.Empty;
    }

    private static void CreateEnumControl(DisplayNameAttribute control, DescriptionAttribute descriptionAttr, PropertyInfo prop,
        int rowIdx, Transform container)
    {
        var row = CreateRowBase(container, rowIdx, control.DisplayName, descriptionAttr);
        row.name = prop.Name;

        var comboObj = Object.Instantiate(comboBoxTemplate, row, false);
        comboObj.name = "combobox";
        var rect = comboObj.GetComponent<RectTransform>();
        PositionControl(rect, ControlWidth, ControlHeight);

        var combo = comboObj.GetComponentInChildren<UIComboBox>();
        var names = Enum.GetNames(prop.PropertyType);
        void RefreshItems()
        {
            combo.Items = names.Select(name => name.Translate()).ToList();
            combo.UpdateItems();
            if (combo.itemIndex >= 0 && combo.itemIndex < combo.Items.Count)
                combo.text = combo.Items[combo.itemIndex];
        }
        RefreshItems();
        languageCallbacks.Add(RefreshItems);
        combo.ItemsData = Enum.GetValues(prop.PropertyType).OfType<int>().ToList();
        combo.onItemIndexChange.RemoveAllListeners();
        combo.onItemIndexChange.AddListener(() => { prop.SetValue(tempMultiplayerOptions, combo.itemIndex, null); });

        tempToUICallbacks[prop.Name] = () =>
        {
            combo.itemIndex = (int)prop.GetValue(tempMultiplayerOptions, null);
        };

        combo.itemIndex = (int)prop.GetValue(tempMultiplayerOptions, null);
    }

    private static void CreateHotkeyControl(DisplayNameAttribute control, DescriptionAttribute descriptionAttr,
        PropertyInfo prop, int rowIdx, Transform container)
    {
        var row = CreateRowBase(container, rowIdx, control.DisplayName, descriptionAttr);
        row.name = prop.Name;

        var entryPrefab = UIRoot.instance != null ? UIRoot.instance.optionWindow?.entryPrefab : null;
        if (entryPrefab == null)
        {
            Log.Error("Failed to find the game key entry for multiplayer hotkeys!");
            return;
        }

        var current = (KeyboardShortcut)prop.GetValue(tempMultiplayerOptions, null);
        var fallback = (KeyboardShortcut)prop.GetValue(new MultiplayerOptions(), null);
        var keyBinder = KeyBinder.Create(row, entryPrefab, fallback);
        keyBinder.OnEdit = shortcut =>
        {
            prop.SetValue(tempMultiplayerOptions, shortcut, null);
        };
        languageCallbacks.Add(keyBinder.RefreshText);

        tempToUICallbacks[prop.Name] = () =>
        {
            keyBinder.SetShortcut((KeyboardShortcut)prop.GetValue(tempMultiplayerOptions, null));
        };

        keyBinder.SetShortcut(current);
    }

    public class Tooltip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public string Title;
        public string Text;
        private UIButtonTip tip;

        public void OnDisable()
        {
            if (tip != null)
            {
                Destroy(tip.gameObject);
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            tip = UIButtonTip.Create(true, Title.Translate(), Text.Translate(), 2, new Vector2(0, 0), 508, gameObject.transform, "", "");
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (tip != null)
            {
                Destroy(tip.gameObject);
            }
        }
    }

    public class KeyBinder : MonoBehaviour
    {
        private static readonly List<KeyBinder> Active = [];

        public Action<KeyboardShortcut> OnEdit;
        private UIButton button;
        private UIButton defaultButton;
        private UIButton noneButton;
        private Text keyText;
        private Text waitingText;
        private KeyboardShortcut shortcut;
        private KeyboardShortcut defaultShortcut;
        private Color builtinColor = Color.white;
        private Color overrideColor = Color.white;
        private bool listening;
        private bool suppressEscape;
        private int armFrame = -1;

        public static KeyBinder Create(RectTransform row, UIKeyEntry entryPrefab, KeyboardShortcut fallback)
        {
            // Keep the prefab instance whole: the row must look exactly like the game's key
            // entries (key column at 450, bind bar at 800, default/none buttons at 930/1020).
            // Only the UIKeyEntry component is removed - its Update() depends on BuiltinKey and
            // the game's option window. Destroying any child (e.g. the waiting text that lives
            // under the prefab root) would leave destroyed transforms behind and NRE later.
            var entryObject = Object.Instantiate(entryPrefab.gameObject, row, false);
            entryObject.SetActive(true);
            var entry = entryObject.GetComponent<UIKeyEntry>();

            var functionText = entry.functionText;
            var binder = row.gameObject.AddComponent<KeyBinder>();
            binder.defaultShortcut = fallback;
            binder.builtinColor = entry.builtinColor;
            binder.overrideColor = entry.overrideColor;
            binder.button = entry.inputUIButton;
            binder.defaultButton = entry.setDefaultUIButton;
            binder.noneButton = entry.setNoneKeyUIButton;
            binder.keyText = entry.keyText;
            binder.waitingText = entry.waitingText;
            Object.DestroyImmediate(entry);

            // The row label created by CreateRowBase already shows the option name, so the
            // prefab's own function text (rendered by the entry root) would double it.
            if (functionText != null)
            {
                functionText.enabled = false;
            }

            var entryRect = (RectTransform)entryObject.transform;
            entryRect.anchorMin = new Vector2(0f, 0.5f);
            entryRect.anchorMax = new Vector2(0f, 0.5f);
            entryRect.pivot = new Vector2(0f, 0.5f);
            entryRect.anchoredPosition = new Vector2(LabelX, 0f);
            entryRect.localScale = Vector3.one;

            binder.button.onClick += binder.OnButtonClick;
            binder.defaultButton.onClick += binder.OnDefaultClick;
            binder.noneButton.onClick += binder.OnNoneClick;
            binder.RefreshText();
            return binder;
        }

        public static bool ConsumeEscape()
        {
            var eat = false;
            foreach (var binder in Active)
            {
                if (!binder.listening && !binder.suppressEscape) continue;
                eat = true;
                binder.StopListening();
                binder.suppressEscape = false;
            }
            return eat;
        }

        public void SetShortcut(KeyboardShortcut newShortcut)
        {
            shortcut = newShortcut;
            RefreshText();
        }

        public void RefreshText()
        {
            if (keyText == null) return;
            keyText.text = Format(shortcut);
            keyText.color = shortcut.Equals(defaultShortcut) ? builtinColor : overrideColor;
        }

        private void OnEnable()
        {
            if (!Active.Contains(this)) Active.Add(this);
        }

        private void OnDisable()
        {
            Active.Remove(this);
            if (listening) StopListening();
        }

        private void OnDestroy()
        {
            Active.Remove(this);
            if (button != null) button.onClick -= OnButtonClick;
            if (defaultButton != null) defaultButton.onClick -= OnDefaultClick;
            if (noneButton != null) noneButton.onClick -= OnNoneClick;
        }

        private void OnDefaultClick(int _) => Apply(defaultShortcut);

        private void OnNoneClick(int _) => Apply(KeyboardShortcut.Empty);

        private void OnButtonClick(int _)
        {
            if (listening)
            {
                StopListening();
                return;
            }

            listening = true;
            armFrame = Time.frameCount;
            button.highlighted = true;
            waitingText.gameObject.SetActive(true);
        }

        private void Update()
        {
            if (!listening) return;

            button.highlighted = true;
            waitingText.gameObject.SetActive(true);

            if (Input.GetKeyDown(KeyCode.Escape) || VFInput.escape)
            {
                suppressEscape = true;
                StopListening();
                return;
            }

            if (Time.frameCount == armFrame) return;

            if (!button._isPointerEnter && (Input.GetKeyDown(KeyCode.Mouse0) || Input.GetKeyDown(KeyCode.Mouse1)))
            {
                StopListening();
                return;
            }

            if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete))
            {
                Apply(KeyboardShortcut.Empty);
                return;
            }

            var mouse = ReadMouse();
            if (mouse != KeyCode.None)
            {
                Apply(WithModifiers(mouse));
                return;
            }

            foreach (KeyCode code in Enum.GetValues(typeof(KeyCode)))
            {
                if (code == KeyCode.None || code == KeyCode.Escape || IsModifier(code) || (int)code >= (int)KeyCode.Mouse0)
                    continue;
                if (!Input.GetKeyUp(code)) continue;
                Apply(WithModifiers(code));
                return;
            }
        }

        private void Apply(KeyboardShortcut newShortcut)
        {
            shortcut = newShortcut;
            StopListening();
            OnEdit?.Invoke(shortcut);
        }

        private void StopListening()
        {
            listening = false;
            if (button != null) button.highlighted = false;
            if (waitingText != null) waitingText.gameObject.SetActive(false);
            RefreshText();
        }

        private KeyCode ReadMouse()
        {
            if (Input.GetKeyDown(KeyCode.Mouse0) && button._isPointerEnter) return KeyCode.None;
            if (Input.GetKeyDown(KeyCode.Mouse0)) return KeyCode.Mouse0;
            if (Input.GetKeyDown(KeyCode.Mouse1)) return KeyCode.Mouse1;
            if (Input.GetKeyDown(KeyCode.Mouse2)) return KeyCode.Mouse2;
            if (Input.GetKeyDown(KeyCode.Mouse3)) return KeyCode.Mouse3;
            if (Input.GetKeyDown(KeyCode.Mouse4)) return KeyCode.Mouse4;
            if (Input.GetKeyDown(KeyCode.Mouse5)) return KeyCode.Mouse5;
            if (Input.GetKeyDown(KeyCode.Mouse6)) return KeyCode.Mouse6;
            return KeyCode.None;
        }

        private static KeyboardShortcut WithModifiers(KeyCode main)
        {
            var modifiers = new List<KeyCode>();
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                modifiers.Add(KeyCode.LeftShift);
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                modifiers.Add(KeyCode.LeftControl);
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
                modifiers.Add(KeyCode.LeftAlt);
            return new KeyboardShortcut(main, modifiers.ToArray());
        }

        private static bool IsModifier(KeyCode code) =>
            code is KeyCode.LeftShift or KeyCode.RightShift
                or KeyCode.LeftControl or KeyCode.RightControl
                or KeyCode.LeftAlt or KeyCode.RightAlt
                or KeyCode.LeftCommand or KeyCode.RightCommand
                or KeyCode.LeftWindows or KeyCode.RightWindows
                or KeyCode.LeftApple or KeyCode.RightApple;

        private static string Format(KeyboardShortcut value)
        {
            if (value.MainKey == KeyCode.None) return "无按键".Translate();

            byte modifier = 0;
            foreach (var key in value.Modifiers)
            {
                if (key is KeyCode.LeftShift or KeyCode.RightShift) modifier |= CombineKey.SHIFT_COMB;
                else if (key is KeyCode.LeftControl or KeyCode.RightControl) modifier |= CombineKey.CTRL_COMB;
                else if (key is KeyCode.LeftAlt or KeyCode.RightAlt) modifier |= CombineKey.ALT_COMB;
            }

            return new CombineKey((int)value.MainKey, modifier, default, false).ToString();
        }
    }
}
