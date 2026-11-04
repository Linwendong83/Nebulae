#region

using NebulaeWorld.MonoBehaviours.Local.Chat;
using UnityEngine;

#endregion

namespace NebulaeWorld.Chat.ChatLinks;

internal class NavigateChatLinkHandler : IChatLinkHandler
{
    const char SplitSeparator = '\t';

    public void OnClick(string data)
    {
        var substrings = data.Split(SplitSeparator);
        switch (substrings.Length)
        {
            case 2: // PlayerId
                if (!ushort.TryParse(substrings[0], out var playerId)) return;
                Multiplayer.Session.Gizmos.SetIndicatorPlayerId(playerId);
                break;

            case 3: // AstroId
                if (!int.TryParse(substrings[0], out var astroId)) return;
                if (astroId < 0) return;

                // Clear the guide line if choosing the same target
                var previousAstro = GameMain.mainPlayer.navigation.indicatorAstroId;
                GameMain.mainPlayer.navigation.indicatorAstroId = previousAstro != astroId ? astroId : 0;
                break;

            case 5: // PlanetPos
                if (!TryParsePlanetPos(substrings, out var planetId, out var pos)) return;
                Multiplayer.Session.Gizmos.SetIndicatorPing(planetId, pos);
                break;
        }
    }

    public void OnRightClick(string data)
    {
        var gizmoManager = Multiplayer.Session.Gizmos;
        var substrings = data.Split(SplitSeparator);
        switch (substrings.Length)
        {
            case 2: // PlayerId
                if (!ushort.TryParse(substrings[0], out var playerId)) return;
                if (gizmoManager.ObservingPlayerId == playerId)
                {
                    gizmoManager.ObservingPlayerId = 0;
                    return;
                }
                gizmoManager.ObservingPlayerId = playerId;
                break;

            case 3: // AstroId
                if (!int.TryParse(substrings[0], out var astorId)) return;
                if (astorId <= 0) return;

                // Open the starmap and focus on the target
                UIRoot.instance.uiGame.OpenStarmap();
                var starmap = UIRoot.instance.uiGame.starmap;
                var planet = GameMain.galaxy.PlanetById(astorId);
                var star = GameMain.galaxy.StarById(astorId / 100);
                if (planet != null)
                {
                    var starIdx = astorId / 100 - 1;
                    starmap.focusStar = starmap.starUIs[starIdx];
                    starmap.focusPlanet = null;
                    starmap.OnCursorFunction2Click(0);
                    starmap.screenCameraController.SetViewTarget(planet, null, null, null, VectorLF3.zero,
                        planet.realRadius * 0.00025 * 6.0, planet.realRadius * 0.00025 * 160.0, true, false);
                }
                else if (star != null)
                {
                    var starIdx = astorId / 100 - 1;
                    starmap.focusPlanet = null;
                    starmap.focusStar = starmap.starUIs[starIdx];
                    starmap.OnCursorFunction2Click(0);
                }
                return;

            case 5: // PlanetPos
                if (!TryParsePlanetPos(substrings, out var planetId, out var pos)) return;
                if (gizmoManager.ObservingPlanetId == planetId && gizmoManager.ObservingPos == pos)
                {
                    gizmoManager.ObservingPlanetId = 0;
                    return;
                }
                gizmoManager.ObservingPlanetId = planetId;
                gizmoManager.ObservingPos = pos;
                if (GameCamera.instance.planetMode && planetId == GameMain.localPlanet?.id)
                {
                    // In planet view, move the camera to aim at the target
                    var toRotate = Quaternion.FromToRotation(Vector3.up, pos);
                    var vector = toRotate * Vector3.up;
                    var normalized = Vector3.Cross(vector, Vector3.up).normalized;
                    toRotate = Quaternion.LookRotation(Vector3.Cross(normalized, vector), vector);
                    GameCamera.instance.planetPoser.rotationWanted = toRotate;
                }
                break;
        }
    }

    public void OnHover(string data, ChatLinkTrigger trigger, ref MonoBehaviour tipObject) =>
        ChatLinkTooltip.Show(data, trigger, ref tipObject, "Navigate",
            "Left click to create a navigate line to the target.\nRight click to track the target.");

    private static bool TryParsePlanetPos(string[] substrings, out int planetId, out Vector3 pos)
    {
        planetId = 0;
        pos = Vector3.zero;
        if (substrings.Length != 5) return false;
        if (!int.TryParse(substrings[0], out planetId)) return false;
        if (!float.TryParse(substrings[1], out var x)) return false;
        if (!float.TryParse(substrings[2], out var y)) return false;
        if (!float.TryParse(substrings[3], out var z)) return false;
        pos = new Vector3(x, y, z);
        return true;
    }
}
