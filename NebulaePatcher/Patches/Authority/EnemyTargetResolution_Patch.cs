#region

using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Combat;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Authority;

/// <summary>
/// Resolves Player hatred targets against the session player table instead of the local player.
/// </summary>
/// <remarks>
/// <para>
/// Vanilla answers "where is my target" for a Player hatred with the local player's position and
/// only when the packed id is 1, so a unit the host pointed at another player — and every unit on a
/// client, where the host's player is remote — reads as targetless and walks back to its formation
/// (see <see cref="EnemyPlayerTarget"/>). Both entry points are patched because both are read by the
/// engage behaviors every tick.
/// </para>
/// <para>
/// Single-player and non-Player targets use vanilla resolution. In multiplayer the session table
/// owns both success and failure, so an absent player cannot fall back to vanilla's local player 1.
/// </para>
/// </remarks>
[HarmonyPatch(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.GetTargetPosition_Ground))]
internal static class EnemyGroundTargetResolution_Patch
{
    [HarmonyPrefix]
    private static bool Prefix(PlanetFactory factory, int idType, out Vector3 target, out float territory,
        ref bool __result)
    {
        target = default;
        territory = 0f;
        if (!Multiplayer.IsActive || !EnemyPlayerTarget.IsPlayer(idType))
        {
            return true;
        }
        __result = EnemyPlayerTarget.TryResolveGround(Multiplayer.Session.Combat?.Players, idType,
            factory?.planetId ?? 0, out target, out territory);
        return false;
    }
}

/// <summary>Space counterpart of <see cref="EnemyGroundTargetResolution_Patch"/>.</summary>
[HarmonyPatch(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.GetTargetPosition_Space))]
internal static class EnemySpaceTargetResolution_Patch
{
    [HarmonyPrefix]
    private static bool Prefix(int idType, out VectorLF3 target, out float territory,
        ref bool __result)
    {
        target = default;
        territory = 0f;
        if (!Multiplayer.IsActive || !EnemyPlayerTarget.IsPlayer(idType))
        {
            return true;
        }
        __result = EnemyPlayerTarget.TryResolveSpace(Multiplayer.Session.Combat?.Players, idType,
            out target, out territory);
        return false;
    }
}
