#region

using System.Collections.Generic;
using NebulaeWorld.Chat.ChatLinks;

#endregion

namespace NebulaeWorld.Chat;

internal static class RichChatLinkRegistry
{
    private static readonly Dictionary<string, IChatLinkHandler> handlers = new();

    static RichChatLinkRegistry()
    {
        RegisterChatLinkHandler("signal", new SignalChatLinkHandler());
        RegisterChatLinkHandler("copytext", new CopyTextChatLinkHandler());
        RegisterChatLinkHandler("navigate", new NavigateChatLinkHandler());
    }

    private static void RegisterChatLinkHandler(string linkID, IChatLinkHandler handler)
    {
        if (handler == null)
        {
            return;
        }
        if (handlers.ContainsKey(linkID))
        {
            return;
        }

        handlers.Add(linkID, handler);
    }

    public static string ParseRichText(string linkString, out string linkData)
    {
        linkData = "";
        var splitStrings = linkString.Split(' ');
        if (splitStrings.Length < 2)
        {
            return "";
        }

        linkData = linkString.Substring(splitStrings[0].Length + 1);
        return splitStrings[0];
    }

    public static IChatLinkHandler GetChatLinkHandler(string linkID)
    {
        return handlers.TryGetValue(linkID, out var handler) ? handler : null;
    }
}
