using System.Linq;
using NebulaeModel.Packets.Warning;

namespace NebulaeWorld.Warning;

internal partial class WarningManager
{
    public void ChangeGreenPrebuildWarning(int planetId, bool enable)
    {
        var data = GameMain.data;
        if (data?.preferences == null || !ValidGreenPrebuildWarningPlanet(data, planetId)) return;

        if (!Multiplayer.Session.LocalPlayer.IsHost)
        {
            Multiplayer.Session.Network.SendPacket(new GreenPrebuildWarningRequest { PlanetId = planetId, Enable = enable });
            return;
        }

        // These are world preferences. Every player's request changes the host's
        // native preferences, so the ordinary world save persists the result.
        if (planetId == 0)
        {
            data.preferences.SetAllPlanetsGreenPrebuildWarningEnabled(enable);
            for (var i = 0; i < data.factoryCount; i++)
                data.factories[i]?.RefreshGreenPrebuildWarning();
        }
        else
        {
            data.preferences.SetGreenPreBuildWarningEnabled(planetId, enable);
            data.galaxy.PlanetById(planetId)?.factory?.RefreshGreenPrebuildWarning();
        }
        Multiplayer.Session.Network.SendPacket(CaptureGreenPrebuildWarningPreferences());
        RefreshGreenPrebuildWarningPanel();
    }

    public GreenPrebuildWarningState CaptureGreenPrebuildWarningPreferences() => new()
    {
        PlanetIds = GameMain.data.preferences.greenPrebuildWarningPlanetIds.OrderBy(id => id).ToArray()
    };

    public void RestoreGreenPrebuildWarningPreferences(GreenPrebuildWarningState packet)
    {
        var data = GameMain.data;
        if (data?.preferences == null || packet.PlanetIds == null || data.galaxy == null ||
            packet.PlanetIds.Length > data.galaxy.stars.Length * 100 + 1 ||
            packet.PlanetIds.Any(id => !ValidGreenPrebuildWarningPlanet(data, id))) return;

        var ids = data.preferences.greenPrebuildWarningPlanetIds;
        ids.Clear();
        ids.UnionWith(packet.PlanetIds);
        // Client warning pools contain server snapshots, not local factory ids.
        // Updating preferences must never run the native warning refresh here.
        RefreshGreenPrebuildWarningPanel();
    }

    private static bool ValidGreenPrebuildWarningPlanet(GameData data, int id) =>
        id == 0 || (id > 0 && data.galaxy?.PlanetById(id) != null);

    private static void RefreshGreenPrebuildWarningPanel()
    {
        var panel = UIRoot.instance?.uiGame?.greenPrebuildWarning;
        if (panel != null && panel.active) panel.RefreshSwitches();
    }
}
