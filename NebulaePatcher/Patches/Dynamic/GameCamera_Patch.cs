#region

using HarmonyLib;
using NebulaeModel.DataStructures.Chat;
using NebulaeWorld;
using NebulaeWorld.MonoBehaviours.Local.Chat;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(GameCamera))]
internal class GameCamera_Patch
{

    [HarmonyPostfix]
    [HarmonyPatch(nameof(GameCamera.Logic))]
    public static void Logic_Postfix(GameCamera __instance)
    {
        if (!Multiplayer.IsActive) return;

        var observingPlayerId = Multiplayer.Session.Gizmos.ObservingPlayerId;
        var observingPlanetId = Multiplayer.Session.Gizmos.ObservingPlanetId;
        if (observingPlayerId == 0 && observingPlanetId == 0) return;

        if (VFInput.escape)
        {
            Multiplayer.Session.Gizmos.ObservingPlayerId = 0;
            Multiplayer.Session.Gizmos.ObservingPlanetId = 0;
            VFInput.UseEscape();
            return;
        }

        if (observingPlayerId > 0)
        {
            TrackingPlayer(__instance, observingPlayerId);
        }
        if (observingPlanetId > 0)
        {
            TrakcingPlanetPos(__instance, observingPlanetId, Multiplayer.Session.Gizmos.ObservingPos);
        }
    }

    private static void TrackingPlayer(GameCamera cam, ushort playerId)
    {
        using (Multiplayer.Session.World.GetRemotePlayersModels(out var models))
        {
            if (!models.TryGetValue(playerId, out var model))
            {
                StopTrackingPlayer(string.Format("Can't find player {0}".Translate(), playerId));
                return;
            }
            var targetName = string.IsNullOrEmpty(model?.Movement?.Username) ? playerId.ToString() : model.Movement.Username;
            var planetId = model.Movement.localPlanetId;
            if (planetId > 0 && planetId != GameMain.mainPlayer.planetId)
            {
                StopTrackingPlayer(string.Format("Player {0} is on a different planet".Translate(), targetName));
                return;
            }
            var starId = model.Movement.LocalStarId;
            if (planetId == 0 && (starId <= 0 || starId != GameMain.localStar?.id))
            {
                StopTrackingPlayer(string.Format("Player {0} is too far away".Translate(), targetName));
                return;
            }
            if (GameMain.mainPlayer.planetId > 0)
            {
                if (planetId == 0 && Multiplayer.Session.IsClient)
                {
                    StopTrackingPlayer(string.Format("Player {0} is too far away".Translate(), targetName));
                    return;
                }
                // The local player is on the planet and viewing another player on the same planet or in space
                cam.rtsTarget.position = model.PlayerTransform.position;
                cam.rtsTarget.eulerAngles = Maths.SphericalRotation(cam.rtsTarget.position, 0f).eulerAngles;
            }
            else
            {
                // Both players are in space
                StopTrackingPlayer(string.Format("Player {0} is too far away".Translate(), targetName));
            }
        }
    }

    private static void StopTrackingPlayer(string text)
    {
        ChatManager.Instance.SendChatMessage(text, ChatMessageType.CommandOutputMessage);
        Multiplayer.Session.Gizmos.ObservingPlayerId = 0;
    }

    private static void TrakcingPlanetPos(GameCamera cam, int planetId, Vector3 pos)
    {
        if (planetId > 0 && planetId != GameMain.mainPlayer.planetId)
        {
            var planet = GameMain.galaxy.PlanetById(planetId);
            if (planet != null)
            {
                UIRoot.instance.uiGame.OpenStarmap();
                var map = UIRoot.instance.uiGame.starmap;
                var starIdx = planetId / 100 - 1;
                map.focusStar = map.starUIs[starIdx];
                map.focusPlanet = null;
                map.OnCursorFunction2Click(0);
                map.screenCameraController.SetViewTarget(planet, null, null, null, VectorLF3.zero,
                    planet.realRadius * 0.00025 * 6.0, planet.realRadius * 0.00025 * 160.0, true, false);
            }
            Multiplayer.Session.Gizmos.ObservingPlanetId = 0;
            return;
        }
        cam.rtsTarget.position = pos;
        cam.rtsTarget.eulerAngles = Maths.SphericalRotation(pos, 0f).eulerAngles;

    }
}
