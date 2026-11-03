using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace NebulaeNetwork.PacketProcessors.Logistics.ControlPanel;

/// <summary>
/// Control-panel rows share one wire shape for the power readout, and every processor
/// used to re-implement vanilla's power block verbatim. Keeping the mapping in one place
/// stops the three row types from drifting apart.
/// </summary>
internal static class ControlPanelEntryUpdate
{
    internal static T Find<T>(int index, int astroId, int objId) where T : UIControlPanelObjectEntry
    {
        var pool = UIRoot.instance.uiGame.controlPanelWindow.objectEntryPool;
        for (var i = 0; i < pool.Count; i++)
        {
            var candidate = pool[i];
            if (candidate != null && candidate.index == index && candidate.target.astroId == astroId && candidate.target.objId == objId)
            {
                return candidate as T;
            }
        }
        return null;
    }

    /// <summary>Mirrors the power block of UIControlPanel*Entry._OnUpdate from the given snapshot.</summary>
    internal static void ApplyPower(UIControlPanelObjectEntry entry, in PowerReadout readout,
        float consumerRatio, long requirePower, long workEnergyPerTick, float powerRound)
    {
        var status = consumerRatio >= 1f ? 1 : consumerRatio >= 0.1f ? 2 : 3;
        readout.CircleFg.fillAmount = consumerRatio >= 1f ? 1f : consumerRatio;
        StringBuilderUtility.WriteKMG1000(readout.PowerSb, 8, requirePower, true);
        StringBuilderUtility.WriteKMG1000(readout.MaxChargeSb, 8, workEnergyPerTick * 60L, true);
        readout.PowerText.text = readout.PowerSb.ToString();
        readout.MaxChargeText.text = readout.MaxChargeSb.ToString();
        readout.RoundFg.fillAmount = powerRound;

        var window = entry.masterWindow;
        readout.Sign.color = SignColor(window, status);
        readout.CircleBg.color = CircleBgColor(window, status);
        readout.CircleFg.color = CircleFgColor(window, status);
        readout.RoundFg.color = RoundFgColor(window, status);
        readout.PowerText.color = TextColor(window, status);
        readout.MaxChargeText.color = TextColor(window, status);
    }

    private static Color SignColor(UIControlPanelWindow window, int status) => status switch
    {
        1 => window.powerSignColor1,
        2 => window.powerSignColor2,
        3 => window.powerSignColor3,
        _ => window.powerSignColor0
    };

    private static Color CircleBgColor(UIControlPanelWindow window, int status) => status switch
    {
        1 => window.powerCircleBgColor1,
        2 => window.powerCircleBgColor2,
        3 => window.powerCircleBgColor3,
        _ => window.powerCircleBgColor0
    };

    private static Color CircleFgColor(UIControlPanelWindow window, int status) => status switch
    {
        1 => window.powerCircleFgColor1,
        2 => window.powerCircleFgColor2,
        3 => window.powerCircleFgColor3,
        _ => window.powerCircleFgColor0
    };

    private static Color RoundFgColor(UIControlPanelWindow window, int status) => status switch
    {
        1 => window.powerRoundFgColor1,
        2 => window.powerRoundFgColor2,
        3 => window.powerRoundFgColor3,
        _ => window.powerRoundFgColor0
    };

    private static Color TextColor(UIControlPanelWindow window, int status) => status switch
    {
        1 => window.powerTextColor1,
        2 => window.powerTextColor2,
        3 => window.powerTextColor3,
        _ => window.powerTextColor0
    };
}

/// <summary>The widgets a control-panel row exposes for the shared power readout.</summary>
internal readonly struct PowerReadout
{
    internal readonly Image Sign;
    internal readonly Image CircleBg;
    internal readonly Image CircleFg;
    internal readonly Image RoundFg;
    internal readonly Text PowerText;
    internal readonly Text MaxChargeText;
    internal readonly StringBuilder PowerSb;
    internal readonly StringBuilder MaxChargeSb;

    internal PowerReadout(Image sign, Image circleBg, Image circleFg, Image roundFg,
        Text powerText, Text maxChargeText, StringBuilder powerSb, StringBuilder maxChargeSb)
    {
        Sign = sign;
        CircleBg = circleBg;
        CircleFg = circleFg;
        RoundFg = roundFg;
        PowerText = powerText;
        MaxChargeText = maxChargeText;
        PowerSb = powerSb;
        MaxChargeSb = maxChargeSb;
    }
}
