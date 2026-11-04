#region

using NebulaeWorld.MonoBehaviours.Local.Chat;
using UnityEngine;

#endregion

namespace NebulaeWorld.Chat.ChatLinks;

internal class CopyTextChatLinkHandler : IChatLinkHandler
{
    public void OnClick(string data)
    {
        GUIUtility.systemCopyBuffer = data;
    }

    public void OnRightClick(string data)
    {
        OnClick(data);
    }

    public void OnHover(string data, ChatLinkTrigger trigger, ref MonoBehaviour tipObject) =>
        ChatLinkTooltip.Show(data, trigger, ref tipObject, "Copy", "Click to copy to clipboard");
}
