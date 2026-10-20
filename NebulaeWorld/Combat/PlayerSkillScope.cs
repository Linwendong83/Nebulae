using UnityEngine;

namespace NebulaeWorld.Combat;

/// <summary>Restore vanilla's shared player view after one projectile, including exceptional exits.</summary>
internal readonly struct PlayerSkillScope : IScopeFrame
{
    private readonly SkillSystem skills;
    private readonly Mecha mecha;
    private readonly Mecha target;
    private readonly bool alive, sailing, warping;
    private readonly int astro, planet;
    private readonly Vector3 localTarget, castLeftL, castRightL, velocityU, velocityL;
    private readonly VectorLF3 universalTarget, lastTarget, castLeftU, castRightU;
    private readonly ColliderData colliderL;
    private readonly ColliderDataLF colliderU;
    private readonly float radius, altitude;

    public PlayerSkillScope(SkillSystem value)
    {
        this = default;
        target = CombatTargetContext.TargetMecha;
        if (!Multiplayer.IsActive) return;
        skills = value; mecha = value.mecha;
        alive = value.playerAlive; sailing = value.playerIsSailing; warping = value.playerIsWarping;
        astro = value.playerAstroId; planet = value.localPlanetAstroId;
        localTarget = value.playerSkillTargetL; universalTarget = value.playerSkillTargetU; lastTarget = value.playerSkillTargetULast;
        castLeftL = value.playerSkillCastLeftL; castRightL = value.playerSkillCastRightL;
        castLeftU = value.playerSkillCastLeftU; castRightU = value.playerSkillCastRightU;
        colliderL = value.playerSkillColliderL; colliderU = value.playerSkillColliderU;
        radius = value.playerEnergyShieldRadius; altitude = value.playerAltL; velocityU = value.playerVelocityU;
        velocityL = value.playerVelocityL;
    }

    public void Restore()
    {
        CombatTargetContext.TargetMecha = target;
        if (skills == null) return;
        skills.mecha = mecha; skills.playerAlive = alive; skills.playerIsSailing = sailing; skills.playerIsWarping = warping;
        skills.playerAstroId = astro; skills.localPlanetAstroId = planet;
        skills.playerSkillTargetL = localTarget; skills.playerSkillTargetU = universalTarget; skills.playerSkillTargetULast = lastTarget;
        skills.playerSkillCastLeftL = castLeftL; skills.playerSkillCastRightL = castRightL;
        skills.playerSkillCastLeftU = castLeftU; skills.playerSkillCastRightU = castRightU;
        skills.playerSkillColliderL = colliderL; skills.playerSkillColliderU = colliderU;
        skills.playerEnergyShieldRadius = radius; skills.playerAltL = altitude; skills.playerVelocityU = velocityU;
        skills.playerVelocityL = velocityL;
    }
}
