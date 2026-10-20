using System;
using System.Collections.Generic;
using UnityEngine;

namespace NebulaeWorld.Combat;

/// <summary>Thread-local player views for the two native sweep collision blocks.</summary>
internal static class SweepPlayerContext
{
    internal sealed class State
    {
        public List<CombatManager.PlayerPosition> Players;
        public int Index;
        public Vector3 LocalTarget;
        public VectorLF3 UniversalTarget;
        public ColliderData Collider;
    }
    [ThreadStatic] public static State Current;

    public static bool Begin(SkillSystem skills, ref SpaceLaserSweep sweep)
    {
        if (!Multiplayer.IsActive) { Current = null; return true; }
        var players = new List<CombatManager.PlayerPosition>();
        var star = sweep.astroId > 1000000 ? skills.sector.GetHiveByAstroId(sweep.astroId)?.starData.id ?? 0 : sweep.astroId / 100;
        foreach (var player in Multiplayer.Session.Combat.Players)
            if (player.isAlive && player.mecha != null && CombatTargetContext.IsPlayerTarget(player.id) &&
                (star == 0 || player.starId == star)) players.Add(player);
        skills.sector.TransformFromAstro(sweep.astroId, out var begin, sweep.beginPos);
        players.Sort((a, b) => (a.uPosition - begin).sqrMagnitude.CompareTo((b.uPosition - begin).sqrMagnitude));
        Current = new State { Players = players, Index = -1 };
        return Next();
    }
    public static bool Next()
    {
        if (Current == null) return false;
        if (++Current.Index >= Current.Players.Count) { Current = null; CombatTargetContext.TargetMecha = null; return false; }
        var mecha = Current.Players[Current.Index].mecha;
        Current.LocalTarget = mecha.skillTargetLCenter;
        Current.UniversalTarget = mecha.skillTargetUCenter;
        Current.Collider = mecha.skillColliderL;
        CombatTargetContext.TargetMecha = mecha;
        return true;
    }
    public static int PlayerId() => Current == null ? 1 : Current.Players[Current.Index].id;
    public static Mecha Mecha(SkillSystem skills) => Current == null ? skills.mecha : Current.Players[Current.Index].mecha;
    public static int Planet(SkillSystem skills) => Current == null ? skills.localPlanetAstroId : Math.Max(0, Current.Players[Current.Index].planetId);
    public static float ShieldRadius(SkillSystem skills) => Current == null ? skills.playerEnergyShieldRadius :
        Mecha(skills).energyShieldRadius * Mecha(skills).energyShieldRadiusMultiplier;
    public static ref Vector3 LocalTarget(SkillSystem skills)
    { if (Current == null) return ref skills.playerSkillTargetL; return ref Current.LocalTarget; }
    public static ref VectorLF3 UniversalTarget(SkillSystem skills)
    { if (Current == null) return ref skills.playerSkillTargetU; return ref Current.UniversalTarget; }
    public static ref ColliderData Collider(SkillSystem skills)
    { if (Current == null) return ref skills.playerSkillColliderL; return ref Current.Collider; }
}
