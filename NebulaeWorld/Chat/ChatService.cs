#region

using System;
using System.Collections.Generic;
using NebulaeModel.DataStructures.Chat;

#endregion

namespace NebulaeWorld.Chat;

/// <summary>
/// Core chat service managing the shared message history
/// </summary>
internal class ChatService
{
    private const int MAX_MESSAGES = 200;

    /// <summary>
    /// Singleton instance of ChatService
    /// </summary>
    public static ChatService Instance { get; private set; } = new();

    private readonly List<RawChatMessage> messageHistory = new();

    /// <summary>
    /// Event triggered when a new message is added
    /// </summary>
    public event Action<RawChatMessage> OnMessageAdded;

    /// <summary>
    /// Event triggered when a message is removed
    /// </summary>
    public event Action<RawChatMessage> OnMessageRemoved;

    /// <summary>
    /// Gets read-only access to message history
    /// </summary>
    public IReadOnlyList<RawChatMessage> MessageHistory => messageHistory.AsReadOnly();

    /// <summary>
    /// Adds a message to the chat history and triggers display
    /// </summary>
    /// <param name="messageText">The message text</param>
    /// <param name="messageType">The type of message</param>
    /// <param name="userName">The username of the sender (optional)</param>
    /// <returns>The added RawChatMessage reference</returns>
    public RawChatMessage AddMessage(string messageText, ChatMessageType messageType, string userName = null)
    {
        var msg = new RawChatMessage
        {
            MessageText = messageText,
            UserName = userName ?? string.Empty,
            MessageType = messageType
        };

        messageHistory.Add(msg);

        // Maintain max message limit
        if (messageHistory.Count > MAX_MESSAGES)
        {
            messageHistory.RemoveAt(0);
        }

        OnMessageAdded?.Invoke(msg);
        return msg;
    }

    /// <summary>
    /// Clear messages that match the filter predicate
    /// </summary>
    /// <param name="filter">Predicate to filter messages</param>
    public void ClearMessages(Func<RawChatMessage, bool> filter)
    {
        for (var i = messageHistory.Count - 1; i >= 0; i--)
        {
            var msg = messageHistory[i];
            if (filter(msg))
            {
                messageHistory.RemoveAt(i);
                OnMessageRemoved?.Invoke(msg);
            }
        }
    }
}
