using System;
using NebulaeModel;
using NebulaeModel.DataStructures.Chat;
using NebulaeModel.Packets.Chat;
using NebulaeWorld.Chat;
using UnityEngine;

namespace NebulaeWorld.MonoBehaviours.Local.Chat;

/// <summary>Connects the quiet in-game overlay to Nebulae's existing chat transport.</summary>
public class ChatManager : MonoBehaviour
{
    /// <summary>Ceiling on one message. The wire format already caps a string, but a chat line that
    /// long is a paste accident rather than a message, and it is dropped before it costs a frame.</summary>
    private const int MaxMessageLength = 256;

    /// <summary>Messages one player may send inside <see cref="RateLimitWindow"/> before the extras
    /// are dropped. Chat has no other throttle, so without this a stuck key or a scripted client can
    /// put an unbounded number of packets on the wire.</summary>
    private const int RateLimitMessages = 5;

    private const float RateLimitWindow = 1f;

    private static readonly float[] RecentSendTimes = new float[RateLimitMessages];
    private static int recentSendCursor;

    public static ChatManager Instance;
    public static RectTransform Overlay { get; private set; }
    private PreviewOverlayView view;

    private void Awake()
    {
        Instance = this;
        Config.OnConfigApplied += ApplyConfig;
        TryCreateView();
    }

    private void Update()
    {
        if (view == null) TryCreateView();
    }

    private void TryCreateView()
    {
        if (Multiplayer.IsDedicated || view != null || UIRoot.instance?.uiGame?.inventoryWindow == null) return;
        var parent = UIRoot.instance.uiGame.inventoryWindow.transform.parent;
        var root = new GameObject("Nebulae Overlay", typeof(RectTransform));
        root.transform.SetParent(parent, false);
        Overlay = root.GetComponent<RectTransform>();
        view = root.AddComponent<PreviewOverlayView>();
        view.OnMessageSubmitted += OnMessageSubmitted;
    }

    private void OnDestroy()
    {
        Config.OnConfigApplied -= ApplyConfig;
        if (view != null)
        {
            view.OnMessageSubmitted -= OnMessageSubmitted;
            Destroy(view.gameObject);
        }
        if (Instance == this) Instance = null;
        Overlay = null;
    }

    private static void ApplyConfig()
    {
        if (Instance?.view != null) Instance.view.RefreshLayout();
    }

    private static void OnMessageSubmitted(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || !Multiplayer.IsActive || Multiplayer.Session == null) return;
        if (input.Length > MaxMessageLength || !TryConsumeSendSlot())
        {
            // Dropped rather than truncated: a message that long, or that many in a second, is not
            // something the other players need to receive.
            ChatService.Instance.AddMessage(
                "Message dropped: too long, or too many messages in a short time.".Translate(),
                ChatMessageType.SystemWarnMessage);
            return;
        }
        var name = Multiplayer.Session.LocalPlayer?.Data?.Username ?? "Unknown";
        // The preview treats slash-prefixed text as ordinary chat, so bypass command parsing.
        ChatService.Instance.AddMessage(input, ChatMessageType.PlayerMessage, name);
        Multiplayer.Session.Network.SendPacket(new NewChatMessagePacket(ChatMessageType.PlayerMessage, input,
            DateTime.Now, name));
    }

    /// <summary>
    ///     Claim one of the send slots inside the current window, oldest first.
    /// </summary>
    private static bool TryConsumeSendSlot()
    {
        var now = Time.unscaledTime;
        var oldest = 0;
        for (var i = 1; i < RecentSendTimes.Length; i++)
        {
            if (RecentSendTimes[i] < RecentSendTimes[oldest])
            {
                oldest = i;
            }
        }
        if (now - RecentSendTimes[oldest] < RateLimitWindow)
        {
            return false;
        }
        RecentSendTimes[recentSendCursor] = now;
        recentSendCursor = (recentSendCursor + 1) % RecentSendTimes.Length;
        return true;
    }

    internal void SendChatMessage(string text, ChatMessageType messageType = ChatMessageType.SystemInfoMessage)
    {
        ChatService.Instance.AddMessage(text, messageType);
    }

    public void NotifyPlayerPresence(string name, bool joined)
    {
        if (view == null) TryCreateView();
        view?.AddPresence(name, joined);
    }

    public bool IsPointerIn() => view != null && view.IsPointerIn;
}
