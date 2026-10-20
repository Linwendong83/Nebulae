using System;

namespace NebulaeWorld.Combat;

internal static class CombatTargetContext
{
    [ThreadStatic] public static Mecha TargetMecha;

    public static int TargetPlayerId()
    {
        if (!Multiplayer.IsActive) return 1;
        var mecha = TargetMecha ?? GameMain.spaceSector.skillSystem.mecha;
        return PlayerIdFor(mecha);
    }

    public static int PlayerIdFor(Mecha mecha)
    {
        if (!Multiplayer.IsActive) return 1;
        if (mecha == null) return 0;
        var session = Multiplayer.Session;
        if (!session.IsDedicated && mecha == GameMain.mainPlayer?.mecha) return session.LocalPlayer.Id;
        if (session.IsClient)
        {
            var players = session.Combat?.Players;
            if (players != null) foreach (var player in players) if (player.mecha == mecha) return player.id;
            return 0;
        }
        return session.CombatAuthority.PlayerIdFor(mecha);
    }

    public static bool IsPlayerTarget(int id)
    {
        if (!Multiplayer.IsActive) return id == 1;
        var session = Multiplayer.Session;
        if (!session.IsDedicated && id == session.LocalPlayer.Id) return true;
        if (session.IsClient) return session.Combat.IndexByPlayerId.TryGetValue(id, out var index) &&
            session.Combat.Players[index].mecha != null;
        return id > 0 && id <= ushort.MaxValue && session.CombatAuthority.MechaFor((ushort)id) != null;
    }

    public static Mecha ResolveDamageMecha(SkillSystem skills, int id)
    {
        if (!Multiplayer.IsActive) return skills.mecha;
        var session = Multiplayer.Session;
        if (!session.IsDedicated && id == session.LocalPlayer.Id) return GameMain.mainPlayer.mecha;
        if (session.IsClient)
        {
            var combat = session.Combat;
            return combat?.IndexByPlayerId != null && combat.IndexByPlayerId.TryGetValue(id, out var index) &&
                index >= 0 && index < combat.Players.Length ? combat.Players[index].mecha : null;
        }
        return session.CombatAuthority.MechaFor((ushort)id) ?? throw new InvalidOperationException("Unknown authoritative combat target.");
    }
}
