using System;
using NebulaeWorld.MonoBehaviours.Local;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NebulaePatcher.UI;

// Widget construction for the multiplayer page: every control is cloned from the game's own
// templates so the page keeps the vanilla look without shipping prefabs.
internal static partial class MultiplayerPage
{
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
