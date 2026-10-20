using NebulaeModel.DataStructures;

namespace NebulaeWorld.Player;

internal static class SpawnManager
{
    public static void SetBirthPoint(PlayerData data)
    {
        var planet = GameMain.galaxy.PlanetById(GameMain.galaxy.birthPlanetId);
        var point = planet.birthPoint;
        var upos = planet.uPosition + (VectorLF3)(planet.runtimeRotation * point);
        data.LocalPlanetId = planet.id;
        data.LocalStarId = planet.star.id;
        data.LocalPlanetPosition = new NebulaeModel.DataStructures.Float3(point);
        data.UPosition = new NebulaeModel.DataStructures.Double3(upos.x, upos.y, upos.z);
        data.Rotation = new NebulaeModel.DataStructures.Float3(Maths.SphericalRotation(point, 0f).eulerAngles);
        // A spawning mecha has no local rotation on its Model child, so the model's world euler
        // angles equal the root's. Seeding BodyRotation keeps the seat's pose complete for the
        // rejoin restore and the host-side combat simulation before the first movement packet.
        data.BodyRotation = data.Rotation;
    }
}
