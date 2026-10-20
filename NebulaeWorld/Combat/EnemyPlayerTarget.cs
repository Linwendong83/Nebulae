using UnityEngine;

namespace NebulaeWorld.Combat;

/// <summary>
/// Resolves a packed <c>ETargetType.Player</c> hatred target against the session's player table.
/// </summary>
/// <remarks>
/// <para>
/// Vanilla resolves a Player target against exactly one player — the local one
/// (<c>SkillSystem.playerSkillTargetL</c> on the ground, <c>GameMain.mainPlayer</c> in space) — and
/// only when the packed id is literally 1. A unit whose host-selected target is any other player,
/// and every client machine (where the host's player is not the local player at all), therefore
/// resolves to nothing: the engage behaviors read that as "no target" and fall into
/// <c>RunBehavior_Engage_EmptyHatred</c>, which clears the hatred and sends the unit back to its
/// formation. That is why a monster charging a player on the host walks home on every client, and
/// why the two machines' trajectories diverge by the whole distance to the formation.
/// </para>
/// <para>
/// The session already maintains the whole table (<see cref="CombatManager.Players"/>: one entry per
/// connected player, refreshed every logic frame on every machine), so resolution is a lookup by id
/// rather than an assumption. A ground unit only ever resolves a player standing on its own planet:
/// the patched sensor can only hate such a player in the first place
/// (<see cref="EnemySensorContext.Begin(PlanetFactory, in EnemyData)"/> filters by planet), so a
/// cross-planet id is stale data and stays unresolvable — the same verdict vanilla gives an absent
/// target. In space any alive player resolves, matching vanilla's own isAlive-only check.
/// </para>
/// <para>
/// Positions come from <see cref="CombatManager.PlayerPosition.position"/> /
/// <see cref="CombatManager.PlayerPosition.uPosition"/> and the shield radius from the mecha, which
/// is the same source the patched sensor already reads through
/// <see cref="EnemySensorContext.GroundPosition"/> and <see cref="EnemySensorContext.ShieldRadius"/>.
/// The array reference is captured once per call, so a worker thread reading it while the main
/// thread installs the next frame's table sees either the old or the new table, never a torn one.
/// </para>
/// </remarks>
internal static class EnemyPlayerTarget
{
    private const int IdMask = 0x3FFFFFF;

    /// <summary>Whether a packed hatred/assault target names a player at all.</summary>
    public static bool IsPlayer(int idType) => (idType >> 26) == (int)ETargetType.Player;

    /// <summary>The player id inside a packed target, without its type bits.</summary>
    public static int IdOf(int idType) => idType & IdMask;

    /// <summary>
    /// Resolves a Player target for a ground unit, or reports it unresolvable.
    /// </summary>
    public static bool TryResolveGround(CombatManager.PlayerPosition[] players, int idType, int planetId,
        out Vector3 target, out float territory)
    {
        target = default;
        territory = 0f;
        if (!Find(players, IdOf(idType), out var player) || !player.isAlive) return false;
        // A ground unit cannot reach a player on another planet; the sensor never hates one either.
        if (player.planetId != planetId) return false;
        target = player.position;
        territory = player.mecha?.energyShieldRadius ?? 0f;
        return true;
    }

    /// <summary>
    /// Resolves a Player target for a space unit, or reports it unresolvable.
    /// </summary>
    public static bool TryResolveSpace(CombatManager.PlayerPosition[] players, int idType,
        out VectorLF3 target, out float territory)
    {
        target = default;
        territory = 0f;
        if (!Find(players, IdOf(idType), out var player) || !player.isAlive) return false;
        target = player.uPosition;
        territory = player.mecha?.energyShieldRadius ?? 0f;
        return true;
    }

    private static bool Find(CombatManager.PlayerPosition[] players, int id, out CombatManager.PlayerPosition player)
    {
        player = default;
        if (players == null || id <= 0) return false;
        var combat = Multiplayer.Session?.Combat;
        if (ReferenceEquals(combat?.Players, players) && combat.IndexByPlayerId != null &&
            combat.IndexByPlayerId.TryGetValue(id, out var index) && index >= 0 && index < players.Length && players[index].id == id)
        { player = players[index]; return true; }
        foreach (var candidate in players)
        {
            if (candidate.id != id) continue;
            player = candidate;
            return true;
        }
        return false;
    }
}
