using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using NebulaeModel.Utils;
using NebulaeWorld;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NebulaePatcher.Patches.Dynamic;

// Keep the slider's "More" position separate from the actual galaxy size.
internal sealed class StarCountSelection
{
    public const int More = GalaxyLimits.VanillaMaxStars + 1;
    public int CustomCount { get; private set; } = More;
    public bool IsMore { get; private set; }

    public void Reset()
    {
        CustomCount = More;
        IsMore = false;
    }

    public int Select(int position)
    {
        IsMore = position >= More;
        return IsMore ? CustomCount : Math.Max(GalaxyLimits.MinStars, position);
    }

    public void Display(int count)
    {
        IsMore = count > GalaxyLimits.VanillaMaxStars;
        if (IsMore) CustomCount = count;
    }

    public bool TrySubmit(string text, bool canEdit, out int count)
    {
        count = CustomCount;
        if (!IsMore || !canEdit || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
        CustomCount = count = Math.Max(More, Math.Min(GalaxyLimits.MaxStars, parsed));
        return true;
    }
}

internal static class StarCountControl
{
    internal const string InputName = "Nebulae star count input";

    private sealed class Widgets
    {
        public readonly StarCountSelection Selection = new();
        public InputField Input;
        public CanvasGroup InputGroup;
        public float Minimum, Maximum;
        public bool WholeNumbers, Active;
    }

    private static readonly ConditionalWeakTable<UIGalaxySelect, Widgets> Controls = new();
    private static bool CanEdit => !Multiplayer.IsActive || Multiplayer.Session.IsServer;

    public static void Open(UIGalaxySelect instance)
    {
        if (!UIGalaxySelect_Patch.UsesExtendedStarRange())
        {
            Close(instance);
            return;
        }

        var widgets = Controls.GetValue(instance, Create);
        widgets.Selection.Reset();
        if (!widgets.Active)
        {
            var slider = instance.starCountSlider;
            widgets.Minimum = slider.minValue;
            widgets.Maximum = slider.maxValue;
            widgets.WholeNumbers = slider.wholeNumbers;
            slider.SetValueWithoutNotify(GalaxyLimits.VanillaMaxStars);
            slider.minValue = GalaxyLimits.MinStars;
            slider.maxValue = StarCountSelection.More;
            slider.wholeNumbers = true;
            widgets.Active = true;
        }
        widgets.Input.SetTextWithoutNotify(widgets.Selection.CustomCount.ToString(CultureInfo.InvariantCulture));
        widgets.Input.interactable = false;
        widgets.InputGroup.alpha = 0.35f;
        widgets.Input.gameObject.SetActive(true);
    }

    private static Widgets Create(UIGalaxySelect instance)
    {
        var sliderRect = (RectTransform)instance.starCountSlider.transform;
        // The native number label lives inside the moving handle. Place the new
        // field beside the track, under its fixed parent, so it never moves with it.
        var parent = (RectTransform)sliderRect.parent;
        var corners = new Vector3[4];
        sliderRect.GetWorldCorners(corners);
        var right = parent.InverseTransformPoint(corners[2]);
        var bottom = parent.InverseTransformPoint(corners[0]);

        // Instantiate inactive so copied seed listeners/triggers never receive UI events.
        var holder = new GameObject("Nebulae star count input template", typeof(RectTransform));
        holder.SetActive(false);
        holder.transform.SetParent(parent, false);
        var field = Object.Instantiate(instance.seedInput, holder.transform, false);
        field.name = InputName;
        field.onValueChanged = new InputField.OnChangeEvent();
        field.onEndEdit = new InputField.EndEditEvent();
        field.onSubmit = new InputField.SubmitEvent();
        foreach (var trigger in field.GetComponentsInChildren<EventTrigger>(true)) Object.DestroyImmediate(trigger);
        foreach (var localizer in field.GetComponentsInChildren<Localizer>(true)) Object.DestroyImmediate(localizer);
        field.contentType = InputField.ContentType.IntegerNumber;
        field.onValidateInput = (_, _, character) => character >= '0' && character <= '9' ? character : '\0';
        field.characterLimit = 4;
        field.lineType = InputField.LineType.SingleLine;
        field.textComponent.alignment = TextAnchor.MiddleCenter;
        var textRect = field.textComponent.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(4f, 0f);
        textRect.offsetMax = new Vector2(-4f, 0f);
        if (field.placeholder != null) field.placeholder.gameObject.SetActive(false);
        var navigation = field.navigation;
        navigation.mode = Navigation.Mode.None;
        field.navigation = navigation;
        var rect = (RectTransform)field.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(sliderRect.anchorMax.x, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.sizeDelta = new Vector2(44f, 22f);
        rect.anchoredPosition = new Vector2(right.x - parent.rect.xMin - parent.rect.width * rect.anchorMin.x + 6f,
            (right.y + bottom.y) * 0.5f - parent.rect.center.y);
        field.gameObject.SetActive(false);
        field.transform.SetParent(parent, false);
        Object.Destroy(holder);
        var group = field.GetComponent<CanvasGroup>() ?? field.gameObject.AddComponent<CanvasGroup>();
        var widgets = new Widgets { Input = field, InputGroup = group };
        field.onEndEdit.AddListener(text => Submit(instance, text));
        return widgets;
    }

    public static void Close(UIGalaxySelect instance)
    {
        if (!Controls.TryGetValue(instance, out var widgets) || !widgets.Active) return;
        // Disable submission before hiding the field (which can cause an end-edit event).
        widgets.Active = false;
        widgets.Input.interactable = false;
        widgets.InputGroup.alpha = 0.35f;
        widgets.Input.gameObject.SetActive(false);
        var slider = instance.starCountSlider;
        slider.SetValueWithoutNotify(Math.Max(widgets.Minimum, Math.Min(widgets.Maximum, slider.value)));
        slider.minValue = widgets.Minimum;
        slider.maxValue = widgets.Maximum;
        slider.wholeNumbers = widgets.WholeNumbers;
        var desc = NativeGameAccess.GetGalaxyDescription(instance);
        if (desc != null) instance.starCountText.text = desc.starCount.ToString(CultureInfo.InvariantCulture);
    }

    public static void ChangeSlider(UIGalaxySelect instance)
    {
        if (!Controls.TryGetValue(instance, out var widgets) || !widgets.Active || !CanEdit) return;
        var count = widgets.Selection.Select((int)instance.starCountSlider.value);
        UpdateInput(instance, widgets);
        ApplyCount(instance, count);
    }

    public static void Refresh(UIGalaxySelect instance)
    {
        if (!Controls.TryGetValue(instance, out var widgets) || !widgets.Active) return;
        var desc = NativeGameAccess.GetGalaxyDescription(instance);
        if (desc == null) return;
        widgets.Selection.Display(desc.starCount);
        instance.starCountSlider.SetValueWithoutNotify(Math.Min(desc.starCount, StarCountSelection.More));
        UpdateInput(instance, widgets);
    }

    private static void UpdateInput(UIGalaxySelect instance, Widgets widgets)
    {
        instance.starCountText.text = widgets.Selection.IsMore
            ? "More".Translate()
            : ((int)instance.starCountSlider.value).ToString(CultureInfo.InvariantCulture);
        widgets.Input.SetTextWithoutNotify(widgets.Selection.CustomCount.ToString(CultureInfo.InvariantCulture));
        var enabled = widgets.Selection.IsMore && CanEdit;
        widgets.Input.interactable = enabled;
        widgets.InputGroup.alpha = enabled ? 1f : 0.35f;
    }

    public static void SubmitPending(UIGalaxySelect instance)
    {
        if (Controls.TryGetValue(instance, out var widgets)) Submit(instance, widgets.Input.text);
    }

    private static void Submit(UIGalaxySelect instance, string text)
    {
        if (!Controls.TryGetValue(instance, out var widgets) || !widgets.Active) return;
        // Read the live slider as well: deselection may run before its change listener.
        var canEdit = UIGalaxySelect_Patch.UsesExtendedStarRange() && CanEdit &&
                      instance.starCountSlider.interactable && instance.starCountSlider.value >= StarCountSelection.More;
        var valid = widgets.Selection.TrySubmit(text, canEdit, out var count);
        widgets.Input.SetTextWithoutNotify(widgets.Selection.CustomCount.ToString(CultureInfo.InvariantCulture));
        if (valid) ApplyCount(instance, count);
    }

    private static void ApplyCount(UIGalaxySelect instance, int count)
    {
        var desc = NativeGameAccess.GetGalaxyDescription(instance);
        if (desc == null || desc.starCount == count) return;
        desc.starCount = count;
        // The native refresh also sends the normal lobby settings update.
        instance.SetStarmapGalaxy();
    }
}
