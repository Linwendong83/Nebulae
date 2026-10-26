#region

using NebulaeWorld.MonoBehaviours.Local.Chat;
using UnityEngine;

#endregion

namespace NebulaeWorld.Chat.ChatLinks;

internal interface IChatLinkHandler
{
    void OnClick(string data);

    void OnRightClick(string data);

    void OnHover(string data, ChatLinkTrigger trigger, ref MonoBehaviour tipObject);
}
