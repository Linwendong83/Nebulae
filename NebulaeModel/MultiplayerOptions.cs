#region

using System;
using System.ComponentModel;
using BepInEx.Configuration;
using NebulaeModel.Attributes;
using UnityEngine;
using UnityEngine.UI;

#endregion

namespace NebulaeModel;

[Serializable]
internal class MultiplayerOptions : ICloneable
{
    [DisplayName("Server Password")]
    [Description("If provided, this will set a password for your hosted server.")]
    [UIContentType(InputField.ContentType.Password)]
    [ConfigKey("ServerPassword")]
    public string ServerPassword { get; set; } = string.Empty;

    [DisplayName("Server Description")]
    [Description("Custom text shown to players browsing the server list.")]
    [ConfigKey("ServerDescription")]
    public string ServerDescription { get; set; } = string.Empty;

    [DisplayName("Host Port")]
    [UIRange(1, ushort.MaxValue)]
    [ConfigKey("HostPort")]
    public ushort HostPort { get; set; } = 8469;

    [DisplayName("Enable UPnp/Pmp Support")]
    [Description(
        "If enabled, attempt to automatically create a port mapping using UPnp/Pmp (only works if your router has this feature and it is enabled)")]
    [ConfigKey("EnableUPnpOrPmpSupport")]
    public bool EnableUPnpOrPmpSupport { get; set; } = false;

    [DisplayName("Cleanup inactive sessions")]
    [Description(
        "If disabled the underlying networking library will not cleanup inactive connections. This might solve issues with clients randomly disconnecting and hosts having a 'System.ObjectDisposedException'.")]
    [ConfigKey("CleanupInactiveSessions")]
    public bool CleanupInactiveSessions { get; set; } = false;

    [DisplayName("Chat Hotkey")]
    [Description("Keyboard shortcut to toggle the chat window")]
    [ConfigKey("ChatHotkey")]
    public KeyboardShortcut ChatHotkey { get; set; } = new(KeyCode.BackQuote, KeyCode.LeftAlt);

    [DisplayName("Player List Hotkey")]
    [Description("Keyboard shortcut to display the Connected Players Window")]
    [ConfigKey("PlayerListHotkey")]
    public KeyboardShortcut PlayerListHotkey { get; set; } = new(KeyCode.BackQuote);


    // Detail function group buttons
    [ConfigKey("ShowDetailPowerGrid")]
    public bool ShowDetailPowerGrid { get; set; }
    [ConfigKey("ShowDetailVeinDistribution")]
    public bool ShowDetailVeinDistribution { get; set; }
    [ConfigKey("ShowDetailSpaceNavigation")]
    public bool ShowDetailSpaceNavigation { get; set; } = true;
    [ConfigKey("ShowDetailDefenseArea")]
    public bool ShowDetailDefenseArea { get; set; }
    [ConfigKey("ShowDetailBuildingAlarm")]
    public bool ShowDetailBuildingAlarm { get; set; } = true;
    [ConfigKey("ShowDetailBuildingIcon")]
    public bool ShowDetailBuildingIcon { get; set; } = true;
    [ConfigKey("ShowGuidingLight")]
    public bool ShowGuidingLight { get; set; } = true;
    [ConfigKey("ShowDetailHpBars")]
    public bool ShowDetailHpBars { get; set; } = true;

    [ConfigKey("RemoteAccessEnabled")]
    public bool RemoteAccessEnabled { get; set; } = false;
    [ConfigKey("RemoteAccessPassword")]
    public string RemoteAccessPassword { get; set; } = "";
    [ConfigKey("AutoPauseEnabled")]
    public bool AutoPauseEnabled { get; set; } = true;


    public object Clone()
    {
        return MemberwiseClone();
    }
}
