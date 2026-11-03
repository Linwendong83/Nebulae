using System;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel;
using NebulaeModel.Networking;
using NebulaeNetwork;
using NebulaeNetwork.ServerList;
using NebulaeWorld;
using NebulaeWorld.MonoBehaviours.Local;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NebulaePatcher.UI;

// Server list rendering: probing, status text and row visuals.
internal static partial class MultiplayerPage
{
    private static void RefreshList()
    {
        foreach (var row in rows) Object.Destroy(row);
        rows.Clear();
        rowByServer.Clear();

        var servers = ServerMemoryStore.Instance.Servers;
        if (selectedId != null && ServerMemoryStore.Instance.FindServer(selectedId) == null) selectedId = null;
        hoveredId = null;

        foreach (var server in servers)
        {
            var id = server.Id;
            var row = new GameObject("server-" + id, typeof(RectTransform)).GetComponent<RectTransform>();
            row.SetParent(listContent, false);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 54;

            var widgets = new RowWidgets();
            var background = row.gameObject.AddComponent<Image>();
            background.color = RowNormalColor;
            widgets.Background = background;

            var accent = new GameObject("Selection accent", typeof(RectTransform)).GetComponent<RectTransform>();
            accent.SetParent(row, false);
            accent.anchorMin = new Vector2(0f, 0f);
            accent.anchorMax = new Vector2(0f, 1f);
            accent.pivot = new Vector2(0f, 0.5f);
            accent.anchoredPosition = Vector2.zero;
            accent.sizeDelta = new Vector2(3f, -10f);
            var accentImage = accent.gameObject.AddComponent<Image>();
            accentImage.color = SelectionAccentColor;
            accentImage.raycastTarget = false;
            accent.gameObject.SetActive(false);
            widgets.SelectionAccent = accentImage;

            var rowButton = row.gameObject.AddComponent<Button>();
            rowButton.transition = Selectable.Transition.None;
            rowButton.onClick = new Button.ButtonClickedEvent();
            rowButton.onClick.AddListener(new UnityAction(() => OnRowClicked(id)));
            row.gameObject.AddComponent<RowHoverHandler>().ServerId = id;

            var name = CloneText(row, server.Name, TextAnchor.MiddleLeft, 18);
            name.color = NameColor;
            AnchorColumn(name, new Vector2(0f, 0.45f), new Vector2(0.78f, 1f), new Vector2(20, 0), new Vector2(-8, -5));
            name.raycastTarget = false;

            var description = CloneText(row, string.Empty, TextAnchor.MiddleLeft, 14);
            description.color = DescriptionColor;
            AnchorColumn(description, new Vector2(0f, 0f), new Vector2(0.78f, 0.45f), new Vector2(20, 2), new Vector2(-8, 0));
            description.raycastTarget = false;
            widgets.Description = description;

            var players = CloneText(row, string.Empty, TextAnchor.MiddleCenter, 17);
            players.color = ValueColor;
            AnchorColumn(players, new Vector2(0.78f, 0f), new Vector2(0.855f, 1f), new Vector2(4, 0), new Vector2(-4, 0));
            players.raycastTarget = false;
            widgets.Players = players;

            // A small person glyph right after the count marks the column as the player count.
            // No game data asset is reliable in the main menu, so the glyph is drawn procedurally.
            // It tracks the number's right edge in ApplyRowStatus and only shows alongside it.
            var badge = NewRect("player badge", row, 0);
            badge.anchorMin = badge.anchorMax = new Vector2(0.78f, 0.5f);
            badge.pivot = new Vector2(0f, 0.5f);
            badge.anchoredPosition = new Vector2(0f, 0f);
            badge.sizeDelta = new Vector2(17f, 17f);
            var badgeImage = badge.gameObject.AddComponent<Image>();
            badgeImage.sprite = GetPlayerBadgeSprite();
            badgeImage.preserveAspect = true;
            badgeImage.raycastTarget = false;
            badgeImage.color = new Color(ValueColor.r, ValueColor.g, ValueColor.b, 0.9f);
            badge.gameObject.SetActive(false);
            widgets.Badge = badgeImage;

            var ping = CloneText(row, string.Empty, TextAnchor.MiddleCenter, 16);
            ping.color = DimColor;
            AnchorColumn(ping, new Vector2(0.89f, 0f), new Vector2(1f, 1f), new Vector2(4, 0), new Vector2(-14, 0));
            ping.raycastTarget = false;
            widgets.Ping = ping;

            rowByServer[id] = widgets;
            rows.Add(row.gameObject);
        }

        // Redraw from cached probe results so rebuilding the rows (selection, language
        // change, ...) does not lose the player/ping display.
        foreach (var pair in rowByServer)
        {
            UpdateRowVisual(pair.Key);
            if (probeResults.TryGetValue(pair.Key, out var stored)) ApplyRowStatus(pair.Value, stored);
            else ApplyCheckingStatus(pair.Value);
        }

        UpdateSelection();
    }

    private static void ProbeAll()
    {
        ServerStatusProbe.RefreshAll(ServerMemoryStore.Instance.Servers
            .Select(s => (s.Id, s.Address, s.Password)));
    }

    private static void OnRowClicked(string id)
    {
        if (selectedId != id)
        {
            selectedId = id;
            UpdateSelection();
        }

        var now = Time.unscaledTime;
        if (lastClickTime.TryGetValue(id, out var previous) && now - previous < 0.35f)
        {
            lastClickTime[id] = 0f;
            JoinSelected();
            return;
        }
        lastClickTime[id] = now;
    }

    private static void UpdateSelection()
    {
        foreach (var id in rowByServer.Keys) UpdateRowVisual(id);

        // Hidden rather than greyed out: with no selection the row actions have no
        // meaning and would only clutter the action bar.
        var hasSelection = Selected() != null;
        joinButton.gameObject.SetActive(hasSelection);
        editButton.gameObject.SetActive(hasSelection);
        deleteButton.gameObject.SetActive(hasSelection);
    }

    private static void DeselectAll()
    {
        if (selectedId == null) return;
        selectedId = null;
        UpdateSelection();
    }

    internal static void SetHovered(string id, bool hovered)
    {
        if (hovered)
        {
            if (hoveredId == id) return;
            var previous = hoveredId;
            hoveredId = id;
            UpdateRowVisual(id);
            if (previous != null && previous != id) UpdateRowVisual(previous);
        }
        else
        {
            if (hoveredId != id) return;
            hoveredId = null;
            UpdateRowVisual(id);
        }
    }

    private static void UpdateRowVisual(string id)
    {
        if (!rowByServer.TryGetValue(id, out var widgets) || widgets.Background == null) return;
        var selected = id == selectedId;
        widgets.Background.color = selected ? RowSelectedColor
            : id == hoveredId ? RowHoverColor
            : RowNormalColor;
        if (widgets.SelectionAccent != null) widgets.SelectionAccent.gameObject.SetActive(selected);
    }

    private static void ApplyCheckingStatus(RowWidgets widgets)
    {
        widgets.Players.text = "—";
        widgets.Players.color = DimColor;
        widgets.Ping.fontSize = 14;
        widgets.Ping.text = "Checking...".Translate();
        widgets.Ping.color = DimColor;
        widgets.Description.text = string.Empty;
        if (widgets.Badge != null) widgets.Badge.gameObject.SetActive(false);
    }

    private static void ApplyRowStatus(RowWidgets widgets, ServerProbeResult result)
    {
        if (result.State != ServerProbeState.Online)
        {
            widgets.Players.text = "—";
            widgets.Players.color = DimColor;
            widgets.Ping.fontSize = 14;
            widgets.Ping.text = StateText(result.State);
            widgets.Ping.color = StateColor(result.State);
            if (widgets.Badge != null) widgets.Badge.gameObject.SetActive(false);
            return;
        }

        widgets.Players.text = result.NumPlayers.ToString();
        widgets.Players.color = ValueColor;
        widgets.Ping.fontSize = 16;
        widgets.Ping.text = $"{result.PingMs} ms";
        widgets.Ping.color = result.PingMs <= 80 ? FastPingColor
            : result.PingMs <= 160 ? MediumPingColor
            : SlowPingColor;

        if (widgets.Badge != null)
        {
            // Centered text of unknown width: park the badge just past the number's right edge.
            // The text column carries a 4px left inset (AnchorColumn offsetMin.x), so 11 here
            // leaves a real 7px breathing gap between the number and the badge.
            var playersRect = widgets.Players.rectTransform;
            var columnWidth = playersRect.rect.width;
            var textWidth = Mathf.Min(widgets.Players.preferredWidth, columnWidth);
            var badgeRect = (RectTransform)widgets.Badge.transform;
            badgeRect.anchoredPosition = new Vector2(columnWidth / 2f + textWidth / 2f + 11f, 0f);
            widgets.Badge.gameObject.SetActive(true);
        }

        if (!NebulaeVersion.IsKnownStatusVersion(result.NebulaeVersion) ||
                 !NebulaeVersion.IsKnownStatusVersion(Config.ModVersion))
        {
            widgets.Description.text = "Unable to determine mod version".Translate();
            widgets.Description.color = WarningColor;
        }
        else if (!NebulaeVersion.IsCompatible(result.NebulaeVersion, Config.ModVersion))
        {
            widgets.Description.text = string.Format("Mod version mismatch: Server {0}".Translate(),
                NebulaeVersion.Normalize(result.NebulaeVersion));
            widgets.Description.color = ErrorColor;
        }
        else
        {
            widgets.Description.text = string.IsNullOrWhiteSpace(result.Description) ? string.Empty : result.Description;
            widgets.Description.color = DescriptionColor;
        }
    }

    private static string StateText(ServerProbeState state)
    {
        return state switch
        {
            ServerProbeState.Unreachable => "Unreachable".Translate(),
            ServerProbeState.Starting => "Starting...".Translate(),
            ServerProbeState.PasswordRequired => "Password Required".Translate(),
            ServerProbeState.WrongPassword => "Wrong Password".Translate(),
            _ => "Checking...".Translate()
        };
    }
}
