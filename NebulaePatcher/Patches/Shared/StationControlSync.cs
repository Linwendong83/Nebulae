using System;
using NebulaeModel.Packets.Logistics;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Shared;

/// <summary>
/// UIStationWindow and UIControlPanelStationInspector expose the same station controls under
/// different field names, and both patches used to carry a verbatim copy of the handling.
/// The shared handlers own the slider formatting and the packet write; each patch only hands
/// over its own widgets.
/// </summary>
internal static class StationControlSync
{
    internal static bool Locked(bool eventLock) =>
        eventLock || !Multiplayer.IsActive || Multiplayer.Session.Ships.PatchLockILS;

    internal static bool IsHost => Multiplayer.Session.LocalPlayer.IsHost;

    internal static bool IsClient => Multiplayer.Session.LocalPlayer.IsClient;

    internal static StationComponent Station(PlanetTransport transport, int stationId) => transport.stationPool[stationId];

    internal static void Send(PlanetFactory factory, PlanetTransport transport, int stationId,
        StationUI.EUISettings setting, double value) =>
        Send(factory, transport.stationPool[stationId], setting, value);

    internal static void Send(PlanetFactory factory, StationComponent station,
        StationUI.EUISettings setting, double value) =>
        Multiplayer.Session.Network.SendPacket(new StationUI(factory.planet.id, station.id, station.gid, setting, value));

    /// <summary>Records the slider value; the window sends it once the mouse button is released.</summary>
    private static void Record(StationUI packet, StationUI.EUISettings setting, float value)
    {
        packet.SettingIndex = setting;
        packet.SettingValue = value;
    }

    /// <summary>
    /// Transport icon click: send the new total, then roll the client back until the host confirms.
    /// </summary>
    internal static void IconCountChanged(PlanetFactory factory, PlanetTransport transport, int stationId,
        StationUI.EUISettings setting, int before, int idle, int work, Action revert)
    {
        if (before == idle) return;
        Send(factory, transport, stationId, setting, idle + work);
        if (!IsClient) return;
        revert();
    }

    internal static bool MaxChargePower(StationUI packet, bool eventLock, float value, Action<float> apply)
    {
        if (Locked(eventLock)) return true;
        Record(packet, StationUI.EUISettings.MaxChargePower, value);
        if (!IsClient) return IsHost;
        apply(value);
        return IsHost;
    }

    internal static bool MaxTripDrone(StationUI packet, bool eventLock, float value, Action<string> apply)
    {
        if (Locked(eventLock)) return true;
        Record(packet, StationUI.EUISettings.MaxTripDrones, value);
        if (!IsClient) return IsHost;
        apply(value.ToString("0 °"));
        return IsHost;
    }

    internal static bool MaxTripVessel(StationUI packet, bool eventLock, float value, Action<string> apply)
    {
        if (Locked(eventLock)) return true;
        Record(packet, StationUI.EUISettings.MaxTripVessel, value);
        if (!IsClient) return IsHost;
        var num = value switch
        {
            > 40.5f => 10000.0f,
            > 20.5f => value * 2f - 20f,
            _ => value
        };
        apply(num < 9999.0f ? num.ToString("0 ly") : "∞");
        return IsHost;
    }

    /// <summary>Both delivery sliders show the same 10%-granularity percentage and clamp at 1%.</summary>
    internal static bool DeliverPercent(StationUI packet, bool eventLock, StationUI.EUISettings setting,
        float value, Action<string> apply)
    {
        if (Locked(eventLock)) return true;
        Record(packet, setting, value);
        if (!IsClient) return IsHost;
        var num = (int)(value * 10f + 0.5f);
        apply((num < 1 ? 1 : num).ToString("0") + " %");
        return IsHost;
    }

    internal static bool MaxMiningSpeed(StationUI packet, bool eventLock, float value, Action<string> apply)
    {
        if (Locked(eventLock)) return true;
        Record(packet, StationUI.EUISettings.MaxMiningSpeed, value);
        if (!IsClient) return IsHost;
        var num = 10000 + (int)(value + 0.5f) * 1000;
        apply((num / 100).ToString("0") + " %");
        return IsHost;
    }

    internal static bool WarperDistance(StationUI packet, bool eventLock, float value,
        Action<float> clampSlider, Action<string> apply)
    {
        if (Locked(eventLock)) return true;
        // The game treats anything below 2 as 2 ly; put the slider back so the UI matches.
        if (value < 2f)
        {
            value = 2f;
            clampSlider(value);
        }
        Record(packet, StationUI.EUISettings.WarpDistance, value);
        if (!IsClient) return IsHost;
        float num;
        if (value < 1.5) num = 0.2f;
        else if (value < 7.5) num = value * 0.5f - 0.5f;
        else if (value < 16.5) num = value - 4f;
        else if (value < 20.5) num = value * 2f - 20f;
        else num = 60.0f;
        apply(num < 10.0 ? num.ToString("0.0 AU") : num.ToString("0 AU"));
        return IsHost;
    }
}
