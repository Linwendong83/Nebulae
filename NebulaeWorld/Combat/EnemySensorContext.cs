using System;
using UnityEngine;

namespace NebulaeWorld.Combat;

/// <summary>Supplies a sensor's player input without changing shared fields on worker threads.</summary>
internal static class EnemySensorContext
{
    internal struct State
    {
        public bool Active, Selected;
        public CombatManager.PlayerPosition Player;
        public StarData Star;
        public VectorLF3 HivePosition;
    }
    [ThreadStatic] private static State current;

    public static State Begin(PlanetFactory factory, in EnemyData enemy)
    {
        var previous = current;
        current = new State { Active = Multiplayer.IsActive, Star = factory.planet.star };
        if (!current.Active) return previous;
        var players = Multiplayer.Session?.Combat?.Players;
        var distance = double.MaxValue;
        if (players != null)
            foreach (var player in players)
            {
                if (!player.isAlive || player.planetId != factory.planetId) continue;
                var delta = (VectorLF3)player.position - enemy.pos;
                if (delta.sqrMagnitude >= distance) continue;
                distance = delta.sqrMagnitude; current.Selected = true; current.Player = player;
            }
        return previous;
    }

    public static State Begin(SpaceSector sector, EnemyDFHiveSystem hive, in EnemyData enemy)
    {
        var previous = current;
        current = new State { Active = Multiplayer.IsActive, Star = hive.starData };
        if (!current.Active) return previous;
        var position = enemy.pos;
        if (enemy.astroId != 0) sector.TransformFromAstro(enemy.astroId, out position, enemy.pos);
        var players = Multiplayer.Session?.Combat?.Players;
        var distance = double.MaxValue;
        if (players != null)
            foreach (var player in players)
            {
                if (!player.isAlive || player.planetId > 0 || player.starId != hive.starData.id) continue;
                var delta = player.uPosition - position;
                if (delta.sqrMagnitude >= distance) continue;
                distance = delta.sqrMagnitude; current.Selected = true; current.Player = player;
            }
        if (current.Selected) sector.InverseTransformToAstro(hive.hiveAstroId, current.Player.uPosition, out current.HivePosition);
        return previous;
    }

    public static void Restore(State state) => current = state;
    public static bool HasPlayer() => !current.Active || current.Selected;
    public static int PlayerId() => current.Active ? current.Selected ? current.Player.id : 0 : 1;
    public static int PlayerTarget() => ((int)ETargetType.Player << 26) | PlayerId();
    public static ref Vector3 GroundPosition(EnemyDFGroundSystem system)
    {
        if (current.Active) return ref current.Player.position;
        return ref system.local_player_pos;
    }
    public static ref VectorLF3 UniversalPosition(global::Player player)
    {
        if (current.Active) return ref current.Player.uPosition;
        return ref player.uPosition;
    }
    public static ref VectorLF3 HivePosition(EnemyDFHiveSystem hive)
    {
        if (current.Active) return ref current.HivePosition;
        return ref hive.player_local_pos;
    }
    public static bool GroundAlive(EnemyDFGroundSystem system) => current.Active ? current.Selected : system.local_player_exist_alive;
    public static bool Grounded(EnemyDFGroundSystem system) => current.Active ? current.Selected && current.Player.planetId > 0 : system.local_player_grounded_alive;
    public static bool SpaceAlive(EnemyDFHiveSystem hive) => current.Active || hive.local_player_exist_alive;
    public static float ShieldRadius(SkillSystem skills) => current.Active ? current.Player.mecha?.energyShieldRadius ?? 0 : skills.playerEnergyShieldRadius;
    public static global::Player MainPlayer() => current.Active ? current.Player.mecha?.player : GameMain.mainPlayer;
    public static StarData LocalStar() => current.Active ? current.Star : GameMain.localStar;
    public static PlanetData LocalPlanet() => current.Active ? current.Player.planetId > 0 ? GameMain.galaxy?.PlanetById(current.Player.planetId) : null : GameMain.localPlanet;
    public static bool FastTravelling(global::Player player) => current.Active ? player?.controller?.actionSail?.fastTravelling ?? false : player.fastTravelling;
}
