using UnityEngine;

namespace NebulaeWorld.Combat;

internal static class FleetPose
{
    // Same distance rule as vanilla, evaluated in the pilot's environment on every peer.
    public static float Scale(SpaceSector sector, ref VectorLF3 position, global::Player player)
    {
        if (!Multiplayer.IsActive) return FleetComponent.CalculateFleetPoseScale(sector, ref position);
        var planet = player.planetData;
        if (planet != null)
            return Mathf.Clamp(((float)(planet.uPosition - position).magnitude - planet.realRadius) / 1000f, .2f, 1f);
        var star = player == GameMain.mainPlayer ? GameMain.localStar : null;
        var players = Multiplayer.Session.Combat?.Players;
        if (players != null)
            foreach (var view in players)
                if (view.mecha?.player == player) { star = GameMain.galaxy.StarById(view.starId); break; }
        return star == null ? 1f : Mathf.Clamp(((float)(star.uPosition - position).magnitude - sector.galaxyAstros[star.astroId].uRadius) / 1000f, .2f, 1f);
    }
}
