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
    [DisplayName("Automatically send error reports")]
    [ConfigKey("AutoReportErrors")]
    public bool AutoReportErrors { get; set; } = true;

    [DisplayName("Server Password")]
    [UIContentType(InputField.ContentType.Password)]
    [ConfigKey("ServerPassword")]
    public string ServerPassword { get; set; } = string.Empty;

    [DisplayName("Server Description")]
    [ConfigKey("ServerDescription")]
    public string ServerDescription { get; set; } = string.Empty;

    [DisplayName("Host Port")]
    [UIRange(1, ushort.MaxValue)]
    [ConfigKey("HostPort")]
    public ushort HostPort { get; set; } = 8469;

    [DisplayName("Enable UPnp/Pmp Support")]
    [ConfigKey("EnableUPnpOrPmpSupport")]
    public bool EnableUPnpOrPmpSupport { get; set; } = false;

    [DisplayName("Cleanup inactive sessions")]
    [ConfigKey("CleanupInactiveSessions")]
    public bool CleanupInactiveSessions { get; set; } = false;

    [DisplayName("Chat Hotkey")]
    [ConfigKey("ChatHotkey")]
    public KeyboardShortcut ChatHotkey { get; set; } = new(KeyCode.BackQuote, KeyCode.LeftAlt);

    [DisplayName("Player List Hotkey")]
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

    [ConfigKey("AutoPauseEnabled")]
    public bool AutoPauseEnabled { get; set; } = true;


    public object Clone()
    {
        return MemberwiseClone();
    }
}
