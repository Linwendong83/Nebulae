using NebulaeWorld.MonoBehaviours.Local.Chat;
using UnityEngine;

namespace NebulaeWorld.Chat.ChatLinks;

/// <summary>Shared button tooltip lifecycle for chat links.</summary>
internal static class ChatLinkTooltip
{
    public static void Show(string data, ChatLinkTrigger trigger, ref MonoBehaviour tipObject,
        string title, string description)
    {
        if (string.IsNullOrEmpty(data))
        {
            if (tipObject is UIButtonTip) Object.Destroy(tipObject.gameObject);
            return;
        }

        var rect = (RectTransform)trigger.transform;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, Input.mousePosition, trigger.camera, out var offset);
        offset -= new Vector2(rect.sizeDelta.x / 2, 3f);

        var buttonTip = tipObject as UIButtonTip;
        if (buttonTip == null)
        {
            buttonTip = UIButtonTip.Create(false, title.Translate(), description.Translate(), 2, offset, 0, rect, "", "");
            if (tipObject != null) Object.Destroy(tipObject.gameObject);
            tipObject = buttonTip;
        }

        var wasInactive = !buttonTip.gameObject.activeSelf;
        if (wasInactive) buttonTip.gameObject.SetActive(true);
        if (wasInactive || buttonTip.isActiveAndEnabled && !buttonTip.titleComp.text.Equals(title))
            buttonTip.SetTip(false, title.Translate(), description.Translate(), 2, offset, 0, rect, "", "");
    }
}
